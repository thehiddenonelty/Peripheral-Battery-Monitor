using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    internal sealed class StandardHidProtocol : IBatteryProtocol
    {
        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDevice device in snapshot.Interfaces.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (snapshot.ClaimedPaths.Contains(device.Path)) continue;
                string instance = Pnp.InstanceId(device.Node);
                // Only USB-backed HID collections. Bluetooth is handled separately.
                if (instance == null || !instance.StartsWith(@"HID\VID_", StringComparison.OrdinalIgnoreCase) ||
                    !Pnp.IsUsbBacked(device.Node)) continue;
                string id = "hid:" + (Pnp.ContainerId(device.Node) ?? instance).ToLowerInvariant();
                if (seen.Contains(id)) continue;

                DeviceKind kind;
                int? level = Hid.StandardBattery(device.Path, out kind);
                if (!level.HasValue) continue;
                seen.Add(id);
                output.Add(new Reading(level, "已读取", "USB HID", id,
                    Hid.ProductName(device.Path) ?? Pnp.Name(device.Node) ?? "USB 外设",
                    kind, "标准 HID"));
            }
        }
    }

    internal static partial class Hid
    {
        private const int HidSuccess = 0x00110000;
        private const int FeatureReport = 2;
        private const ushort DeviceControls = 0x06;
        private const ushort BatteryStrength = 0x20;

        [StructLayout(LayoutKind.Explicit, Size = 72)]
        private struct ValueCaps
        {
            [FieldOffset(2)] internal byte ReportId;
            [FieldOffset(6)] internal ushort Link;
            [FieldOffset(18)] internal ushort Bits;
            [FieldOffset(20)] internal ushort Count;
            [FieldOffset(40)] internal int LogicalMin;
            [FieldOffset(44)] internal int LogicalMax;
        }

        internal static int? StandardBattery(string path, out DeviceKind kind)
        {
            kind = DeviceKind.Generic;
            using (SafeFileHandle info = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                IntPtr descriptor;
                if (info.IsInvalid || !HidD_GetPreparsedData(info, out descriptor)) return null;
                IntPtr caps = Marshal.AllocHGlobal(64);
                try
                {
                    if (HidP_GetCaps(descriptor, caps) != HidSuccess) return null;
                    if (Marshal.ReadInt16(caps, 2) == 1 && Marshal.ReadInt16(caps, 0) == 2)
                        kind = DeviceKind.Mouse;
                    int length = (ushort)Marshal.ReadInt16(caps, 8);
                    ushort count = (ushort)Marshal.ReadInt16(caps, 60);
                    if (length < 2 || count == 0 || count > 1024) return null;
                    var fields = new ValueCaps[count];
                    if (HidP_GetSpecificValueCaps(FeatureReport, DeviceControls, 0, BatteryStrength, fields, ref count, descriptor) != HidSuccess)
                        return null;
                    for (int i = 0; i < count; i++)
                    {
                        ValueCaps field = fields[i];
                        if (field.Count != 1 || field.Bits == 0 || field.Bits > 32 || field.LogicalMax <= field.LogicalMin) continue;
                        var report = new byte[length];
                        report[0] = field.ReportId;
                        if (!HidD_GetFeature(info, report, length))
                        {
                            using (SafeFileHandle access = Open(path, false))
                            {
                                report[0] = field.ReportId;
                                if (access.IsInvalid || !HidD_GetFeature(access, report, length)) continue;
                            }
                        }
                        uint raw;
                        if (HidP_GetUsageValue(FeatureReport, DeviceControls, field.Link, BatteryStrength,
                            out raw, descriptor, report, length) != HidSuccess) continue;
                        int? percent = NormalizeLevel(raw, field.Bits, field.LogicalMin, field.LogicalMax);
                        if (percent.HasValue) return percent;
                    }
                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(caps);
                    HidD_FreePreparsedData(descriptor);
                }
            }
        }

        internal static int? NormalizeLevel(uint raw, int bits, int min, int max)
        {
            if (bits < 1 || bits > 32 || max <= min) return null;
            long value = raw;
            long limit = 1L << bits;
            if (value >= limit) return null;
            if (min < 0 && (value & (limit >> 1)) != 0) value -= limit;
            if (value < min || value > max) return null;
            return (int)Math.Round((value - min) * 100d / ((long)max - min), MidpointRounding.AwayFromZero);
        }

        internal static string ProductName(string path)
        {
            using (SafeFileHandle info = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                var text = new byte[512];
                if (info.IsInvalid || !HidD_GetProductString(info, text, text.Length)) return null;
                string label = System.Text.Encoding.Unicode.GetString(text).TrimEnd('\0').Trim();
                return label.Length == 0 ? null : label;
            }
        }

        [DllImport("hid.dll")] private static extern int HidP_GetSpecificValueCaps(int report, ushort page,
            ushort link, ushort usage, [Out] ValueCaps[] values, ref ushort length, IntPtr descriptor);
        [DllImport("hid.dll")] private static extern int HidP_GetUsageValue(int report, ushort page,
            ushort link, ushort usage, out uint value, IntPtr descriptor, byte[] buffer, int length);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetProductString(SafeFileHandle file, [Out] byte[] buffer, int length);
    }
}
