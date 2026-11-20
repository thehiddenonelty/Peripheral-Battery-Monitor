using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace BatteryBar
{
    internal sealed class HidDevice
    {
        internal readonly string Path;
        internal readonly uint Node;

        internal HidDevice(string path, uint node)
        {
            Path = path;
            Node = node;
        }
    }

    internal sealed class DiscoverySnapshot
    {
        internal readonly List<HidDevice> Interfaces;
        internal readonly List<string> Paths;
        internal readonly HashSet<string> ClaimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal DiscoverySnapshot(List<HidDevice> interfaces)
        {
            Interfaces = interfaces;
            Paths = interfaces.Select(device => device.Path).ToList();
        }
    }

    internal interface IBatteryProtocol
    {
        void Scan(DiscoverySnapshot snapshot, List<Reading> output);
    }

    internal static class Devices
    {
        // Earlier readers have precedence if two providers return the same stable ID.
        internal static readonly IBatteryProtocol[] Protocols = new IBatteryProtocol[]
        {
            new LogitechLightspeedProtocol(), new PulsarX2HProtocol(),
            new AtkZeroProtocol(), new HyperXCloudThreeSProtocol(),
            new VendorProtocol(), new SonyInzoneH9Protocol()
        }.Concat(DeviceEditionProtocol.Readers).Concat(new IBatteryProtocol[]
        {
            new BluetoothProtocol(), new StandardHidProtocol()
        }).ToArray();

        internal static Reading[] Poll()
        {
            var snapshot = new DiscoverySnapshot(Hid.Discover());
            var result = new List<Reading>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IBatteryProtocol protocol in Protocols)
            {
                var found = new List<Reading>();
                try { protocol.Scan(snapshot, found); }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine(error.GetType().Name); }
                foreach (Reading reading in found)
                    if (reading.Id != null && ids.Add(reading.Id)) result.Add(reading);
            }
            return result.ToArray();
        }

        internal const int SummaryLimit = 3;

        internal static Reading[] SelectSummary(Reading[] available, string[] pinned)
        {
            var shown = new List<Reading>(SummaryLimit);
            foreach (string id in pinned)
            {
                if (shown.Count == SummaryLimit) break;
                if (string.IsNullOrEmpty(id)) continue;
                Reading selected = Array.Find(available, state =>
                    state.Level.HasValue && string.Equals(state.Id, id, StringComparison.OrdinalIgnoreCase));
                if (selected != null && !shown.Contains(selected)) shown.Add(selected);
            }
            foreach (Reading state in available)
            {
                if (shown.Count == SummaryLimit) break;
                if (state.Level.HasValue && !string.IsNullOrEmpty(state.Id) && !shown.Contains(state)) shown.Add(state);
            }
            return shown.ToArray();
        }
    }

    internal sealed class BluetoothProtocol : IBatteryProtocol
    {
        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            var groups = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            foreach (string bus in new[] { "BTHENUM", "BTHLE", "BTHLEDEVICE" })
            foreach (uint node in Pnp.PresentNodes(bus))
            {
                string id = Pnp.ContainerId(node);
                if (id == null) id = Pnp.InstanceId(node);
                if (string.IsNullOrEmpty(id)) continue;
                List<uint> nodes;
                if (!groups.TryGetValue(id, out nodes))
                {
                    nodes = new List<uint>();
                    groups.Add(id, nodes);
                }
                nodes.Add(node);
            }

            foreach (var group in groups.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                string name = null, primaryName = null;
                int? level = null;
                bool connected = false, disconnected = false;
                foreach (uint node in group.Value)
                {
                    string instance = Pnp.InstanceId(node);
                    if (instance != null && (instance.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase) ||
                        instance.StartsWith(@"BTHLE\DEV_", StringComparison.OrdinalIgnoreCase)))
                        primaryName = Pnp.Name(node) ?? primaryName;
                    bool? connection = Pnp.Connected(node);
                    connected |= connection == true;
                    disconnected |= connection == false;
                    int? battery = Pnp.Battery(node);
                    if (!battery.HasValue) continue;
                    string label = Pnp.Name(node);
                    if (!level.HasValue || connection == true)
                    {
                        level = battery;
                        name = label;
                    }
                }
                if (!level.HasValue || (disconnected && !connected)) continue;
                string labelName = primaryName ?? name ?? "蓝牙设备";
                output.Add(new Reading(level, connected ? "已连接" : "Windows 报告值", "Bluetooth",
                    "bluetooth:" + group.Key.ToLowerInvariant(), labelName, DisplayKind(labelName),
                    "Windows 蓝牙", fresh: connected));
            }
        }

        internal static DeviceKind DisplayKind(string name)
        {
            if (name.IndexOf("Varmilo", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Minilo", StringComparison.OrdinalIgnoreCase) >= 0) return DeviceKind.Keyboard;
            if (name.IndexOf("Sennheiser", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MOMENTUM 4", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MOMENTUM True Wireless", StringComparison.OrdinalIgnoreCase) >= 0)
                return DeviceKind.Headset;
            return DeviceKind.Generic;
        }
    }

    internal static class Pnp
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct DeviceInfo
        {
            internal int Size;
            internal Guid Class;
            internal uint Node;
            internal IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            internal Guid Format;
            internal uint Id;
            internal PropertyKey(string format, uint id) { Format = new Guid(format); Id = id; }
        }

        private static readonly PropertyKey battery = new PropertyKey("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
        private static readonly PropertyKey connected = new PropertyKey("78C34FC8-104A-4ACA-9EA4-524D52996E57", 55);
        private static readonly PropertyKey container = new PropertyKey("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C", 2);
        private static readonly PropertyKey friendly = new PropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        private static readonly PropertyKey description = new PropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        private static readonly PropertyKey busDescription = new PropertyKey("540B947E-8B40-45BC-A8A2-6A0B894CBDA2", 4);

        internal static List<uint> PresentNodes(string bus)
        {
            var result = new List<uint>();
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, bus, IntPtr.Zero, 6);
            if (set == new IntPtr(-1)) return result;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var info = new DeviceInfo { Size = Marshal.SizeOf(typeof(DeviceInfo)) };
                    if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;
                    result.Add(info.Node);
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        private static byte[] Read(uint node, PropertyKey key, uint expectedType, int capacity)
        {
            uint size = (uint)capacity;
            uint type;
            var buffer = new byte[capacity];
            if (CM_Get_DevNode_PropertyW(node, ref key, out type, buffer, ref size, 0) != 0 ||
                type != expectedType || size == 0 || size > buffer.Length) return null;
            Array.Resize(ref buffer, (int)size);
            return buffer;
        }

        internal static int? Battery(uint node)
        {
            byte[] value = Read(node, battery, 3, 1);
            return value != null && value[0] <= 100 ? value[0] : (int?)null;
        }

        internal static bool? Connected(uint node)
        {
            byte[] value = Read(node, connected, 0x11, 1);
            return value != null ? value[0] != 0 : (bool?)null;
        }

        internal static string ContainerId(uint node)
        {
            byte[] value = Read(node, container, 13, 16);
            if (value == null || value.Length != 16) return null;
            var id = new Guid(value);
            return id == Guid.Empty || id == new Guid("00000000-0000-0000-FFFF-FFFFFFFFFFFF") ? null : id.ToString();
        }

        internal static string Name(uint node)
        {
            return Text(node, friendly) ?? Text(node, busDescription) ?? Text(node, description);
        }

        private static string Text(uint node, PropertyKey key)
        {
            byte[] value = Read(node, key, 0x12, 1024);
            if (value == null || value.Length % 2 != 0) return null;
            string label = Encoding.Unicode.GetString(value).TrimEnd('\0').Trim();
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }

        internal static string InstanceId(uint node)
        {
            var id = new StringBuilder(512);
            return CM_Get_Device_IDW(node, id, id.Capacity, 0) == 0 ? id.ToString() : null;
        }

        internal static bool IsUsbBacked(uint node)
        {
            for (int depth = 0; depth < 8; depth++)
            {
                uint parent;
                if (CM_Get_Parent(out parent, node, 0) != 0) return false;
                string id = InstanceId(parent);
                if (id != null && id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)) return true;
                if (id != null && id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return false;
                node = parent;
            }
            return false;
        }

        [DllImport("cfgmgr32.dll")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr cls, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll", ExactSpelling = true)] private static extern uint CM_Get_DevNode_PropertyW(
            uint node, ref PropertyKey key, out uint type, [Out] byte[] value, ref uint size, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, int length, uint flags);
    }
}
