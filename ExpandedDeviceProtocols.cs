using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    internal sealed partial class DeviceEditionProtocol
    {
        private static Reading ReadRazer(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            string pid = ProductId(device.Path);
            bool legacy = new[] { "0072", "007a", "007b", "007c", "0088" }.Contains(pid);
            bool interfaceTwo = legacy || pid == "008f";
            string mi = interfaceTwo ? "&mi_02" : "&mi_00";
            if (!device.Path.Contains(mi) &&
                !(pid == "00bf" && device.Path.Contains("&mi_03") && page >= 0xff00)) return null;
            int feature;
            if (!Hid.Capabilities(handle, out page, out input, out output, out feature) || feature != 91) return null;
            using (SafeFileHandle control = Hid.Open(device.Path, false))
            {
                if (control.IsInvalid) return null;
                byte transaction = legacy ? (byte)0x3f : (byte)0x1f;
                int? level = VendorProtocol.MouseQuery(control, 0x80, transaction);
                if (!level.HasValue) return null;
                bool charging = VendorProtocol.MouseQuery(control, 0x84, transaction) == 1;
                return Sample(device, (level.Value * 100 + 127) / 255, charging ? "充电中" : "已读取",
                    true, "razer:" + pid, RazerName(pid), DeviceKind.Mouse, "Razer");
            }
        }

        private static string RazerName(string pid)
        {
            switch (pid)
            {
                case "00bf": return "DeathAdder V4 Pro";
                case "00b6": return "DeathAdder V3 Pro";
                case "00b8": return "DeathAdder V3 HyperSpeed";
                case "007c": return "DeathAdder V2 Pro";
                case "007a":
                case "007b": return "Viper Ultimate";
                case "00a5": return "Viper V2 Pro";
                case "00c2": return "Viper V3 Pro";
                case "0088": return "Basilisk Ultimate";
                case "00aa": return "Basilisk V3 Pro";
                case "008f": return "Naga Pro";
                case "00b3": return "Naga V2 HyperSpeed";
                case "0094": return "Orochi V2";
                case "0072": return "Mamba Wireless";
                case "00af": return "Cobra Pro";
                default: return "Razer 无线鼠标";
            }
        }

        private static string ProductId(string path)
        {
            int start = path.IndexOf("&pid_", StringComparison.Ordinal);
            return start >= 0 && path.Length >= start + 9 ? path.Substring(start + 5, 4) : "";
        }

        private static Reading ReadRogMouse(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            string pid = ProductId(device.Path);
            bool omni = pid == "1ace";
            if (input < 11 || output < 64 || (omni
                ? page != 0xff01 || usage != 3 || !device.Path.Contains("&mi_02")
                : page < 0xff00 || !device.Path.Contains("&mi_00"))) return null;
            byte id = omni ? (byte)3 : (byte)0;
            byte[] reply;
            int? level = Exchange(handle, Packet(output, id, 0x12, 7), input, "ROG mouse",
                (data, count) => HarpeLevel(data, count, id), out reply);
            if (!level.HasValue) return null;
            string name;
            switch (pid)
            {
                case "1a68": case "1a6a": name = "Keris Wireless AimPoint"; break;
                case "1a72": name = "Gladius III Wireless AimPoint"; break;
                case "1b1a": name = "Keris II Ace"; break;
                case "1c0e": name = "Keris II Origin"; break;
                default: name = "ROG OMNI 鼠标"; break;
            }
            return Sample(device, level.Value, reply[10] == 1 ? "充电中" : "已读取", omni,
                "rog:mouse:" + pid, name, DeviceKind.Mouse, "ASUS ROG", omni ? null : "USB / 2.4 GHz");
        }

        private static Reading ReadNovaPro(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            if ((page != 0xffc0 && page != 0xff00) || input < 16 || output < 2 ||
                (!device.Path.Contains("&mi_03") && !device.Path.Contains("&mi_04"))) return null;
            byte[] reply;
            int? level = Exchange(handle, Packet(output, 6, 0xb0), input, "Arctis Nova Pro", NovaProLevel, out reply);
            return level.HasValue ? Sample(device, level.Value, reply[15] == 2 ? "充电中 · 电量估算" : "电量估算",
                true, "steelseries:novapro", "Arctis Nova Pro Wireless", DeviceKind.Headset, "SteelSeries") : null;
        }

        internal static int? NovaProLevel(byte[] reply, int count)
        {
            if (count < 16 || reply[0] != 6 || reply[1] != 0xb0 || reply[6] > 8 ||
                (reply[15] != 2 && reply[15] != 8)) return null;
            return (reply[6] * 100 + 4) / 8;
        }

        private static Reading ReadRapooKeyboard(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            if (page != 0xff00 || input < 9 || !Hid.HasReportId(handle, 0, 0xbd)) return null;
            byte[] reply;
            int? level = Receive(handle, input, 4200, "Rapoo V700DIY-98",
                (data, count) => count >= 9 && data[0] == 0xbd && (data[7] == 1 || data[7] == 2) && data[8] <= 100
                    ? (int?)data[8] : null, out reply);
            return level.HasValue ? Sample(device, level.Value, reply[7] == 2 ? "充电中" : "已读取",
                ProductId(device.Path) != "4824", "rapoo:v700diy98", "V700DIY-98", DeviceKind.Keyboard, "Rapoo") : null;
        }

        private static Reading ReadCompXDevice(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            if (page != 0xff02 || usage != 2 || input != 17 || output != 17 ||
                !Hid.HasReportId(handle, 0, 8) || !Hid.HasReportId(handle, 1, 8)) return null;
            string pid = ProductId(device.Path);
            bool wired = pid == "3414" || pid == "f58c" || pid == "f58f";
            byte[] report = CompXMouse.Exchange(handle, CompXMouse.BuildFrame(4), true);
            int? level = AtkZeroProtocol.ReadCompXLevel(report);
            if (!level.HasValue) return null;
            if (!wired && report[7] == 0)
            {
                byte[] link = CompXMouse.Exchange(handle, CompXMouse.BuildFrame(3), true);
                if (link != null && link[6] != 1) return null;
            }
            string brand, name;
            switch (pid)
            {
                case "f58a":
                case "f58c": brand = "ATK / VXE"; name = "VXE R1 Pro Max"; break;
                case "1085":
                case "f58f": brand = "ATK / VXE"; name = "VXE R1 SE+"; break;
                case "101a": brand = "ATK"; name = "ATK X1 Pro"; break;
                case "101b": brand = "ATK"; name = "ATK Fire X 8K"; break;
                case "0330": brand = "CHERRY"; name = "CHERRY XTRFY 无线鼠标"; break;
                default: brand = "Pulsar"; name = "Pulsar X2 CrazyLight Mini"; break;
            }
            return Sample(device, level.Value, report[7] == 1 ? "USB 供电" : "已读取", !wired,
                "compx:" + name, name, DeviceKind.Mouse, brand);
        }

        private static Reading ReadCloud(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            string pid = ProductId(device.Path);
            bool old = pid == "0696" || pid == "018b";
            bool core = pid == "0995" || pid == "0795";
            if (old)
            {
                if (page != 0xff90 || usage != 0x0303 || input < 8 || output < 52) return null;
            }
            else if (input < 5 || output < 62 ||
                (core ? page < 0xff00 || !device.Path.Contains("&mi_03") : page != 0xff13 || usage != 1))
                return null;

            byte[] reply;
            byte[] query = old ? Packet(output, 0x06, 0xff, 0xbb, 2) : Packet(output, 0x66, 0x89);
            int? level = Exchange(handle, query, input, "HyperX Cloud", (data, count) => CloudLevel(data, count, old), out reply);
            if (!level.HasValue && !old && !core)
            {
                int feature;
                if (Hid.Capabilities(handle, out page, out input, out output, out feature) && feature == 62 &&
                    Hid.HidD_SetFeature(handle, Packet(feature, 0x66, 0x89), feature))
                    level = Receive(handle, input, 450, "HyperX Cloud III feature fallback",
                        (data, count) => CloudLevel(data, count, false), out reply);
            }
            if (!level.HasValue) return null;
            string name = old ? "Cloud II Wireless" : (core ? "Cloud II Core Wireless" : "Cloud III Wireless");
            return Sample(device, level.Value, "已读取", true, "hyperx:" + name, name, DeviceKind.Headset, "HyperX");
        }

        internal static int? CloudLevel(byte[] report, int count, bool old)
        {
            if (old)
                return count >= 8 && report[0] == 6 && report[1] == 0xff && report[2] == 0xbb &&
                    report[3] == 2 && report[7] <= 100 ? (int?)report[7] : null;
            return count >= 5 && report[0] == 0x66 && (report[1] == 0x89 || report[1] == 0x0d) &&
                (report[2] != 0 || report[3] != 0) && report[4] <= 100 ? (int?)report[4] : null;
        }

        private static Reading ReadRapooNordic(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            if (page != 0xff00 || usage != 2 || (input != 13 && input != 16 && input != 19)) return null;
            string pid = ProductId(device.Path);
            bool vt3 = pid == "1417" || pid == "4617";
            int expected = vt3 ? 0x4617 : 0x4614;
            string name = vt3 ? "VT3 Max Gen 2" : "VT9 Gen 2";
            byte[] reply;
            int? level = Receive(handle, input, 4200, name,
                (data, count) => RapooNordicLevel(data, count, expected), out reply);
            return level.HasValue ? Sample(device, level.Value, reply[7] == 2 ? "充电中" : "已读取",
                (reply[1] & 0x0f) != 2, "rapoo:nordic:" + name, name, DeviceKind.Mouse, "Rapoo") : null;
        }

        internal static int? RapooNordicLevel(byte[] reply, int count, int expected)
        {
            if ((count != 13 && count != 16 && count != 19) || reply[0] != 7 ||
                ((reply[1] & 15) != 0 && (reply[1] & 15) != 2) ||
                (reply[7] != 1 && reply[7] != 2) || reply[8] > 100) return null;
            if (count >= 15 && (reply[13] | reply[14] << 8) != expected) return null;
            return reply[8];
        }

        private static Reading ReadAerox(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            if (page != 0xffc0 || input < 3 || output < 2) return null;
            string pid = ProductId(device.Path);
            string model = pid == "1858" || pid == "1874" ? "9" :
                (pid == "1852" || pid == "185c" || pid == "1860" ? "5" : "3");
            byte[] reply;
            int? level = Exchange(handle, Packet(output, 0, 0xd2), input, "SteelSeries Aerox", AeroxLevel, out reply);
            if (!level.HasValue) return null;
            int offset = reply[0] == 0 ? 1 : 0;
            return Sample(device, level.Value, (reply[offset + 1] & 0x80) != 0 ? "充电中" : "已读取",
                true, "steelseries:aerox:" + pid, "Aerox " + model + " Wireless", DeviceKind.Mouse, "SteelSeries");
        }

        internal static int? AeroxLevel(byte[] reply, int count)
        {
            if (count < 2) return null;
            int offset = reply[0] == 0 ? 1 : 0;
            if (count < offset + 2 || reply[offset] != 0xd2) return null;
            int raw = reply[offset + 1] & 0x7f;
            if (raw == 0 || raw > 100) return null;
            return raw <= 21 ? (raw - 1) * 5 : raw;
        }

        private static Reading ReadHyperXStatus(HidDevice device, SafeFileHandle handle, int page, int usage,
            int input, int output)
        {
            string pid = ProductId(device.Path);
            bool alpha = pid == "08be";
            if (input < 8 || output < 64 || (alpha
                ? page != 0xff13 || usage != 0xff00
                : page < 0xff00 || !device.Path.Contains("&mi_02"))) return null;
            byte[] reply;
            if (!alpha)
            {
                int? linked = Exchange(handle, Packet(output, 0x52, 1), input, "HyperX Haste link",
                    (data, count) => count >= 3 && data[0] == 0x53 && data[1] == 1 && data[2] <= 1
                        ? (int?)data[2] : null, out reply);
                if (linked == 0) return null;
            }
            int? level = Exchange(handle, Packet(output, 0x50, 2), input, "HyperX battery",
                (data, count) => count >= 7 && data[0] == 0x51 && data[1] == 2 && data[2] <= 100 &&
                    (alpha || data[3] <= 2) ? (int?)data[2] : null, out reply);
            if (!level.HasValue) return null;
            string name = alpha ? "Cloud Alpha 2 Wireless" :
                (pid == "0ab5" ? "Pulsefire Haste 2 Core Wireless" : "Pulsefire Haste 2 Wireless");
            bool charging = alpha ? (reply[6] & 0x80) != 0 : reply[3] == 1;
            return Sample(device, level.Value, charging ? "充电中" : "已读取", true,
                "hyperx:" + pid, name, alpha ? DeviceKind.Headset : DeviceKind.Mouse, "HyperX");
        }

        private static byte[] Packet(int size, params byte[] header)
        {
            var request = new byte[size];
            Array.Copy(header, request, header.Length);
            return request;
        }
    }

    internal sealed class SonyInzoneH9Protocol : IBatteryProtocol
    {
        private static readonly byte[][] Queries =
        {
            new byte[] { 1, 0, 0xfc, 8, 0x96, 0xc3, 0x21, 1, 1, 1, 0, 0x7d },
            new byte[] { 1, 0, 0xfc, 8, 0x96, 0xc3, 0x41, 2, 1, 1, 0, 0x9e },
            new byte[] { 1, 0, 0xfc, 8, 0x96, 0xc3, 0x41, 4, 1, 1, 0, 0xa0 }
        };
        private static readonly byte[] Signature = { 4, 0xff, 0x0b, 0, 0x96, 0xc3, 0x14, 4 };

        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            foreach (uint node in Pnp.PresentNodes("USB"))
            {
                string id = Pnp.InstanceId(node);
                if (id == null || !id.StartsWith(@"USB\VID_054C&PID_0E53", StringComparison.OrdinalIgnoreCase)) continue;
                var port = Regex.Match(Pnp.Name(node) ?? "", @"\bCOM[0-9]+\b", RegexOptions.IgnoreCase);
                if (!port.Success) continue;
                try
                {
                    using (var serial = new SerialPort(port.Value.ToUpperInvariant(), 115200, Parity.None, 8, StopBits.One))
                    {
                        serial.DtrEnable = serial.RtsEnable = true;
                        serial.ReadTimeout = 100;
                        serial.WriteTimeout = 250;
                        serial.Open();
                        for (int i = 0; i < Queries.Length - 1; i++)
                        {
                            serial.Write(Queries[i], 0, Queries[i].Length);
                            Thread.Sleep(250);
                            serial.DiscardInBuffer();
                        }
                        byte[] query = Queries[Queries.Length - 1];
                        serial.Write(query, 0, query.Length);
                        var buffer = new List<byte>();
                        var clock = Stopwatch.StartNew();
                        while (clock.ElapsedMilliseconds < 1100 && buffer.Count < 1024)
                        {
                            try { buffer.Add((byte)serial.ReadByte()); }
                            catch (TimeoutException) { continue; }
                            bool charging;
                            int? level = Parse(buffer, out charging);
                            if (!level.HasValue) continue;
                            output.Add(new Reading(level, charging ? "充电中" : "已读取", "2.4 GHz",
                                "sony:inzone-h9:" + (Pnp.ContainerId(node) ?? id), "INZONE H9", DeviceKind.Headset, "Sony"));
                            BatteryDiagnostics.Add("Sony INZONE H9: battery " + level.Value + "%");
                            return;
                        }
                        BatteryDiagnostics.Add("Sony INZONE H9: no valid serial reply");
                    }
                }
                catch (UnauthorizedAccessException) { BatteryDiagnostics.Add("Sony INZONE H9: serial interface busy"); }
                catch (IOException) { BatteryDiagnostics.Add("Sony INZONE H9: serial interface disconnected"); }
                catch (TimeoutException) { BatteryDiagnostics.Add("Sony INZONE H9: serial write timeout"); }
            }
        }

        internal static int? Parse(IList<byte> buffer, out bool charging)
        {
            charging = false;
            for (int offset = 0; offset <= buffer.Count - 14; offset++)
            {
                bool matched = true;
                for (int i = 0; i < Signature.Length; i++)
                    if (buffer[offset + i] != Signature[i]) { matched = false; break; }
                if (!matched || buffer[offset + 12] > 100) continue;
                charging = buffer[offset + 11] == 1;
                return buffer[offset + 12];
            }
            return null;
        }
    }
}
