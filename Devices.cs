using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    internal enum DeviceKind { Generic, Mouse, Headset, Keyboard }

    internal sealed class Reading
    {
        internal readonly int? Level;
        internal readonly string Status;
        internal readonly string Link;
        internal readonly DateTime? Updated;
        internal readonly string Id, Name, Source;
        internal readonly DeviceKind Kind;
        internal static readonly Reading Empty = new Reading(null, "无可显示设备", "—", null, "—");


        internal Reading(int? level, string status, string link, string id = null, string name = null,
            DeviceKind kind = DeviceKind.Generic, string source = null, bool fresh = true)
        {
            Level = level;
            Status = status;
            Link = link;
            Updated = level.HasValue && fresh ? DateTime.Now : (DateTime?)null;
            Id = id;
            Name = name;
            Kind = kind;
            Source = source;

        }

        internal string Value { get { return Level.HasValue ? Level.Value + "%" : "—"; } }
    }

    internal static class Protocol
    {
        internal static byte RazerCrc(byte[] frame)
        {
            byte checksum = 0;
            for (int i = 3; i <= 88; i++) checksum ^= frame[i];
            return checksum;
        }

        internal static byte[] MouseRequest(byte command, byte transaction = 0x1f)
        {
            byte[] bytes = new byte[91];
            bytes[2] = transaction;
            bytes[6] = 2;
            bytes[7] = 7;
            bytes[8] = command;
            bytes[89] = RazerCrc(bytes);
            return bytes;
        }

        internal static int? MouseValue(byte[] bytes, byte command, byte transaction = 0x1f)
        {
            if (bytes.Length != 91 || bytes[0] != 0 || bytes[1] != 2 || bytes[2] != transaction ||
                bytes[6] < 2 || bytes[7] != 7 || bytes[8] != command || bytes[89] != RazerCrc(bytes))
                return null;
            return bytes[10];
        }

        internal static byte[] HeadsetRequest(ushort transaction, int length)
        {
            byte[] report = new byte[length];
            byte[] packet = { 0x02, 12, 0x01, 0x00, 0xfc, 8, 0x96, 0xc3, 0x41, 0x04, 0x01,
                unchecked((byte)transaction), (byte)(transaction >> 8), 0 };
            int sum = 0;
            for (int i = 6; i < 13; i++) sum += packet[i];
            packet[13] = unchecked((byte)sum);
            Buffer.BlockCopy(packet, 0, report, 0, packet.Length);
            return report;
        }

        internal static int? HeadsetValue(byte[] report, int count, ushort transaction)
        {
            if (count < 16 || count > report.Length || report[0] != 2 || report[1] < 14 ||
                report[1] > count - 2 || report[4] + 3 != report[1] ||
                report[2] != 4 || report[3] != 0xff || report[5] != 0 || report[6] != 0x96 ||
                report[7] != 0xc3 || report[8] != 0x14 || report[9] != 4 ||
                report[10] != 0x10 || (report[11] | (report[12] << 8)) != transaction)
                return null;
            int last = 1 + report[1];
            int sum = 0;
            for (int i = 5; i < last; i++) sum += report[i];
            return unchecked((byte)sum) == report[last] && report[14] <= 100 ? report[14] : (int?)null;
        }
    }

    internal sealed class VendorProtocol : IBatteryProtocol
    {
        private static int sonyTransaction = Environment.TickCount;

        private static bool Claims(string path)
        {
            return path.Contains("vid_1532&pid_00e5") || path.Contains("vid_1532&pid_00e6") ||
                path.Contains("vid_054c&pid_0ebf");
        }

        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            List<string> paths = snapshot.Paths;
            snapshot.ClaimedPaths.UnionWith(paths.FindAll(Claims));
            if (paths.Exists(p => p.Contains("vid_1532&pid_00e5") || p.Contains("vid_1532&pid_00e6")))
            {
                Reading state = ReadMouse(paths);
                output.Add(new Reading(state.Level, state.Status, state.Link,
                    "vendor:viper-v4-pro", "Viper V4 Pro", DeviceKind.Mouse, "Razer"));
            }
            if (paths.Exists(p => p.Contains("vid_054c&pid_0ebf")))
            {
                Reading state = ReadHeadset(paths);
                output.Add(new Reading(state.Level, state.Status, state.Link,
                    "vendor:inzone-h5", "INZONE H5", DeviceKind.Headset, "Sony"));
            }
        }

        private static Reading ReadMouse(List<string> paths)
        {
            bool found = false;
            foreach (string filter in new[] { "vid_1532&pid_00e5", "vid_1532&pid_00e6" })
            {
                string link = filter.EndsWith("00e5") ? "USB 有线" : "2.4 GHz";
                foreach (string path in paths)
                {
                    if (!path.Contains(filter)) continue;
                    found = true;
                    if (!path.Contains("&mi_03")) continue;
                    using (SafeFileHandle handle = Hid.Open(path, false))
                    {
                        int page, input, output, feature;
                        if (handle.IsInvalid || !Hid.Capabilities(handle, out page, out input, out output, out feature) ||
                            feature != 91) continue;
                        int? raw = MouseQuery(handle, 0x80);
                        if (!raw.HasValue) continue;
                        int? charging = MouseQuery(handle, 0x84);
                        int percent = (raw.Value * 100 + 127) / 255;
                        return new Reading(percent, charging == 1 ? "充电中" : "已连接", link);
                    }
                }
            }
            return new Reading(null, found ? "暂时读不到电量" : "未检测到设备", "USB 接收器");
        }

        internal static int? MouseQuery(SafeFileHandle handle, byte command, byte transaction = 0x1f)
        {
            if (!Hid.HidD_SetFeature(handle, Protocol.MouseRequest(command, transaction), 91)) return null;
            byte[] reply = new byte[91];
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Thread.Sleep(attempt == 0 ? 12 : 32);
                reply[0] = 0;
                if (!Hid.HidD_GetFeature(handle, reply, reply.Length)) return null;
                if (reply[1] >= 3 && reply[7] == 7 && reply[8] == command) return null;
                int? result = Protocol.MouseValue(reply, command, transaction);
                if (result.HasValue) return result;
            }
            return null;
        }

        private static Reading ReadHeadset(List<string> paths)
        {
            bool found = false;
            foreach (string path in paths)
            {
                if (!path.Contains("vid_054c&pid_0ebf")) continue;
                found = true;
                using (SafeFileHandle handle = Hid.Open(path, true))
                {
                    int page, input, output, feature;
                    if (handle.IsInvalid || !Hid.Capabilities(handle, out page, out input, out output, out feature) ||
                        page != 0xff04 || input < 16 || output < 14) continue;
                    ushort transaction = unchecked((ushort)Interlocked.Increment(ref sonyTransaction));
                    byte[] request = Protocol.HeadsetRequest(transaction, output);
                    if (Hid.Transfer(handle, request, true, 450) != request.Length) continue;
                    Stopwatch clock = Stopwatch.StartNew();
                    byte[] reply = new byte[input];
                    while (clock.ElapsedMilliseconds < 1450)
                    {
                        int count = Hid.Transfer(handle, reply, false, Math.Max(1, 1450 - (int)clock.ElapsedMilliseconds));
                        if (count <= 0) break;
                        int? result = Protocol.HeadsetValue(reply, count, transaction);
                        if (result.HasValue) return new Reading(result, "已连接", "2.4 GHz");
                    }
                }
            }
            return new Reading(null, found ? "暂时读不到电量" : "未检测到接收器", "2.4 GHz");
        }
    }

    internal static partial class Hid
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct InterfaceData
        {
            internal int Size;
            internal Guid Class;
            internal int Flags;
            internal IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Overlap
        {
            internal IntPtr InternalLow, InternalHigh;
            internal int Offset, OffsetHigh;
            internal IntPtr Event;
        }

        internal static List<HidDevice> Discover()
        {
            Guid guid;
            HidD_GetHidGuid(out guid);
            var paths = new List<HidDevice>();
            IntPtr set = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) return paths;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var data = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    int size;
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out size, IntPtr.Zero);
                    if (size < 8 || size > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        var node = new Pnp.DeviceInfo { Size = Marshal.SizeOf(typeof(Pnp.DeviceInfo)) };
                        if (GetInterfaceWithNode(set, ref data, detail, size, out size, ref node))
                            paths.Add(new HidDevice(Marshal.PtrToStringUni(IntPtr.Add(detail, 4)).ToLowerInvariant(), node.Node));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return paths;
        }

        internal static SafeFileHandle Open(string path, bool overlapped)
        {
            return CreateFile(path, 0xc0000000, 3, IntPtr.Zero, 3, overlapped ? 0x40000000u : 0u, IntPtr.Zero);
        }

        internal static bool Capabilities(SafeFileHandle handle, out int page, out int input, out int output, out int feature)
        {
            page = input = output = feature = 0;
            IntPtr descriptor;
            if (!HidD_GetPreparsedData(handle, out descriptor)) return false;
            IntPtr caps = Marshal.AllocHGlobal(64);
            try
            {
                if (HidP_GetCaps(descriptor, caps) != 0x00110000) return false;
                page = (ushort)Marshal.ReadInt16(caps, 2);
                input = (ushort)Marshal.ReadInt16(caps, 4);
                output = (ushort)Marshal.ReadInt16(caps, 6);
                feature = (ushort)Marshal.ReadInt16(caps, 8);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(caps);
                HidD_FreePreparsedData(descriptor);
            }
        }

        internal static int Transfer(SafeFileHandle handle, byte[] bytes, bool write, int timeout)
        {
            using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset))
            {
                IntPtr overlap = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Overlap)));
                GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    Marshal.StructureToPtr(new Overlap { Event = ready.SafeWaitHandle.DangerousGetHandle() }, overlap, false);
                    int transferred;
                    bool ok = write
                        ? WriteFile(handle, pin.AddrOfPinnedObject(), bytes.Length, out transferred, overlap)
                        : ReadFile(handle, pin.AddrOfPinnedObject(), bytes.Length, out transferred, overlap);
                    if (ok) return transferred;
                    if (Marshal.GetLastWin32Error() != 997) return -1;
                    if (!ready.WaitOne(timeout))
                    {
                        CancelIoEx(handle, overlap);
                        GetOverlappedResult(handle, overlap, out transferred, true);
                        return -1;
                    }
                    return GetOverlappedResult(handle, overlap, out transferred, false) ? transferred : -1;
                }
                finally
                {
                    pin.Free();
                    Marshal.FreeHGlobal(overlap);
                }
            }
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle file, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, IntPtr caps);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_SetFeature(SafeFileHandle file, byte[] report, int length);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetFeature(SafeFileHandle file, [In, Out] byte[] report, int length);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, IntPtr device);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", ExactSpelling = true, SetLastError = true)]
        private static extern bool GetInterfaceWithNode(IntPtr set, ref InterfaceData data, IntPtr detail, int size,
            out int required, ref Pnp.DeviceInfo device);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, int share, IntPtr security, int creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadFile(SafeFileHandle file, IntPtr bytes, int length, out int transferred, IntPtr overlap);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteFile(SafeFileHandle file, IntPtr bytes, int length, out int transferred, IntPtr overlap);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlap);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetOverlappedResult(SafeFileHandle file, IntPtr overlap, out int transferred, bool wait);
    }
}
