using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace BatteryBar
{
    internal sealed class Dashboard : Surface
    {
        private readonly bool demo;
        private readonly Preferences preferences;
        private readonly Func<Reading[]> readings;
        private readonly Action<int, int, BarPlacement> applyDisplay;
        private readonly Button refresh, previousPage, nextPage, pin;
        private readonly Action<string[]> pinDevices;
        private int page;
        private int PageCount { get { return Math.Max(1, (readings().Length + 1) / 2); } }
        private readonly ComboBox interval;
        private readonly ComboBox lowLevel;
        private readonly ComboBox placement;
        private readonly CheckBox startup;
        private DateTime? lastRefresh;
        private string currentPlacement;
        private bool busy;
        private bool updating;

        internal Dashboard(bool demo, Preferences preferences, Func<Reading[]> readings, Action requestRefresh,
            Action<int, int, BarPlacement> applyDisplay, Action<string[]> pinDevices) : base(656, 488)
        {
            this.demo = demo;
            this.preferences = preferences;
            this.readings = readings;
            this.applyDisplay = applyDisplay;
            this.pinDevices = pinDevices;
            BackColor = Art.Paper;
            AccessibleName = "外设电量与设置";
            KeyPreview = true;
            refresh = ButtonAt("立即刷新", 501, 39, 83, 32, requestRefresh);
            Button close = ButtonAt("×", 591, 39, 32, 32, Hide);
            close.AccessibleName = "收起大面板";

            previousPage = ButtonAt("‹", 306, 442, 32, 29, () => ChangePage(-1));
            previousPage.AccessibleName = "上一组设备";
            nextPage = ButtonAt("›", 346, 442, 32, 29, () => ChangePage(1));
            nextPage.AccessibleName = "下一组设备";
            pin = ButtonAt("优先显示本组", 498, 442, 138, 29, PinPage);

            placement = SelectAt("电量条位置", 365, 322, 246, new[] { "任务栏同排（优先）", "任务栏上方", "仅系统托盘图标" });
            interval = SelectAt("刷新频率", 365, 357, 110, new[] { "1 分钟", "3 分钟", "5 分钟", "10 分钟", "15 分钟" });
            lowLevel = SelectAt("低电量变色", 502, 357, 109, new[] { "10%", "20%", "30%" });
            startup = new SmoothedCheckBox
            {
                Location = new Point(Art.S(365), Art.S(394)),
                Size = new Size(Art.S(230), Art.S(25)),
                BackColor = Color.White,
                ForeColor = Art.Ink,
                Font = UiFont(13),
                Text = demo ? "登录 Windows 时启动（实机可设）" : "登录 Windows 时自动启动",
                Enabled = !demo,
                UseCompatibleTextRendering = true
            };
            Controls.Add(startup);
            ReadSettings();
            UpdatePagination();
            placement.SelectedIndexChanged += DisplayChanged;
            interval.SelectedIndexChanged += DisplayChanged;
            lowLevel.SelectedIndexChanged += DisplayChanged;
            startup.CheckedChanged += StartupChanged;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Hide();
                if (e.KeyCode == Keys.F5) requestRefresh();
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (e.CloseReason != CloseReason.UserClosing) return;
                e.Cancel = true;
                Hide();
            };
        }

        private ComboBox SelectAt(string name, int x, int y, int width, string[] choices)
        {
            var select = new ComboBox
            {
                AccessibleName = name,
                Location = new Point(Art.S(x), Art.S(y)),
                Width = Art.S(width),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Font = Typography.SystemFont(Art.S(13)),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = Art.S(20),
                BackColor = Art.Paper,
                ForeColor = Art.Ink
            };
            select.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                e.DrawBackground();
                if (e.Index >= 0)
                {
                    string text = (string)select.Items[e.Index];
                    Color color = (e.State & DrawItemState.Selected) != 0 ? SystemColors.HighlightText : Art.Ink;
                    Art.Text(e.Graphics, text, e.Bounds.X + Art.S(4), e.Bounds.Y + Art.S(2), Art.S(13), color, TypeWeight.Medium);
                }
                e.DrawFocusRectangle();
            };
            select.Items.AddRange(choices);
            Controls.Add(select);
            return select;
        }

        private void ReadSettings()
        {
            updating = true;
            placement.SelectedIndex = (int)preferences.Placement;
            interval.SelectedIndex = Array.IndexOf(Preferences.Intervals, preferences.Interval);
            lowLevel.SelectedIndex = Array.IndexOf(Preferences.LowLevels, preferences.LowLevel);
            startup.Checked = preferences.RunOnLogin;
            updating = false;
        }

        private void DisplayChanged(object sender, EventArgs e)
        {
            if (updating) return;
            try
            {
                applyDisplay(Preferences.Intervals[interval.SelectedIndex], Preferences.LowLevels[lowLevel.SelectedIndex],
                    (BarPlacement)placement.SelectedIndex);
            }
            catch (Exception)
            {
                ReadSettings();
                MessageBox.Show(this, "保存设置失败，请重试。", "外设电量");
            }
        }

        private void StartupChanged(object sender, EventArgs e)
        {
            if (updating) return;
            try { preferences.SetRunOnLogin(startup.Checked); }
            catch (Exception)
            {
                ReadSettings();
                MessageBox.Show(this, "未能更新登录启动设置。", "外设电量");
            }
        }

        private void ChangePage(int step)
        {
            page = Math.Max(0, Math.Min(PageCount - 1, page + step));
            UpdatePagination();
        }

        private void UpdatePagination()
        {
            page = Math.Min(page, PageCount - 1);
            previousPage.Enabled = page > 0;
            nextPage.Enabled = page + 1 < PageCount;
            pin.Enabled = readings().Length > 0;
            Invalidate();
        }

        private void PinPage()
        {
            try
            {
                pinDevices(readings().Skip(page * 2).Take(2).Select(item => item.Id).ToArray());
            }
            catch (Exception)
            {
                MessageBox.Show(this, "未能保存任务栏设备。", "外设电量");
            }
        }

        internal void SetRefreshState(bool running, DateTime? time)
        {
            busy = running;
            lastRefresh = time;
            refresh.Enabled = !running;
            refresh.Text = running ? "读取中…" : "立即刷新";
            UpdatePagination();
        }

        internal void SetPlacementStatus(string status)
        {
            if (currentPlacement == status) return;
            currentPlacement = status;
            Invalidate();
        }

        private static readonly RectangleF HeaderBounds = new RectangleF(20, 19, 616, 73);

        private static void DrawHeaderBackground(Graphics graphics)
        {
            Art.GradientFill(graphics, HeaderBounds, 14, Art.HeaderMint, Art.HeaderBlue, LinearGradientMode.Horizontal);
        }

        internal override void PaintButtonBackdrop(Graphics graphics, Rectangle buttonBounds)
        {
            base.PaintButtonBackdrop(graphics, buttonBounds);
            var headerPixels = new RectangleF(HeaderBounds.X * Art.Scale, HeaderBounds.Y * Art.Scale,
                HeaderBounds.Width * Art.Scale, HeaderBounds.Height * Art.Scale);
            if (!headerPixels.IntersectsWith(buttonBounds)) return;

            GraphicsState saved = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TranslateTransform(-buttonBounds.Left, -buttonBounds.Top);
                graphics.ScaleTransform(Art.Scale, Art.Scale);
                DrawHeaderBackground(graphics);
            }
            finally { graphics.Restore(saved); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Art.Border(g, new RectangleF(.5f, .5f, 655, 487), 14, Art.Line);
            DrawHeaderBackground(g);
            Art.Text(g, demo ? "Device Edition · 演示" : "Device Edition · 外设电量", 36, 27, 25, Art.Ink, TypeWeight.Bold);
            string stamp = busy ? "正在读取设备…" :
                lastRefresh.HasValue ? "上次刷新  " + lastRefresh.Value.ToString("HH:mm", CultureInfo.InvariantCulture) : "等待首次读取";
            Art.Text(g, stamp, 37, 61, 12, Art.Muted);

            Reading[] states = readings();
            int first = page * 2;
            DrawDevice(g, first < states.Length ? states[first] : Reading.Empty, 20);
            DrawDevice(g, first + 1 < states.Length ? states[first + 1] : Reading.Empty, 335);
            DrawSettings(g);
            if (currentPlacement != null) Art.Text(g, currentPlacement, 22, 448, 12, Art.Muted);
            Art.TextClipped(g, states.Length + " 台设备 · " + (page + 1) + "/" + PageCount, 390, 448, 101, 12, Art.Muted);
        }

        private static void DrawDevice(Graphics g, Reading state, int x)
        {
            var card = new RectangleF(x, 107, 301, 153);
            Art.Fill(g, card, 14, Color.White);
            Art.Border(g, card, 14, Art.Line);
            Art.Fill(g, new RectangleF(x + 14, 121, 31, 31), 9, Art.Paper);
            Art.Device(g, state.Kind, x + 19, 126, 21, Art.Muted);
            Art.TextClipped(g, state.Name, x + 54, 126, 230, 15, Art.Ink);
            Color tone = Art.Value(state);
            Art.Text(g, state.Value, x + 12, 151, 42, tone, TypeWeight.Bold);
            Art.TextRight(g, state.Status, x + 286, 169, 12, tone);
            Art.TextRight(g, state.Link, x + 286, 188, 11, Art.Muted);
            var track = new RectangleF(x + 15, 226, 271, 12);
            Art.Fill(g, track, 6, Art.Line);
            if (state.Level.HasValue && state.Level.Value > 0)
            {
                float filled = track.Width * state.Level.Value / 100f;
                Art.BatteryFill(g, new RectangleF(track.X, track.Y, filled, track.Height), Math.Min(6, filled / 2),
                    state.Level.Value, LinearGradientMode.Horizontal);
            }
        }

        private static void DrawSettings(Graphics g)
        {
            var card = new RectangleF(20, 274, 616, 156);
            Art.Fill(g, card, 14, Color.White);
            Art.Border(g, card, 14, Art.Line);
            Art.Text(g, "设置", 36, 285, 17, Art.Ink, TypeWeight.Bold);
            Art.Text(g, "显示位置", 37, 326, 13, Art.Ink, TypeWeight.Medium);
            Art.Text(g, "刷新频率 / 低电量变色", 37, 361, 13, Art.Ink, TypeWeight.Medium);
            Art.Text(g, "启动", 37, 398, 13, Art.Ink, TypeWeight.Medium);
            Art.Divider(g, 36, 351, 617, 351, Art.Line);
            Art.Divider(g, 36, 387, 617, 387, Art.Line);
        }
    }
}
