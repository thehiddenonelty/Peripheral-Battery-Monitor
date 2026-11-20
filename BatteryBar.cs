using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.VisualStyles;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace BatteryBar
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Contains("--self-test")) return ProtocolChecks.Run();
            if (args.Contains("--diagnose")) return BatteryDiagnostics.Run();
            bool demo = args.Contains("--demo");
            bool created;
            using (var instance = new Mutex(true, @"Local\BatteryBar", out created))
            {
                if (!created)
                {
                    MessageBox.Show("已有 BatteryBar 版本在运行。请先从右下角托盘退出旧版本，再启动 Device Edition。", "外设电量");
                    return 0;
                }
                Native.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try
                {
                    using (var app = new BatteryApplication(demo)) Application.Run(app);
                }
                finally { Typography.Release(); }
            }
            return 0;
        }

    }

    internal sealed class BatteryApplication : ApplicationContext
    {
        private readonly bool demo;
        private readonly Preferences preferences;
        private readonly NotifyIcon tray;
        private readonly Icon trayIcon;
        private readonly ContextMenuStrip menu;
        private readonly Bar bar;
        private readonly Detail panel;
        private readonly Dashboard dashboard;
        private readonly Timer auto = new Timer();
        private readonly Timer deviceChange = new Timer { Interval = 1150 };
        private readonly Timer layout = new Timer { Interval = 650 };
        private readonly Timer click = new Timer { Interval = SystemInformation.DoubleClickTime + 25 };
        private readonly ToolStripMenuItem refreshItem;
        private Reading[] readings = new Reading[0];
        private Reading[] summary = new Reading[0];
        private bool refreshing, closing, openingMenu, inspecting;
        private bool pendingTray;
        private Point pendingPoint;
        private int pendingTick;
        private DateTime? lastRefresh;
        private DateTime lastScanRequested;
        private TaskbarLayout dock;
        private Size scannedBarSize;

        internal BatteryApplication(bool demo)
        {
            this.demo = demo;
            preferences = new Preferences(demo);
            Art.LowLevel = preferences.LowLevel;
            menu = new ContextMenuStrip();
            menu.Items.Add("打开电量卡片", null, delegate { OpenPanel(false); });
            menu.Items.Add("电量与设置", null, delegate { OpenPanel(true); });
            refreshItem = (ToolStripMenuItem)menu.Items.Add("立即刷新", null, delegate { Refresh(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            menu.Opening += delegate { openingMenu = true; click.Stop(); };
            menu.Closed += delegate { openingMenu = false; };

            trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray = new NotifyIcon { Text = "BatteryBar Device Edition", Icon = trayIcon ?? SystemIcons.Information, ContextMenuStrip = menu, Visible = true };
            tray.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) LeftClick(true);
            };
            bar = new Bar(demo, () => summary);
            panel = new Detail(demo, () => summary, Refresh, () => OpenPanel(true));
            dashboard = new Dashboard(demo, preferences, () => readings, Refresh, ApplyPreferences, PinDevices);
            bar.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) LeftClick(false);
                if (e.Button == MouseButtons.Right) menu.Show(Cursor.Position);
            };
            click.Tick += delegate { click.Stop(); OpenPanel(false); };
            bar.DevicesChanged += delegate
            {
                if (demo || closing) return;
                deviceChange.Stop();
                deviceChange.Start();
            };
            deviceChange.Tick += delegate
            {
                if (refreshing) return;
                deviceChange.Stop();
                Refresh();
            };
            auto.Interval = preferences.Interval * 60000;
            auto.Tick += delegate { Refresh(); };
            layout.Tick += delegate { PositionBar(); };
            PositionBar();
            auto.Start();
            layout.Start();
            SystemEvents.PowerModeChanged += PowerChanged;
            Refresh();
            if (demo) OpenPanel(false);
        }

        private void LeftClick(bool fromTray)
        {
            Point point = Cursor.Position;
            Size limit = SystemInformation.DoubleClickSize;
            int now = Environment.TickCount;
            if (click.Enabled && pendingTray == fromTray && unchecked((uint)(now - pendingTick)) <= SystemInformation.DoubleClickTime &&
                Math.Abs(point.X - pendingPoint.X) <= limit.Width / 2 && Math.Abs(point.Y - pendingPoint.Y) <= limit.Height / 2)
            {
                click.Stop();
                OpenPanel(true);
                return;
            }

            click.Stop();
            pendingTray = fromTray;
            pendingPoint = point;
            pendingTick = now;
            click.Start();
        }

        private void PowerChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume && !closing && bar.IsHandleCreated)
                bar.BeginInvoke(new Action(Refresh));
        }

        private void ApplyPreferences(int interval, int lowLevel, BarPlacement placement)
        {
            preferences.SaveDisplay(interval, lowLevel, placement);
            Art.LowLevel = preferences.LowLevel;
            auto.Interval = preferences.Interval * 60000;
            lastScanRequested = DateTime.MinValue;
            bar.Invalidate();
            UpdateReadings();
            PositionBar();
        }

        private async void ScanBand(TaskbarLayout band)
        {
            inspecting = true;
            lastScanRequested = DateTime.UtcNow;
            Size size = bar.Size;
            try
            {
                TaskbarLayout result = await Task.Run(() => TaskbarDock.Inspect(band, size, Art.S(7)));
                if (!closing && size == bar.Size && summary.Length > 0)
                {
                    dock = result;
                    scannedBarSize = size;
                }
            }
            catch (Exception) { dock = null; }
            finally { inspecting = false; }
            if (!closing) PositionBar();
        }

        private void PositionBar()
        {
            Screen screen = Screen.PrimaryScreen;
            Rectangle area = screen.WorkingArea;
            bool fullScreen = Native.FullScreen(screen.Bounds, bar.Handle, panel.IsHandleCreated ? panel.Handle : IntPtr.Zero);
            bool hasDevices = summary.Length > 0;
            if (!openingMenu && bar.AdjustWidth(summary.Length))
            {
                dock = null;
                lastScanRequested = DateTime.MinValue;
            }
            bool onTaskbar = false;
            Point target = new Point(area.Right - bar.Width - Art.S(14), area.Bottom - bar.Height - Art.S(6));
            if (hasDevices && preferences.Placement == BarPlacement.Taskbar && TaskbarDock.CanInspect && !fullScreen)
            {
                TaskbarLayout band = TaskbarDock.Band(screen.Bounds, bar.Size);
                if (band != null)
                {
                    if (!inspecting && (!band.SameBand(dock) || (DateTime.UtcNow - lastScanRequested).TotalSeconds >= 2) &&
                        (DateTime.UtcNow - lastScanRequested).TotalMilliseconds > 350)
                        ScanBand(band);
                    if (scannedBarSize == bar.Size && band.SameBand(dock) && dock.Position.HasValue && (DateTime.UtcNow - dock.Captured).TotalSeconds < 6)
                    {
                        target = dock.Position.Value;
                        onTaskbar = true;
                    }
                }
            }

            bar.SetPlacement(onTaskbar);
            bool barVisible = hasDevices && preferences.Placement != BarPlacement.TrayOnly && !fullScreen;
            if (!openingMenu)
            {
                if (bar.Location != target) bar.Location = target;
                if (barVisible && !bar.Visible) bar.Show();
                if (!barVisible && bar.Visible) bar.Hide();
                if (onTaskbar && bar.Visible) Native.RaiseWithoutActivating(bar.Handle);
            }

            if (panel.Visible) PlacePanel(panel, area);
            if (dashboard.Visible) PlacePanel(dashboard, area);
            dashboard.SetPlacementStatus(!hasDevices || preferences.Placement == BarPlacement.TrayOnly ? "系统托盘图标" :
                onTaskbar ? "任务栏同排" : "任务栏上方");
        }

        private void PlacePanel(Form window, Rectangle area)
        {
            int bottom = bar.Visible ? Math.Min(area.Bottom, bar.Top) : area.Bottom;
            Point target = new Point(Math.Max(area.Left, area.Right - window.Width - Art.S(14)),
                Math.Max(area.Top, bottom - window.Height - Art.S(9)));
            if (window.Location != target) window.Location = target;
        }

        private void OpenPanel(bool large)
        {
            if (closing) return;
            click.Stop();
            Form window;
            if (large)
            {
                panel.Hide();
                window = dashboard;
            }
            else
            {
                dashboard.Hide();
                window = panel;
            }
            PlacePanel(window, Screen.PrimaryScreen.WorkingArea);
            window.Show();
            window.Activate();
        }

        private void PinDevices(string[] ids)
        {
            preferences.SavePinnedDevices(ids);
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            summary = Devices.SelectSummary(readings, preferences.PinnedDevices);
            string values = string.Join("  ·  ", summary.Select(item => item.Value));
            tray.Text = "BatteryBar Device Edition" + (demo ? "（演示）" : "") + (values.Length > 0 ? "  " + values : "");
            panel.AdjustWidth(summary.Length);
            PositionBar();
            bar.Invalidate();
            panel.Invalidate();
        }

        private void UpdateReadings()
        {
            panel.SetRefreshState(refreshing, lastRefresh, preferences.Interval);
            dashboard.SetRefreshState(refreshing, lastRefresh);
        }

        private async void Refresh()
        {
            if (refreshing || closing) return;
            refreshing = true;
            refreshItem.Enabled = false;
            UpdateReadings();
            Reading[] result;
            try
            {
                if (demo)
                {
                    await Task.Delay(220);
                    result = new[]
                    {
                        new Reading(83, "已读取", "LIGHTSPEED", "demo:logitech", "G915 X LIGHTSPEED TKL", DeviceKind.Keyboard, "Logitech"),
                        new Reading(64, "已读取", "2.4 GHz", "demo:pulsar", "Pulsar X2H Super Clear", DeviceKind.Mouse, "Pulsar")
                    };
                }
                else result = await Task.Run(new Func<Reading[]>(Devices.Poll));
            }
            catch
            {
                result = new Reading[0];
            }
            if (closing) return;
            readings = result;
            lastRefresh = DateTime.Now;
            refreshing = false;
            refreshItem.Enabled = true;
            UpdateSummary();
            UpdateReadings();
        }

        protected override void ExitThreadCore()
        {
            if (closing) return;
            closing = true;
            SystemEvents.PowerModeChanged -= PowerChanged;
            layout.Dispose();
            auto.Dispose();
            deviceChange.Dispose();
            click.Dispose();
            tray.Dispose();
            if (trayIcon != null) trayIcon.Dispose();
            menu.Dispose();
            dashboard.Dispose();
            panel.Dispose();
            bar.Dispose();
            base.ExitThreadCore();
        }
    }

    internal class Surface : Form
    {
        internal Surface(int width, int height, int radius = 14)
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            DoubleBuffered = true;
            SetRoundedSize(width, height, radius);
        }

        protected void SetRoundedSize(int width, int height, int radius)
        {
            ClientSize = new Size(Art.S(width), Art.S(height));
            using (GraphicsPath path = Art.Round(new RectangleF(0, 0, Width, Height), Art.S(radius)))
                Region = new Region(path);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= 0x80; // WS_EX_TOOLWINDOW
                p.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return p;
            }
        }

        protected Button ButtonAt(string text, int x, int y, int width, int height, Action action)
        {
            var button = new SmoothedButton
            {
                Text = text,
                Location = new Point(Art.S(x), Art.S(y)),
                Size = new Size(Art.S(width), Art.S(height)),
                FlatStyle = FlatStyle.Flat,
                BackColor = this.BackColor,
                ForeColor = Art.Ink,
                Cursor = Cursors.Hand,
                Font = UiFont(text == "×" ? 19 : 13),
                UseVisualStyleBackColor = false,
                UseCompatibleTextRendering = true
            };
            button.Click += delegate { action(); };
            Controls.Add(button);
            return button;
        }

        internal virtual void PaintButtonBackdrop(Graphics graphics, Rectangle buttonBounds)
        {
            using (var brush = new SolidBrush(BackColor))
                graphics.FillRectangle(brush, 0, 0, buttonBounds.Width, buttonBounds.Height);
        }

        protected static Font UiFont(int size)
        {
            return Typography.ControlFont(Art.S(size));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Art.SmoothText(e.Graphics);
            e.Graphics.ScaleTransform(Art.Scale, Art.Scale);
            base.OnPaint(e);
        }
    }

    internal sealed class SmoothedButton : Button
    {
        private bool hovered, pressed;

        internal SmoothedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque |
                ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            // Paint the complete child surface before blending its rounded face.
            var surface = (Surface)Parent;
            surface.PaintButtonBackdrop(graphics, Bounds);
            if (ClientSize.Width <= 2 || ClientSize.Height <= 2) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var face = new RectangleF(1, 1, ClientSize.Width - 2, ClientSize.Height - 2);
            Color fill = !Enabled ? Color.FromArgb(242, 247, 247) :
                pressed && hovered ? Color.FromArgb(215, 239, 235) :
                hovered ? Color.FromArgb(235, 248, 245) : Color.White;
            Color border = Focused && ShowFocusCues ? Art.ButtonFocus :
                hovered && Enabled ? Art.ButtonHoverBorder : Art.ButtonBorder;
            float radius = Math.Min(8 * Art.Scale, Math.Min(face.Width, face.Height) / 2);
            Art.Fill(graphics, face, radius, fill);
            Art.Border(graphics, face, radius, border);
            Art.Caption(graphics, Text, Font, ClientRectangle,
                Enabled ? ForeColor : SystemColors.GrayText, StringAlignment.Center);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hovered = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hovered = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            pressed = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            ClearPress();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture) ClearPress();
        }

        private void ClearPress()
        {
            if (!pressed) return;
            pressed = false;
            Invalidate();
        }
    }

    internal sealed class SmoothedCheckBox : CheckBox
    {
        private bool paintingChrome;

        public override string Text
        {
            get { return paintingChrome ? "" : base.Text; }
            set { base.Text = value; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            paintingChrome = true;
            try { base.OnPaint(e); }
            finally { paintingChrome = false; }
            int labelStart = CheckBoxRenderer.GetGlyphSize(e.Graphics, CheckBoxState.UncheckedNormal).Width + Art.S(4);
            var bounds = new Rectangle(labelStart, 0, Math.Max(0, ClientSize.Width - labelStart), ClientSize.Height);
            Art.Caption(e.Graphics, Text, Font, bounds,
                Enabled ? ForeColor : SystemColors.GrayText, StringAlignment.Near);
        }
    }

    internal sealed class Bar : Surface
    {
        private const int ColumnWidth = 73;
        private const int StripHeight = 30;
        private const int CornerRadius = 8;
        private const int DemoBadgeWidth = 46;
        private readonly bool demo;
        private readonly Func<Reading[]> read;
        private readonly ToolTip tooltip = new ToolTip();
        private bool light;
        private bool docked;
        internal event EventHandler DevicesChanged;

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg != 0x0219 || message.WParam.ToInt32() != 7) return;
            EventHandler changed = DevicesChanged;
            if (changed != null) changed(this, EventArgs.Empty);
        }

        internal Bar(bool demo, Func<Reading[]> read) : base(LogicalWidth(1, demo), StripHeight, CornerRadius)
        {
            this.demo = demo;
            this.read = read;
            BackColor = Art.Dark;
            Cursor = Cursors.Hand;
            AccessibleName = "外设电量摘要";
            tooltip.SetToolTip(this, "单击查看电量 · 双击打开设置");
        }

        internal static int LogicalWidth(int count, bool demo)
        {
            if (count == 0) return 0;
            return count * ColumnWidth + (demo ? DemoBadgeWidth : 0);
        }

        internal bool AdjustWidth(int count)
        {
            if (count == 0 || ClientSize.Width == Art.S(LogicalWidth(count, demo))) return false;
            // Do not widen a visible docked window into neighboring tray controls.
            if (Visible) Hide();
            SetRoundedSize(LogicalWidth(count, demo), StripHeight, CornerRadius);
            return true;
        }

        internal void SetPlacement(bool onTaskbar)
        {
            if (docked == onTaskbar) return;
            docked = onTaskbar;
            light = onTaskbar && Preferences.IsLightTaskbar();
            BackColor = light ? Color.FromArgb(241, 243, 245) : Art.Dark;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tooltip.Dispose();
            base.Dispose(disposing);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams p = base.CreateParams;
                p.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                p.ClassStyle &= ~0x00020000; // Keep the taskbar widget flat.
                return p;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Reading[] states = read();
            if (states.Length == 0) return;
            Color border = light ? Color.FromArgb(216, 222, 224) : Art.LineDark;
            Art.Border(g, new RectangleF(.5f, .5f, Width / Art.Scale - 1, StripHeight - 1), CornerRadius, border);
            for (int index = 0; index < states.Length; index++)
            {
                int x = index * ColumnWidth;
                if (index > 0) Art.Divider(g, x, 7, x, 23, border);
                DrawSummary(g, states[index], x);
            }
            if (demo)
            {
                int x = states.Length * ColumnWidth;
                Art.Divider(g, x, 7, x, 23, border);
                Art.Text(g, "演示", x + 10, 7, 11, light ? Art.Muted : Art.Faint);
            }
        }

        private void DrawSummary(Graphics g, Reading state, float x)
        {
            Art.Device(g, state.Kind, x + 8, 6, 17, light ? Art.Muted : Art.Faint);
            Art.TextRight(g, state.Value, x + 65, 6, 13, light ? Art.Value(state) : Art.TrayValue(state), TypeWeight.Bold, useOutlines: false);
        }
    }

    internal sealed class Detail : Surface
    {
        private const int StandardWidth = 488;
        private const int CardStep = 228;
        private const int PanelHeight = 324;
        internal const int MaximumWidth = StandardWidth + CardStep;
        private int logicalWidth = StandardWidth;
        private readonly bool demo;
        private readonly Func<Reading[]> read;
        private readonly Button settings, refresh, close;
        private DateTime? lastRefresh;
        private bool busy;
        private int interval = 5;

        internal Detail(bool demo, Func<Reading[]> read, Action requestRefresh, Action openSettings) : base(StandardWidth, PanelHeight)
        {
            this.demo = demo;
            this.read = read;
            BackColor = Art.Paper;
            KeyPreview = true;
            AccessibleName = "外设电量面板";
            settings = ButtonAt("设置", 253, 23, 65, 34, openSettings);
            refresh = ButtonAt("立即刷新", 325, 23, 99, 34, requestRefresh);
            close = ButtonAt("×", 431, 23, 34, 34, Hide);
            close.AccessibleName = "收起面板";
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Hide();
                if (e.KeyCode == Keys.F5) requestRefresh();
            };
            Deactivate += delegate { Hide(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                Hide();
            };
        }

        internal static int WidthForCount(int count)
        {
            return count >= 3 ? MaximumWidth : StandardWidth;
        }

        internal void AdjustWidth(int count)
        {
            int width = WidthForCount(count);
            if (width == logicalWidth) return;
            logicalWidth = width;
            SetRoundedSize(width, PanelHeight, 14);
            settings.Left = Art.S(width - 235);
            refresh.Left = Art.S(width - 163);
            close.Left = Art.S(width - 57);
        }

        internal void SetRefreshState(bool running, DateTime? time, int minutes)
        {
            interval = minutes;
            busy = running;
            lastRefresh = time;
            refresh.Enabled = !running;
            refresh.Text = running ? "读取中…" : "立即刷新";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Art.Border(g, new RectangleF(.5f, .5f, logicalWidth - 1, PanelHeight - 1), 14, Art.Line);
            Art.Text(g, demo ? "外设电量 · 演示" : "外设电量", 22, demo ? 16 : 24, 23, Art.Ink, TypeWeight.Bold);
            if (demo) Art.Text(g, "示例数据", 23, 50, 12, Art.Muted);
            Reading[] states = read();
            if (states.Length == 0) Art.Text(g, "暂时没有可显示的电量", 23, 115, 14, Art.Muted);
            for (int index = 0; index < states.Length; index++)
                DrawCard(g, states[index], 22 + index * CardStep);
            Art.Text(g, "每 " + interval + " 分钟自动刷新", 23, 291, 11, Art.Muted);
            string updated = busy ? "读取中…" : lastRefresh.HasValue ? "上次刷新  " + lastRefresh.Value.ToString("HH:mm") : "等待首次读取";
            Art.TextRight(g, updated, logicalWidth - 23, 291, 11, Art.Muted);
        }

        private static void DrawCard(Graphics g, Reading state, float x)
        {
            RectangleF card = new RectangleF(x, 82, 216, 191);
            Art.Fill(g, card, 13, Color.White);
            Art.Border(g, card, 13, Art.Line);
            Art.Fill(g, new RectangleF(x + 15, 97, 30, 30), 8, Art.Paper);
            Art.Device(g, state.Kind, x + 20, 102, 20, Art.Muted);
            Art.Text(g, state.Source ?? "电量", x + 54, 97, 10, Art.Muted, TypeWeight.Medium);
            Art.TextClipped(g, state.Name, x + 54, 110, 150, 13, Art.Ink);
            Color tone = Art.Value(state);
            Art.Text(g, state.Value, x + 14, 140, 43, tone, TypeWeight.Bold);
            Art.Text(g, state.Status,
                x + 16, 198, 11, state.Status == "充电中" ? tone : Art.Muted);
            Art.Divider(g, x + 16, 229, x + 200, 229, Art.Line);
            Art.Text(g, state.Link, x + 16, 242, 11, Art.Muted);
            Art.TextRight(g, state.Updated.HasValue ? state.Updated.Value.ToString("HH:mm") : "—",
                x + 200, 242, 11, Art.Muted);
            Art.Gauge(g, x + 159, 139, state);
        }
    }

    internal static class Art
    {
        internal static readonly float Scale = GetScale();
        internal static int LowLevel = 20;
        internal static readonly Color Ink = Color.FromArgb(24, 35, 30);
        internal static readonly Color Muted = Color.FromArgb(83, 100, 90);
        internal static readonly Color Line = Color.FromArgb(219, 230, 222);
        internal static readonly Color Paper = Color.FromArgb(245, 249, 246);
        internal static readonly Color Dark = Color.FromArgb(25, 38, 32);
        internal static readonly Color Faint = Color.FromArgb(197, 217, 204);
        internal static readonly Color LineDark = Color.FromArgb(69, 90, 79);
        internal static readonly Color ButtonBorder = Color.FromArgb(203, 221, 221);
        internal static readonly Color ButtonHoverBorder = Color.FromArgb(146, 193, 187);
        internal static readonly Color ButtonFocus = Color.FromArgb(26, 115, 145);
        internal static readonly Color HeaderMint = Color.FromArgb(210, 246, 223);
        internal static readonly Color HeaderBlue = Color.FromArgb(164, 213, 252);
        private static readonly Color BatteryMint = Color.FromArgb(118, 221, 169);
        private static readonly Color BatteryBlue = Color.FromArgb(51, 132, 213);
        private static readonly Color WarningLight = Color.FromArgb(249, 184, 102);
        private static readonly Color WarningDark = Color.FromArgb(200, 105, 39);

        private static float GetScale()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            float fitting = Math.Min(area.Width / (Detail.MaximumWidth + 30f), area.Height / 530f);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return Math.Min(fitting, Math.Max(1, g.DpiX / 96f));
        }

        internal static int S(int value) { return (int)Math.Round(value * Scale); }

        internal static Color Value(Reading state)
        {
            if (!state.Level.HasValue) return Muted;
            return state.Level.Value <= LowLevel ? Color.FromArgb(147, 81, 10) : Color.FromArgb(13, 110, 74);
        }

        internal static Color TrayValue(Reading state)
        {
            if (!state.Level.HasValue) return Faint;
            return state.Level.Value <= LowLevel ? Color.FromArgb(255, 196, 122) : Color.FromArgb(134, 230, 181);
        }

        internal static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static void Border(Graphics g, RectangleF r, float radius, Color color)
        {
            using (var pen = new Pen(color)) using (GraphicsPath path = Round(r, radius)) g.DrawPath(pen, path);
        }

        internal static void Fill(Graphics g, RectangleF r, float radius, Color color)
        {
            using (var brush = new SolidBrush(color)) using (GraphicsPath path = Round(r, radius)) g.FillPath(brush, path);
        }

        internal static void GradientFill(Graphics g, RectangleF area, float radius, Color start, Color end,
            LinearGradientMode direction)
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            using (var brush = new LinearGradientBrush(area, start, end, direction))
            using (GraphicsPath path = Round(area, radius))
                g.FillPath(brush, path);
        }

        internal static void BatteryFill(Graphics g, RectangleF area, float radius, int level, LinearGradientMode direction)
        {
            bool low = level <= LowLevel;
            GradientFill(g, area, radius, low ? WarningLight : BatteryMint, low ? WarningDark : BatteryBlue, direction);
        }

        internal static void Divider(Graphics g, float x1, float y1, float x2, float y2, Color color)
        {
            using (var pen = new Pen(color)) g.DrawLine(pen, x1, y1, x2, y2);
        }

        internal static void SmoothText(Graphics graphics)
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        internal static void Text(Graphics g, string text, float x, float y, float size, Color color, TypeWeight weight = TypeWeight.Regular)
        {
            using (var font = Typography.Create(text, size, weight))
                DrawText(g, text, font, color, new RectangleF(x, y, 0, 0), StringFormat.GenericTypographic, size <= 20);
        }

        internal static void TextRight(Graphics g, string text, float x, float y, float size, Color color,
            TypeWeight weight = TypeWeight.Regular, bool useOutlines = true)
        {
            using (var font = Typography.Create(text, size, weight))
            using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Far })
                DrawText(g, text, font, color, new RectangleF(x, y, 0, 0), format, useOutlines && size <= 20);
        }

        internal static void TextClipped(Graphics g, string text, float x, float y, float width, float size, Color color)
        {
            using (var font = Typography.Create(text, size, TypeWeight.Medium))
            using (var format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.FormatFlags = (format.FormatFlags & ~StringFormatFlags.NoClip) | StringFormatFlags.NoWrap;
                format.Trimming = StringTrimming.EllipsisCharacter;
                DrawText(g, text, font, color, new RectangleF(x, y, width, size * 2), format, true);
            }
        }

        internal static void Caption(Graphics g, string text, Font font, Rectangle bounds, Color color, StringAlignment alignment)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            using (var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = alignment,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
                DrawText(g, text, font, color, bounds, format, true);
        }

        private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF bounds,
            StringFormat format, bool useOutlines)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool laidOut = bounds.Width > 0;
            using (var brush = new SolidBrush(color))
            {
                if (useOutlines)
                {
                    using (var path = new GraphicsPath(FillMode.Winding))
                    {
                        try
                        {
                            if (laidOut) path.AddString(text, font.FontFamily, (int)font.Style, font.Size, bounds, format);
                            else path.AddString(text, font.FontFamily, (int)font.Style, font.Size, bounds.Location, format);
                        }
                        catch (ArgumentException) { path.Reset(); }
                        catch (ExternalException) { path.Reset(); }
                        if (path.PointCount != 0)
                        {
                            SmoothingMode previous = g.SmoothingMode;
                            try
                            {
                                g.SmoothingMode = SmoothingMode.AntiAlias;
                                g.FillPath(brush, path);
                            }
                            finally { g.SmoothingMode = previous; }
                            return;
                        }
                    }
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                }
                else SmoothText(g);

                if (laidOut) g.DrawString(text, font, brush, bounds, format);
                else g.DrawString(text, font, brush, bounds.Location, format);
            }
        }

        internal static void Device(Graphics g, DeviceKind kind, float x, float y, float size, Color color)
        {
            GraphicsState saved = g.Save();
            g.TranslateTransform(x, y);
            g.ScaleTransform(size / 20, size / 20);
            using (var pen = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (kind == DeviceKind.Headset)
                {
                    g.DrawArc(pen, 2.5f, 3f, 15, 15, 180, 180);
                    g.DrawLine(pen, 2.5f, 10.5f, 2.5f, 15);
                    g.DrawLine(pen, 17.5f, 10.5f, 17.5f, 15);
                    using (GraphicsPath left = Round(new RectangleF(2.5f, 11.5f, 4.5f, 6), 2))
                    using (GraphicsPath right = Round(new RectangleF(13, 11.5f, 4.5f, 6), 2))
                    { g.DrawPath(pen, left); g.DrawPath(pen, right); }
                }
                else if (kind == DeviceKind.Mouse)
                {
                    using (GraphicsPath path = Round(new RectangleF(5, 2, 10, 16), 5)) g.DrawPath(pen, path);
                    g.DrawLine(pen, 10, 5, 10, 8);
                }
                else if (kind == DeviceKind.Keyboard)
                {
                    using (GraphicsPath outline = Round(new RectangleF(1.5f, 4.5f, 17, 11), 2)) g.DrawPath(pen, outline);
                    for (int key = 0; key < 3; key++)
                        g.DrawLine(pen, 5 + key * 4, 8, 5.6f + key * 4, 8);
                    g.DrawLine(pen, 6, 12, 14, 12);
                }
                else
                {
                    using (GraphicsPath path = Round(new RectangleF(2, 5, 15, 10), 2)) g.DrawPath(pen, path);
                    g.DrawLine(pen, 18, 8, 18, 12);
                    g.DrawLine(pen, 5, 10, 12, 10);
                }
            }
            g.Restore(saved);
        }

        internal static void Gauge(Graphics g, float x, float y, Reading state)
        {
            Fill(g, new RectangleF(x + 13, y, 12, 7), 3, Line);
            var body = new RectangleF(x, y + 5, 38, 74);
            Fill(g, body, 8, Paper);
            Border(g, body, 8, Line);
            if (!state.Level.HasValue || state.Level.Value <= 0) return;
            var inside = new RectangleF(x + 3, y + 8, 32, 68);
            float fill = inside.Height * state.Level.Value / 100f;
            var liquid = new RectangleF(inside.X, inside.Bottom - fill, inside.Width, fill);
            BatteryFill(g, liquid, Math.Min(5, fill / 2), state.Level.Value, LinearGradientMode.Vertical);
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { internal int Left, Top, Right, Bottom; }

        internal static bool FullScreen(Rectangle screen, IntPtr bar, IntPtr panel)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == bar || foreground == panel || foreground == GetShellWindow())
                return false;
            uint process;
            GetWindowThreadProcessId(foreground, out process);
            uint shellProcess;
            GetWindowThreadProcessId(GetShellWindow(), out shellProcess);
            if (process == shellProcess) return false;
            Rect window;
            return GetWindowRect(foreground, out window) && window.Left <= screen.Left &&
                window.Right >= screen.Right && window.Top <= screen.Top && window.Bottom >= screen.Bottom;
        }

        internal static void RaiseWithoutActivating(IntPtr window)
        {
            SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x13);
        }

        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint process);
    }

    internal static class ProtocolChecks
    {
        internal static int Run()
        {
            byte[] mouse = Protocol.MouseRequest(0x80);
            if (mouse[89] != 0x85) return 1;
            mouse[1] = 2;
            mouse[10] = 0xab;
            mouse[89] = Protocol.RazerCrc(mouse);
            if (Protocol.MouseValue(mouse, 0x80) != 171 ||
                Protocol.MouseValue(mouse, 0x84).HasValue) return 2;
            mouse[10] ^= 1;
            if (Protocol.MouseValue(mouse, 0x80).HasValue) return 3;

            byte[] request = Protocol.HeadsetRequest(0x1234, 64);
            if (request[13] != 0xe5) return 4;
            byte[] reply = { 2, 14, 4, 0xff, 11, 0, 0x96, 0xc3, 0x14, 4, 0x10, 0x34, 0x12, 0, 70, 0x0d };
            if (Protocol.HeadsetValue(reply, reply.Length, 0x1234) != 70 ||
                Protocol.HeadsetValue(reply, reply.Length, 0x1235).HasValue) return 5;
            reply[15] ^= 1;
            if (Protocol.HeadsetValue(reply, reply.Length, 0x1234).HasValue) return 6;

            var band = new Rectangle(0, 1040, 1920, 40);
            var tray = new Rectangle(1712, 1040, 208, 40);
            var widget = new Size(146, 30);
            var buttons = new[] { new Rectangle(880, 1040, 440, 40), tray };
            if (TaskbarDock.FindPosition(band, tray, buttons, widget, 7) != new Point(1559, 1045)) return 7;
            buttons[0] = new Rectangle(880, 1040, 700, 40);
            if (TaskbarDock.FindPosition(band, tray, buttons, widget, 7).HasValue) return 8;
            if (TaskbarDock.FindPosition(band, tray, new[] { tray }, widget, 7).HasValue) return 9;
            if (TaskbarDock.FindPosition(band, Rectangle.Empty, buttons, widget, 7).HasValue) return 10;

            if (Hid.NormalizeLevel(127, 8, 0, 255) != 50 || Hid.NormalizeLevel(3, 4, 0, 4) != 75 ||
                Hid.NormalizeLevel(0xff, 8, 0, 100).HasValue || Hid.NormalizeLevel(0xff, 8, -2, 0) != 50 ||
                Hid.NormalizeLevel(0, 8, 0, 100) != 0) return 11;
            if (Marshal.SizeOf(typeof(Pnp.DeviceInfo)) != (IntPtr.Size == 8 ? 32 : 28)) return 12;

            var one = new Reading(10, "", "", "one");
            var two = new Reading(20, "", "", "two");
            var three = new Reading(null, "", "", "three");
            var four = new Reading(40, "", "", "four");
            var five = new Reading(0, "", "", "five");
            var available = new[] { one, two, three, four, five };
            var summary = Devices.SelectSummary(available, new[] { "gone", "THREE", "three" });
            if (summary.Length != 3 || summary[0] != one || summary[1] != two || summary[2] != four) return 13;
            if (Devices.SelectSummary(new Reading[0], new[] { "one" }).Length != 0) return 14;
            summary = Devices.SelectSummary(new[] { Reading.Empty, three, five }, new string[0]);
            if (summary.Length != 1 || summary[0] != five) return 15;
            if (Bar.LogicalWidth(0, false) != 0 || Bar.LogicalWidth(1, false) != 73 ||
                Bar.LogicalWidth(2, false) != 146 || Bar.LogicalWidth(3, false) != 219 ||
                Bar.LogicalWidth(3, true) != 265) return 16;
            buttons[0] = new Rectangle(880, 1040, 440, 40);
            if (TaskbarDock.FindPosition(band, tray, buttons, new Size(219, 30), 7) != new Point(1486, 1045))
                return 17;
            var recovered = new Reading(30, "", "", "three");
            available[2] = recovered;
            summary = Devices.SelectSummary(available, new[] { "THREE", "three" });
            if (summary.Length != 3 || summary[0] != recovered || summary[1] != one || summary[2] != two) return 18;
            if (Devices.SelectSummary(new[] { three }, new[] { "three" }).Length != 0) return 19;
            if (Detail.WidthForCount(0) != 488 || Detail.WidthForCount(2) != 488 ||
                Detail.WidthForCount(3) != 716) return 20;
            byte[] pulsar = CompXMouse.BuildFrame(4);
            if (pulsar[16] != 0x49 || !CompXMouse.IsReply(pulsar, 17, 4) ||
                CompXMouse.IsReply(pulsar, 17, 3)) return 21;
            pulsar[6] = 64;
            if (CompXMouse.IsReply(pulsar, 17, 4)) return 22;
            byte[] hidpp = { 0x10, 1, 8, 0x19, 72, 2, 0 };
            if (LogitechLightspeedProtocol.MatchResponse(hidpp, 7, 1, 8, 0x19) != 1 ||
                LogitechLightspeedProtocol.MatchResponse(hidpp, 7, 2, 8, 0x19) != 0) return 23;
            if (!LogitechLightspeedProtocol.IsG915X("G915 X LS TKL") ||
                LogitechLightspeedProtocol.IsG915X("G915 TKL") ||
                LogitechLightspeedProtocol.ReadLegacyPercent(0, 1).HasValue ||
                LogitechLightspeedProtocol.ReadLegacyPercent(0, 3) != 100) return 24;
            byte[] atk = AtkZeroProtocol.BuildRequest(65, true);
            if (atk.Length != 65 || atk[0] != 8 || atk[2] != 0x72 || atk[5] != 1 || atk[6] != 7 ||
                atk[7] != 1 || atk[3] != 2 || atk[1] != 0x7d ||
                AtkZeroProtocol.BuildRequest(64, false)[5] != 0) return 25;
            byte[] atkReply = { 8, 0x72, 0, 5, 0, 7, 1, 100, 0x52, 0x10 };
            if (AtkZeroProtocol.ReadLevel(atkReply, atkReply.Length) != 100 ||
                AtkZeroProtocol.ReadLevel(atkReply, 9).HasValue) return 26;
            atkReply[7] = 0;
            if (AtkZeroProtocol.ReadLevel(atkReply, atkReply.Length) != 0) return 27;
            atkReply[5] = 8;
            if (AtkZeroProtocol.ReadLevel(atkReply, atkReply.Length).HasValue) return 28;
            byte[] hyperx = HyperXCloudThreeSProtocol.BuildRequest(64);
            if (hyperx.Length != 64 || hyperx[0] != 0x0c || hyperx[1] != 2 ||
                hyperx[2] != 3 || hyperx[3] != 1 || hyperx[4] != 0 || hyperx[5] != 6) return 29;
            hyperx[6] = 89;
            if (HyperXCloudThreeSProtocol.ReadLevel(hyperx, 64) != 89 ||
                HyperXCloudThreeSProtocol.ReadLevel(hyperx, 6).HasValue) return 30;
            hyperx[6] = 0xff;
            if (HyperXCloudThreeSProtocol.ReadLevel(hyperx, 64).HasValue) return 31;
            hyperx[5] = 0x48;
            hyperx[6] = 1;
            if (HyperXCloudThreeSProtocol.ReadLevel(hyperx, 64).HasValue) return 32;
            var checkedReply = AtkZeroProtocol.BuildRequest(64, true);
            checkedReply[8] = 83;
            checkedReply[1] = 0xd0;
            if (AtkZeroProtocol.ReadLevel(checkedReply, 64) != 83) return 34;
            checkedReply[8] = 84;
            if (AtkZeroProtocol.ReadLevel(checkedReply, 64).HasValue) return 35;
            if (HyperXCloudThreeSProtocol.BuildRequest(52)[5] != 6) return 36;
            if (!AtkZeroProtocol.IsCandidate("hid#vid_373b&pid_12cc&mi_01#sample") ||
                !AtkZeroProtocol.IsCandidate("hid#vid_373b&pid_1155#sample") ||
                AtkZeroProtocol.IsCandidate("hid#vid_373b&pid_12cd&mi_01#sample")) return 37;
            byte[] receiverReply = CompXMouse.BuildFrame(4);
            if (AtkZeroProtocol.ReadCompXLevel(receiverReply).HasValue) return 38;
            receiverReply[6] = 67;
            receiverReply[8] = 0x0f;
            receiverReply[9] = 0xa0;
            receiverReply[16] = CompXMouse.Checksum(receiverReply);
            if (AtkZeroProtocol.ReadCompXLevel(receiverReply) != 67) return 39;
            receiverReply[6] = 0;
            if (AtkZeroProtocol.ReadCompXLevel(receiverReply).HasValue) return 40;
            receiverReply[16] = CompXMouse.Checksum(receiverReply);
            if (AtkZeroProtocol.ReadCompXLevel(receiverReply) != 0) return 41;

            byte[] harpe = { 3, 0x12, 7, 0, 0, 53, 2, 0x14, 0xfd, 0x0e, 0 };
            if (DeviceEditionProtocol.HarpeLevel(harpe, harpe.Length, 3) != 53 ||
                DeviceEditionProtocol.HarpeLevel(harpe, harpe.Length, 0).HasValue) return 42;
            byte[] nova = { 0, 0xb0, 3, 4, 1 };
            if (DeviceEditionProtocol.NovaLevel(nova, nova.Length, true) != 100 ||
                DeviceEditionProtocol.NovaLevel(nova, nova.Length, false) != 4) return 43;
            nova[2] = 0;
            if (DeviceEditionProtocol.NovaLevel(nova, nova.Length, false).HasValue) return 44;
            byte[] rapoo = { 7, 0, 0, 0, 0, 0, 2, 62 };
            if (DeviceEditionProtocol.RapooLevel(rapoo, rapoo.Length) != 62) return 45;
            rapoo[6] = 0;
            if (DeviceEditionProtocol.RapooLevel(rapoo, rapoo.Length).HasValue) return 46;
            byte[] mchose = { 0x11, 0xf9, 0xac, 0xad, 0xce, 0xff, 0xfd, 0xfa,
                0xf8, 0xff, 0xf6, 0x9b, 0xff, 0x9b };
            if (DeviceEditionProtocol.MchoseLevel(mchose, 0x11) != 100 ||
                DeviceEditionProtocol.MchoseLevel(mchose, 0x12).HasValue) return 47;
            byte[] cherry = DeviceEditionProtocol.CherryRequest(64);
            if (cherry[0] != 4 || cherry[1] != 0x72 || cherry[3] != 0x1a || cherry[7] != 0x55)
                return 48;
            byte[] cherryReply = { 4, 0x59, 0, 0x1a, 0, 0, 0, 0, 62, 1 };
            if (DeviceEditionProtocol.CherryLevel(cherryReply, cherryReply.Length) != 62 ||
                DeviceEditionProtocol.CherryLevel(cherryReply, 9).HasValue) return 49;
            if (BluetoothProtocol.DisplayKind("Minilo75 Pro") != DeviceKind.Keyboard ||
                BluetoothProtocol.DisplayKind("MOMENTUM 4") != DeviceKind.Headset ||
                BluetoothProtocol.DisplayKind("Bluetooth device") != DeviceKind.Generic) return 50;

            if (LogitechLightspeedProtocol.EstimateCharge(3811, false) != 50 ||
                LogitechLightspeedProtocol.EstimateCharge(0, false).HasValue) return 51;
            byte[] alternateRazer = Protocol.MouseRequest(0x80, 0x3f);
            alternateRazer[1] = 2;
            alternateRazer[10] = 117;
            alternateRazer[89] = Protocol.RazerCrc(alternateRazer);
            if (Protocol.MouseValue(alternateRazer, 0x80, 0x3f) != 117 ||
                Protocol.MouseValue(alternateRazer, 0x80).HasValue) return 52;
            byte[] novaPro = { 6, 0xb0, 0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 8 };
            if (DeviceEditionProtocol.NovaProLevel(novaPro, 16) != 50) return 53;
            novaPro[15] = 1;
            if (DeviceEditionProtocol.NovaProLevel(novaPro, 16).HasValue) return 54;
            byte[] nordic = { 7, 0x20, 3, 0x20, 3, 0x20, 3, 1, 25, 0, 4, 0x84, 0, 0x17, 0x46, 0, 2, 3, 0xbc };
            if (DeviceEditionProtocol.RapooNordicLevel(nordic, 19, 0x4617) != 25 ||
                DeviceEditionProtocol.RapooNordicLevel(nordic, 19, 0x4614).HasValue) return 55;
            byte[] aerox = { 0, 0xd2, 4 };
            if (DeviceEditionProtocol.AeroxLevel(aerox, aerox.Length) != 15) return 56;
            aerox[2] = 0;
            if (DeviceEditionProtocol.AeroxLevel(aerox, aerox.Length).HasValue) return 57;
            byte[] cloud = { 0x66, 0x89, 0x0e, 0xd7, 48 };
            if (DeviceEditionProtocol.CloudLevel(cloud, cloud.Length, false) != 48 ||
                DeviceEditionProtocol.CloudLevel(cloud, cloud.Length, true).HasValue) return 58;
            byte[] h9 = { 0, 4, 0xff, 0x0b, 0, 0x96, 0xc3, 0x14, 4, 0, 0, 0, 1, 64, 0 };
            bool h9Charging;
            if (SonyInzoneH9Protocol.Parse(h9, out h9Charging) != 64 || !h9Charging) return 59;
            h9[13] = 0xff;
            if (SonyInzoneH9Protocol.Parse(h9, out h9Charging).HasValue) return 60;
            byte[] otherMchose = (byte[])mchose.Clone();
            otherMchose[4] = 0x4f;
            if (DeviceEditionProtocol.MchoseLevel(otherMchose, 0x11, false) != 100 ||
                DeviceEditionProtocol.MchoseLevel(otherMchose, 0x11).HasValue) return 61;
            return 0;
        }
    }
}
