using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
#if BATTERY_UI_AUTOMATION
using System.Windows.Automation;
#endif

namespace BatteryBar
{
    internal sealed class TaskbarLayout
    {
        internal readonly IntPtr Window;
        internal readonly Rectangle Bounds;
        internal readonly Rectangle Tray;
        internal readonly Point? Position;
        internal readonly DateTime Captured;

        internal TaskbarLayout(IntPtr window, Rectangle bounds, Rectangle tray, Point? position)
        {
            Window = window;
            Bounds = bounds;
            Tray = tray;
            Position = position;
            Captured = DateTime.UtcNow;
        }

        internal bool SameBand(TaskbarLayout other)
        {
            return other != null && Window == other.Window && Bounds == other.Bounds && Tray == other.Tray;
        }
    }

    internal static class TaskbarDock
    {
        internal static readonly bool CanInspect =
#if BATTERY_UI_AUTOMATION
            true;
#else
            false;
#endif

        internal static TaskbarLayout Band(Rectangle screen, Size widget)
        {
            IntPtr bar = FindWindow("Shell_TrayWnd", null);
            Rect barRect;
            if (bar == IntPtr.Zero || !IsWindowVisible(bar) || !GetWindowRect(bar, out barRect))
                return null;
            Rectangle bounds = Rectangle.Intersect(barRect.ToRectangle(), screen);
            if (bounds.Width < screen.Width / 2 || bounds.Height < widget.Height || bounds.Height >= bounds.Width)
                return null;

            IntPtr tray = FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
            Rect trayRect;
            Rectangle notificationArea = Rectangle.Empty;
            if (tray != IntPtr.Zero && IsWindowVisible(tray) && GetWindowRect(tray, out trayRect))
            {
                Rectangle candidate = Rectangle.Intersect(bounds, trayRect.ToRectangle());
                if (ValidTray(bounds, candidate)) notificationArea = candidate;
            }
            return new TaskbarLayout(bar, bounds, notificationArea, null);
        }

        internal static TaskbarLayout Inspect(TaskbarLayout band, Size widget, int gap)
        {
#if BATTERY_UI_AUTOMATION
            try
            {
                AutomationElement root = AutomationElement.FromHandle(band.Window);
                var clickable = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.SystemTrayFrame"),
                    new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.SystemTrayFrameAutomationPeer"),
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayFrame"));
                var cached = new CacheRequest();
                cached.Add(AutomationElement.BoundingRectangleProperty);
                cached.Add(AutomationElement.IsOffscreenProperty);
                cached.Add(AutomationElement.ClassNameProperty);
                cached.Add(AutomationElement.AutomationIdProperty);
                cached.AutomationElementMode = AutomationElementMode.None;
                var occupied = new List<Rectangle>();
                Rectangle tray = band.Tray;
                using (cached.Activate())
                {
                    foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, clickable))
                    {
                        if (element.Cached.IsOffscreen) continue;
                        System.Windows.Rect item = element.Cached.BoundingRectangle;
                        if (item.IsEmpty || item.Width <= 1 || item.Height <= 1) continue;
                        var pixels = Rectangle.FromLTRB((int)Math.Floor(item.Left), (int)Math.Floor(item.Top),
                            (int)Math.Ceiling(item.Right), (int)Math.Ceiling(item.Bottom));
                        if (!pixels.IntersectsWith(band.Bounds)) continue;
                        string className = element.Cached.ClassName ?? "";
                        if (className.StartsWith("SystemTray.SystemTrayFrame", StringComparison.Ordinal) ||
                            element.Cached.AutomationId == "SystemTrayFrame")
                        {
                            Rectangle candidate = Rectangle.Intersect(band.Bounds, pixels);
                            if (ValidTray(band.Bounds, candidate) && (tray.IsEmpty || candidate.Left < tray.Left))
                                tray = candidate;
                        }
                        else occupied.Add(pixels);
                    }
                }
                Point? result = FindPosition(band.Bounds, tray, occupied, widget, gap);
                return new TaskbarLayout(band.Window, band.Bounds, band.Tray, result);
            }
            catch (Exception)
            {
                // Explorer may be restarting or unavailable to UI Automation.
                return new TaskbarLayout(band.Window, band.Bounds, band.Tray, null);
            }
#else
            return band;
#endif
        }

        private static bool ValidTray(Rectangle band, Rectangle tray)
        {
            return tray.Width > 0 && tray.Height > 0 && tray.Left >= band.Left + band.Width / 2 &&
                tray.Right <= band.Right && tray.Right >= band.Right - band.Height && band.IntersectsWith(tray);
        }

        internal static Point? FindPosition(Rectangle band, Rectangle tray, IList<Rectangle> occupied, Size widget, int gap)
        {
            if (!ValidTray(band, tray) || widget.Height > band.Height) return null;
            int limit = tray.Left;
            int lastTask = band.Left;
            int y = band.Top + (band.Height - widget.Height) / 2;
            var row = new Rectangle(band.Left, y, limit - band.Left, widget.Height);
            bool taskButtonsSeen = false;
            foreach (Rectangle item in occupied)
            {
                if (!row.IntersectsWith(item)) continue;
                taskButtonsSeen = true;
                lastTask = Math.Max(lastTask, item.Right);
            }

            int x = limit - gap - widget.Width;
            if (!taskButtonsSeen || x < lastTask + gap) return null;
            return new Point(x, y);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string name, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string name, string title);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    }
}
