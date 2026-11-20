using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    internal sealed class LogitechLightspeedProtocol : IBatteryProtocol
    {
        private static readonly string[] Receivers =
        {
            "c539", "c53a", "c53d", "c53f", "c541", "c545", "c547", "c548", "c54d", "c52b", "c532"
        };

        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HidDevice device in snapshot.Interfaces.Where(item => item.Path.Contains("vid_046d&pid_")))
            {
                using (SafeFileHandle handle = Hid.Open(device.Path, true))
                {
                    int page, usage, input, length;
                    if (handle.IsInvalid || !Hid.ReportShape(handle, out page, out usage, out input, out length) ||
                        input != 20 || length != 20 ||
                        !((page == 0xff00 && usage == 2) || (page == 0xff43 && (usage == 0x0602 || usage == 0x0202)))) continue;
                    string receiver = Pnp.ContainerId(device.Node) ?? device.Path;
                    if (seen.Contains(receiver)) continue;
                    bool isReceiver = Receivers.Any(pid => device.Path.Contains("vid_046d&pid_" + pid));
                    bool matched = false;
                    byte[] slots = isReceiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xff, 1 };
                    foreach (byte index in slots)
                    {
                        string name = ReadName(handle, index);
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        Reading reading = ReadBattery(handle, index, receiver, name, isReceiver);
                        if (reading.Level.HasValue || IsG915X(name))
                        {
                            output.Add(reading);
                            matched = true;
                        }
                        if (!isReceiver && matched) break;
                    }
                    if (matched)
                    {
                        seen.Add(receiver);
                        snapshot.ClaimedPaths.Add(device.Path);
                    }
                }
            }
        }

        internal static bool IsG915X(string name)
        {
            return !string.IsNullOrWhiteSpace(name) &&
                name.Replace(" ", "").IndexOf("G915X", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string ReadName(SafeFileHandle handle, byte index)
        {
            byte feature = FindFeature(handle, index, 0x0005);
            if (feature == 0) return null;
            byte[] reply = Request(handle, index, feature, 0);
            if (reply == null || reply[4] == 0 || reply[4] > 96) return null;
            int length = reply[4];
            var bytes = new List<byte>(length);
            while (bytes.Count < length)
            {
                reply = Request(handle, index, feature, 0x10, (byte)bytes.Count);
                if (reply == null) return null;
                int chunk = Math.Min(length - bytes.Count, reply[0] == 0x11 ? 16 : 3);
                if (chunk <= 0 || reply.Length < 4 + chunk) return null;
                bytes.AddRange(reply.Skip(4).Take(chunk));
            }
            return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\0').Trim();
        }

        private static Reading ReadBattery(SafeFileHandle handle, byte index, string receiver, string name, bool isReceiver)
        {
            int? level = null;
            bool charging = false, estimated = false;
            byte feature = FindFeature(handle, index, 0x1004);
            if (feature != 0)
            {
                byte[] capabilities = Request(handle, index, feature, 0);
                if (capabilities != null && (capabilities[5] & 2) != 0)
                {
                    byte[] status = Request(handle, index, feature, 0x10);
                    if (status != null && status[4] <= 100)
                    {
                        level = status[4];
                        charging = status[6] == 1 || status[6] == 2;
                    }
                }
            }
            if (!level.HasValue)
            {
                feature = FindFeature(handle, index, 0x1000);
                if (feature != 0)
                {
                    byte[] capabilities = Request(handle, index, feature, 0x10);
                    byte[] status = capabilities != null && capabilities[4] >= 10 && (capabilities[5] & 2) != 0
                        ? Request(handle, index, feature, 0) : null;
                    if (status != null)
                    {
                        level = ReadLegacyPercent(status[4], status[6]);
                        charging = status[6] == 1 || status[6] == 2 || status[6] == 4;
                    }
                }
            }
            DeviceKind kind = ReadDeviceKind(handle, index, name);
            if (!level.HasValue)
            {
                feature = FindFeature(handle, index, kind == DeviceKind.Headset ? (ushort)0x1f20 : (ushort)0x1001);
                byte[] status = feature == 0 ? null : Request(handle, index, feature, 0);
                if (status != null)
                {
                    int millivolts = (status[4] << 8) | status[5];
                    bool headset = kind == DeviceKind.Headset;
                    bool full = headset ? status[6] == 7 : (status[6] & 0x87) == 0x81;
                    charging = headset ? status[6] == 3 : (status[6] & 0x87) == 0x80;
                    // Charging voltage is not a reliable state-of-charge estimate.
                    if (full) level = 100;
                    else if (!charging && (headset ? status[6] == 1 : (status[6] & 0x80) == 0))
                    {
                        level = EstimateCharge(millivolts, headset);
                        estimated = level.HasValue;
                    }
                }
            }
            string label = IsG915X(name) && name.IndexOf("TKL", StringComparison.OrdinalIgnoreCase) >= 0
                ? "G915 X LIGHTSPEED TKL" : name;
            string statusText = charging ? "充电中" : (level.HasValue ? (estimated ? "电量估算" : "已读取") : "暂时读不到电量");
            return new Reading(level, statusText, isReceiver ? "USB 接收器" : "USB",
                "gearpair:logitech:" + receiver.ToLowerInvariant() + ":" + index, label, kind, "Logitech");
        }

        private static DeviceKind ReadDeviceKind(SafeFileHandle handle, byte index, string name)
        {
            if (IsG915X(name)) return DeviceKind.Keyboard;
            if (name.IndexOf("headset", StringComparison.OrdinalIgnoreCase) >= 0 ||
                new[] { "G533", "G633", "G635", "G733", "G933", "G935" }.Any(model =>
                    name.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0)) return DeviceKind.Headset;
            byte feature = FindFeature(handle, index, 0x0005);
            byte[] kind = feature == 0 ? null : Request(handle, index, feature, 0x20);
            if (kind != null && kind[4] == 0) return DeviceKind.Keyboard;
            if (kind != null && kind[4] == 3) return DeviceKind.Mouse;
            return name.IndexOf("mouse", StringComparison.OrdinalIgnoreCase) >= 0 ? DeviceKind.Mouse : DeviceKind.Generic;
        }

        // Ten-percent thresholds from Linux's HID++ voltage and headset ADC curves.
        private static readonly int[] MouseVoltage = { 3500, 3671, 3717, 3751, 3778, 3811, 3859, 3922, 3989, 4067, 4186 };
        private static readonly int[] HeadsetVoltage = { 3399, 3543, 3579, 3611, 3640, 3674, 3712, 3757, 3820, 3937, 4030 };

        internal static int? EstimateCharge(int millivolts, bool headset)
        {
            if (millivolts < 3250 || millivolts > 4500) return null;
            int[] curve = headset ? HeadsetVoltage : MouseVoltage;
            for (int i = 1; i < curve.Length; i++)
                if (millivolts <= curve[i])
                    return (i - 1) * 10 + Math.Max(0, (millivolts - curve[i - 1]) * 10 / (curve[i] - curve[i - 1]));
            return 100;
        }

        internal static int? ReadLegacyPercent(byte capacity, byte status)
        {
            if (status == 3) return 100;
            if (status == 0 && capacity <= 100) return capacity;
            return null;
        }

        private static byte FindFeature(SafeFileHandle handle, byte index, ushort feature)
        {
            byte[] reply = Request(handle, index, 0, 0, (byte)(feature >> 8), (byte)(feature & 0xff));
            return reply == null ? (byte)0 : reply[4];
        }

        private static byte softwareId = 7;
        private static byte[] Request(SafeFileHandle handle, byte index, byte feature, byte function, params byte[] parameters)
        {
            softwareId = softwareId >= 14 ? (byte)7 : (byte)(softwareId + 1);
            byte address = (byte)(function | softwareId);
            var request = new byte[20];
            request[0] = 0x11;
            request[1] = index;
            request[2] = feature;
            request[3] = address;
            Buffer.BlockCopy(parameters, 0, request, 4, parameters.Length);
            if (Hid.Transfer(handle, request, true, 200) != request.Length) return null;

            var clock = Stopwatch.StartNew();
            var response = new byte[20];
            while (clock.ElapsedMilliseconds < 280)
            {
                int count = Hid.Transfer(handle, response, false, Math.Max(1, 280 - (int)clock.ElapsedMilliseconds));
                if (count <= 0) return null;
                int match = MatchResponse(response, count, index, feature, address);
                if (match > 0) return response;
                if (match < 0) return null;
            }
            return null;
        }

        internal static int MatchResponse(byte[] report, int count, byte index, byte feature, byte address)
        {
            if (count < 7 || report[1] != index ||
                (report[0] != 0x10 && (report[0] != 0x11 || count < 20))) return 0;
            if ((report[2] == 0xff || report[2] == 0x8f) && report[3] == feature && report[4] == address)
                return -1;
            return report[2] == feature && report[3] == address ? 1 : 0;
        }
    }

    internal sealed class PulsarX2HProtocol : IBatteryProtocol
    {
        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            var candidates = snapshot.Interfaces.Where(device => KnownPath(device.Path))
                .OrderBy(device => device.Path.Contains("pid_f507") ? 0 : 1);
            foreach (HidDevice device in candidates)
            {
                using (SafeFileHandle handle = Hid.Open(device.Path, true))
                {
                    int page, usage, input, length;
                    if (handle.IsInvalid || !Hid.ReportShape(handle, out page, out usage, out input, out length) ||
                        page < 0xff00 || input != 17 || length != 17 || !Hid.HasReportId(handle, 0, 8) ||
                        !Hid.HasReportId(handle, 1, 8)) continue;
                    bool wired = device.Path.Contains("pid_f507");
                    if (!wired)
                    {
                        byte[] status = CompXMouse.Exchange(handle, CompXMouse.BuildFrame(3));
                        if (status == null || status[6] != 1) continue;
                    }
                    byte[] power = CompXMouse.Exchange(handle, CompXMouse.BuildFrame(4));
                    if (power == null || power[6] > 100 || power[7] > 1) continue;

                    byte[] identifying = CompXMouse.BuildFrame(1);
                    identifying[5] = 8;
                    byte[] challenge = BitConverter.GetBytes(Environment.TickCount);
                    Buffer.BlockCopy(challenge, 0, identifying, 6, challenge.Length);
                    identifying[16] = CompXMouse.Checksum(identifying);
                    int? model = ModelId(identifying, CompXMouse.Exchange(handle, identifying));
                    string product = Hid.ProductName(device.Path);
                    string name = model == 0x060c || model == 0x060d ? "Pulsar X2H" :
                        (product != null && product.IndexOf("X2H", StringComparison.OrdinalIgnoreCase) >= 0
                            ? product : "Pulsar 无线鼠标");
                    output.Add(new Reading(power[6], power[7] == 1 ? "有线供电" : "已读取",
                        wired ? "USB 有线" : "2.4 GHz", "gearpair:pulsar:x2h", name, DeviceKind.Mouse, "Pulsar"));
                    return;
                }
            }
        }

        private static bool KnownPath(string path)
        {
            return path.Contains("&mi_01") &&
                (path.Contains("vid_3554&pid_f507") || path.Contains("vid_3554&pid_f508") ||
                    path.Contains("vid_3554&pid_f509"));
        }

        private static int? ModelId(byte[] sent, byte[] reply)
        {
            if (reply == null) return null;
            for (int i = 0; i < 4; i++)
            {
                int value = reply[6 + i] - (i + 1) * sent[6 + i] - sent[6 + (i + 1) % 4];
                byte expected = i < 2 ? reply[10 + i] : (byte)0;
                if (unchecked((byte)value) != expected) return null;
            }
            return (reply[10] << 8) | reply[11];
        }

    }

    internal static class CompXMouse
    {
        internal static byte[] BuildFrame(byte command)
        {
            var frame = new byte[17];
            frame[0] = 8;
            frame[1] = command;
            frame[16] = Checksum(frame);
            return frame;
        }

        internal static byte Checksum(byte[] frame)
        {
            int remaining = 0x55;
            for (int i = 0; i < 16; i++) remaining -= frame[i];
            return unchecked((byte)remaining);
        }

        internal static bool IsReply(byte[] data, int count, byte command)
        {
            return count == 17 && data[0] == 8 && data[1] == command && data[16] == Checksum(data);
        }

        internal static byte[] Exchange(SafeFileHandle handle, byte[] request, bool interruptFirst = false)
        {
            return Send(handle, request, !interruptFirst) ?? Send(handle, request, interruptFirst);
        }

        private static byte[] Send(SafeFileHandle handle, byte[] request, bool control)
        {
            bool sent = control ? Hid.HidD_SetOutputReport(handle, request, request.Length) :
                Hid.Transfer(handle, request, true, 180) == request.Length;
            BatteryDiagnostics.Add("CompX " + (control ? "control" : "interrupt") +
                " output: " + (sent ? "sent" : "failed"));
            return sent ? Receive(handle, request[1]) : null;
        }

        private static byte[] Receive(SafeFileHandle handle, byte command)
        {
            var clock = Stopwatch.StartNew();
            var response = new byte[17];
            int unmatched = 0;
            while (clock.ElapsedMilliseconds < 250)
            {
                int count = Hid.Transfer(handle, response, false, Math.Max(1, 250 - (int)clock.ElapsedMilliseconds));
                if (count <= 0) break;
                if (IsReply(response, count, command)) return response;
                unmatched++;
            }
            BatteryDiagnostics.Add("CompX: no valid reply; unmatched reports=" + unmatched);
            return null;
        }
    }

    internal static partial class Hid
    {
        internal static bool ReportShape(SafeFileHandle handle, out int page, out int usage, out int input, out int output)
        {
            page = usage = input = output = 0;
            IntPtr descriptor;
            if (!HidD_GetPreparsedData(handle, out descriptor)) return false;
            IntPtr caps = Marshal.AllocHGlobal(64);
            try
            {
                if (HidP_GetCaps(descriptor, caps) != 0x00110000) return false;
                usage = (ushort)Marshal.ReadInt16(caps);
                page = (ushort)Marshal.ReadInt16(caps, 2);
                input = (ushort)Marshal.ReadInt16(caps, 4);
                output = (ushort)Marshal.ReadInt16(caps, 6);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(caps);
                HidD_FreePreparsedData(descriptor);
            }
        }

        internal static bool HasReportId(SafeFileHandle handle, int type, byte reportId)
        {
            IntPtr descriptor;
            if (!HidD_GetPreparsedData(handle, out descriptor)) return false;
            IntPtr caps = Marshal.AllocHGlobal(64);
            try
            {
                if (HidP_GetCaps(descriptor, caps) != 0x00110000) return false;
                int offset = type == 0 ? 46 : 52;
                for (int kind = 0; kind < 2; kind++)
                {
                    ushort count = (ushort)Marshal.ReadInt16(caps, offset + kind * 2);
                    if (count == 0 || count > 1024) continue;
                    IntPtr fields = Marshal.AllocHGlobal(count * 72);
                    try
                    {
                        int status = kind == 0 ? HidP_GetButtonCaps(type, fields, ref count, descriptor)
                            : HidP_GetValueCaps(type, fields, ref count, descriptor);
                        if (status != 0x00110000) continue;
                        for (int i = 0; i < count; i++)
                            if (Marshal.ReadByte(fields, i * 72 + 2) == reportId) return true;
                    }
                    finally { Marshal.FreeHGlobal(fields); }
                }
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(caps);
                HidD_FreePreparsedData(descriptor);
            }
        }

        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
        internal static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] data, int length);
        [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int type, IntPtr fields,
            ref ushort length, IntPtr descriptor);
        [DllImport("hid.dll")] private static extern int HidP_GetValueCaps(int type, IntPtr fields,
            ref ushort length, IntPtr descriptor);
    }
}
