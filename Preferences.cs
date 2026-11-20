using System;
using System.Windows.Forms;
using System.Globalization;
using Microsoft.Win32;

namespace BatteryBar
{
    internal enum BarPlacement { Taskbar, Above, TrayOnly }

    internal sealed class Preferences
    {
        internal static readonly int[] Intervals = { 1, 3, 5, 10, 15 };
        internal static readonly int[] LowLevels = { 10, 20, 30 };
        private const string SettingsKey = @"Software\BatteryBarDeviceEdition";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "BatteryBarDeviceEdition";
        private readonly bool demo;
        internal int Interval = 5;
        internal int LowLevel = 20;
        internal BarPlacement Placement = BarPlacement.Taskbar;
        internal bool RunOnLogin;
        internal string[] PinnedDevices = new string[0];

        internal static bool IsLightTaskbar()
        {
            try
            {
                return Equals(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "SystemUsesLightTheme", 1), 1);
            }
            catch (Exception) { return false; }
        }

        internal Preferences(bool demo)
        {
            this.demo = demo;
            if (demo) return;
            try
            {
                using (RegistryKey settings = Registry.CurrentUser.OpenSubKey(SettingsKey))
                {
                    if (settings != null)
                    {
                        PinnedDevices = settings.GetValue("PinnedDevices") as string[] ?? new string[0];
                        string[] fields = (Convert.ToString(settings.GetValue("Display", ""), CultureInfo.InvariantCulture) ?? "").Split('|');
                        int interval, lowLevel, placement;
                        if (fields.Length == 3 && int.TryParse(fields[0], out interval) &&
                            int.TryParse(fields[1], out lowLevel) && int.TryParse(fields[2], out placement) &&
                            Array.IndexOf(Intervals, interval) >= 0 && Array.IndexOf(LowLevels, lowLevel) >= 0 &&
                            placement >= 0 && placement <= 2)
                        {
                            Interval = interval;
                            LowLevel = lowLevel;
                            Placement = (BarPlacement)placement;
                        }
                    }
                }
                string launch;
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey))
                    launch = run == null ? null : run.GetValue(RunName) as string;
                RunOnLogin = launch != null;
                if (RunOnLogin)
                {
                    string executable = System.IO.Path.GetFileName(launch.Trim('"'));
                    if (string.Equals(executable, "BatteryBar-DeviceEdition-0.1.0.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(executable, "BatteryBar-DeviceEdition-0.1.1.exe", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(executable, "BatteryBar-DeviceEdition-0.1.2.exe", StringComparison.OrdinalIgnoreCase))
                        SetRunOnLogin(true);
                }
            }
            catch (Exception) { }
        }

        internal void SaveDisplay(int interval, int lowLevel, BarPlacement placement)
        {
            if (!demo)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    key.SetValue("Display", string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", interval, lowLevel, (int)placement));
            }
            Interval = interval;
            LowLevel = lowLevel;
            Placement = placement;
        }

        internal void SavePinnedDevices(string[] ids)
        {
            if (!demo)
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    key.SetValue("PinnedDevices", ids, RegistryValueKind.MultiString);
            }
            PinnedDevices = ids;
        }

        internal void SetRunOnLogin(bool enabled)
        {
            if (demo) return;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue(RunName, false);
            }
            RunOnLogin = enabled;
        }
    }
}
