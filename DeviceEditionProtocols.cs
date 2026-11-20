using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    internal sealed partial class DeviceEditionProtocol : IBatteryProtocol
    {
        private delegate Reading ReadBattery(HidDevice device, SafeFileHandle handle, int page, int usage,
            int input, int output);

        private readonly string label;
        private readonly string[] hardwareIds;
        private readonly ReadBattery read;

        private static readonly DeviceEditionProtocol[] readers =
        {
            new DeviceEditionProtocol("ASUS ROG Harpe II", ReadHarpe, "0b05&pid_1ad0", "0b05&pid_1c69"),
            new DeviceEditionProtocol("SteelSeries Arctis Nova 7", ReadNova, "1038&pid_2202", "1038&pid_22a1", "1038&pid_227e",
                "1038&pid_2206", "1038&pid_220a", "1038&pid_22a5", "1038&pid_22a7", "1038&pid_2269", "1038&pid_226d"),
            new DeviceEditionProtocol("Rapoo VT9 Pro V2", ReadRapoo, "24ae&pid_14a9", "24ae&pid_1413", "24ae&pid_4613"),
            new DeviceEditionProtocol("MCHOSE M7 Ultra", ReadMchose, "5253&pid_1020", "5253&pid_0031", "5253&pid_1021", "3837&pid_100b", "3837&pid_1001"),
            new DeviceEditionProtocol("CHERRY MX 3.0S Wireless / PIXIU 75", ReadCherry,
                "046a&pid_00ea", "046a&pid_00e3", "046a&pid_01e4"),
            new DeviceEditionProtocol("Razer wireless mice", ReadRazer,
                "1532&pid_00bf", "1532&pid_00b6", "1532&pid_00b8", "1532&pid_007c", "1532&pid_007a",
                "1532&pid_007b", "1532&pid_00a5", "1532&pid_00c2", "1532&pid_0088", "1532&pid_00aa",
                "1532&pid_008f", "1532&pid_00b3", "1532&pid_0094", "1532&pid_0072", "1532&pid_00af"),
            new DeviceEditionProtocol("ASUS ROG wireless mice / OMNI", ReadRogMouse,
                "0b05&pid_1a68", "0b05&pid_1a6a", "0b05&pid_1a72", "0b05&pid_1b1a", "0b05&pid_1c0e", "0b05&pid_1ace"),
            new DeviceEditionProtocol("SteelSeries Arctis Nova Pro Wireless", ReadNovaPro, "1038&pid_12e0", "1038&pid_12e5"),
            new DeviceEditionProtocol("Rapoo V700DIY-98", ReadRapooKeyboard, "24ae&pid_1924", "24ae&pid_4824"),
            new DeviceEditionProtocol("CompX Pulsar / ATK / VXE / CHERRY", ReadCompXDevice,
                "3710&pid_5406", "3710&pid_3414", "373b&pid_101a", "373b&pid_101b",
                "3554&pid_f58a", "3554&pid_f58c", "373b&pid_1085", "3554&pid_f58f", "046a&pid_0330"),
            new DeviceEditionProtocol("HyperX Cloud II / III Wireless", ReadCloud,
                "03f0&pid_0995", "03f0&pid_0795", "03f0&pid_0696", "03f0&pid_018b", "03f0&pid_05b7", "03f0&pid_0c9d"),
            new DeviceEditionProtocol("HyperX Cloud Alpha 2 / Pulsefire Haste 2", ReadHyperXStatus,
                "03f0&pid_08be", "03f0&pid_0ab5", "03f0&pid_0f98", "03f0&pid_0d97"),
            new DeviceEditionProtocol("Rapoo Nordic VT3 / VT9 Gen 2", ReadRapooNordic,
                "24ae&pid_1417", "24ae&pid_4617", "24ae&pid_1414", "24ae&pid_4614"),
            new DeviceEditionProtocol("SteelSeries Aerox 3 / 5 / 9 Wireless", ReadAerox,
                "1038&pid_1838", "1038&pid_1878", "1038&pid_1852", "1038&pid_185c",
                "1038&pid_1860", "1038&pid_1858", "1038&pid_1874")
        };

        internal static IEnumerable<IBatteryProtocol> Readers { get { return readers; } }

        private DeviceEditionProtocol(string label, ReadBattery read, params string[] hardwareIds)
        {
            this.label = label;
            this.read = read;
            this.hardwareIds = hardwareIds;
        }

        public override string ToString() { return label; }

        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            var candidates = snapshot.Interfaces.Where(device => hardwareIds.Any(id => Matches(device.Path, id))).ToArray();
            BatteryDiagnostics.Add(label + ": matching HID collections=" + candidates.Length);
            foreach (HidDevice device in candidates)
            {
                using (SafeFileHandle handle = Hid.Open(device.Path, true))
                {
                    int page, usage, input, length;
                    if (handle.IsInvalid || !Hid.ReportShape(handle, out page, out usage, out input, out length))
                    {
                        BatteryDiagnostics.Add(label + ": collection cannot be opened");
                        continue;
                    }

                    BatteryDiagnostics.Add(string.Format("{0}: {1:X4}:{2:X4} input={3} output={4}",
                        label, page, usage, input, length));
                    Reading reading = read(device, handle, page, usage, input, length);
                    if (reading == null) continue;

                    snapshot.ClaimedPaths.Add(device.Path);
                    int existing = output.FindIndex(item => item.Id == reading.Id);
                    if (existing < 0) output.Add(reading);
                    else if (reading.Link == "USB 有线") output[existing] = reading;
                }
            }
        }

        private static bool Matches(string path, string id)
        {
            string token = "vid_" + id;
            return path.Contains(token + "&") || path.Contains(token + "#");
        }

        private static Reading ReadHarpe(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            bool wireless = Matches(device.Path, "0b05&pid_1ad0");
            if (input < 11 || output < 64 || (wireless
                ? !device.Path.Contains("&mi_02&col03")
                : (page < 0xff00 || !device.Path.Contains("&mi_00"))))
                return null;

            byte report = wireless ? (byte)3 : (byte)0;
            var request = new byte[output];
            request[0] = report;
            request[1] = 0x12;
            request[2] = 0x07;
            byte[] reply;
            int? level = Exchange(handle, request, input, "ROG Harpe II",
                (data, count) => HarpeLevel(data, count, report), out reply);
            if (!level.HasValue) return null;
            string product = Hid.ProductName(device.Path);
            string name = product != null && product.IndexOf("EXTREME", StringComparison.OrdinalIgnoreCase) >= 0
                ? "ROG Harpe II Extreme" : (wireless ? "ROG Harpe II" : "ROG Harpe II Ace");
            return Sample(device, level.Value, reply[10] == 1 ? "充电中" : "已读取",
                wireless, "rog:harpe2", name, DeviceKind.Mouse, "ASUS ROG");
        }

        private static Reading ReadNova(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            if (page != 0xffc0 || !device.Path.Contains("&mi_03") || input < 4 || output < 2) return null;
            string pid = ProductId(device.Path);
            bool original = pid == "2202" || pid == "2206" || pid == "220a";
            var request = new byte[output];
            request[1] = 0xb0;
            byte[] reply;
            int? level = Exchange(handle, request, input, "Arctis Nova 7",
                (data, count) => NovaLevel(data, count, original), out reply);
            if (!level.HasValue) return null;

            int offset = NovaOffset(reply);
            string status = reply[offset + 3] == 1 ? "充电中" : "已读取";
            if (original) status += " · 电量估算";
            string name = "Arctis Nova 7";
            if (pid == "227e") name += " Gen 2";
            if (pid == "2206" || pid == "22a5") name += "X";
            if (pid == "220a" || pid == "22a7") name += "P";
            if (pid == "2269") name = "Arctis Nova 3P Wireless";
            if (pid == "226d") name = "Arctis Nova 3X Wireless";
            return Sample(device, level.Value, status, true, "steelseries:nova7", name, DeviceKind.Headset, "SteelSeries");
        }

        private static Reading ReadRapoo(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            string pid = ProductId(device.Path);
            bool vt9 = pid == "14a9";
            if (page != 0xff00 || (usage != 0x000e && (vt9 || usage != 2)) || input < 8 ||
                !Hid.HasReportId(handle, 0, 7)) return null;
            string name = vt9 ? "VT9 Pro V2" : "VT7 Gen 2";
            BatteryDiagnostics.Add("Rapoo " + name + ": listening for input report 07");
            byte[] reply;
            int? level = Receive(handle, input, 4200, "Rapoo " + name, RapooLevel, out reply);
            return level.HasValue ? Sample(device, level.Value, reply[6] == 2 ? "充电中" : "已读取", pid != "4613",
                "rapoo:" + name, name, DeviceKind.Mouse, "Rapoo") : null;
        }

        private static Reading ReadMchose(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            if (page != 0xff01) return null;
            int feature;
            if (!Hid.Capabilities(handle, out page, out input, out output, out feature) || feature < 14)
                return null;

            foreach (byte report in new byte[] { 0x11, 0x12 })
            {
                var request = new byte[feature];
                request[0] = report;
                request[1] = 0xf9;
                for (int i = 2; i < request.Length; i++) request[i] = 0xff;
                bool sent = Hid.HidD_SetFeature(handle, request, request.Length);
                BatteryDiagnostics.Add(string.Format("MCHOSE M7 Ultra: feature {0:X2} ({1} bytes): {2}", report,
                    feature, sent ? "sent" : "failed"));
                if (!sent) continue;

                Thread.Sleep(60);
                var reply = new byte[feature];
                reply[0] = report;
                if (!Hid.HidD_GetFeature(handle, reply, reply.Length)) continue;
                string pid = ProductId(device.Path);
                int? level = MchoseLevel(reply, report, pid == "0031");
                if (!level.HasValue)
                {
                    BatteryDiagnostics.Add("MCHOSE M7 Ultra: no valid status payload");
                    continue;
                }
                int offset = MchoseOffset(reply, report);
                int pairedModel = (reply[offset + 2] ^ 0xff) | ((reply[offset + 3] ^ 0xff) << 8);
                string name = pid == "1021" ? "A7" : (pid == "100b" ? "A7 V2 Ultra" :
                    (pid == "1001" ? "K7 Ultra" : (pairedModel == 0x00b0 ? "L7 Pro" :
                    (pairedModel == 0x0031 ? "M7 Ultra" : "MCHOSE Realtek 无线鼠标"))));
                return Sample(device, level.Value, (reply[offset + 10] ^ 0xff) == 1 ? "充电中" : "已读取",
                    pid != "0031", "mchose:" + name, name, DeviceKind.Mouse, "MCHOSE");
            }
            return null;
        }

        private static Reading ReadCherry(HidDevice device, SafeFileHandle handle, int page, int usage, int input, int output)
        {
            if (page != 0xff1c || usage != 0x0092 || input < 10 || output < 64 ||
                !Hid.HasReportId(handle, 1, 4)) return null;
            byte[] reply;
            int? level = Exchange(handle, CherryRequest(output), input, "CHERRY MX 3.0S", CherryLevel, out reply);
            string pid = ProductId(device.Path);
            string name = pid == "01e4" ? "PIXIU 75" : "MX 3.0S Wireless";
            return level.HasValue ? Sample(device, level.Value, reply[9] != 0 ? "USB 供电" : "已读取",
                pid != "00e3", "cherry:" + name, name, DeviceKind.Keyboard, "CHERRY") : null;
        }

        private static Reading Sample(HidDevice device, int level, string status, bool wireless,
            string key, string product, DeviceKind kind, string brand, string link = null)
        {
            string id = Pnp.ContainerId(device.Node) ?? device.Path;
            BatteryDiagnostics.Add(brand + ": battery " + level + "%");
            return new Reading(level, status, link ?? (wireless ? "2.4 GHz" : "USB 有线"),
                "device-edition:" + key + ":" + id, product, kind, brand);
        }

        private static int? Exchange(SafeFileHandle handle, byte[] request, int input, string label,
            Func<byte[], int, int?> parse, out byte[] reply)
        {
            reply = null;
            if (!BatteryDiagnostics.Write(handle, request, label)) return null;
            return Receive(handle, input, 450, label, parse, out reply);
        }

        private static int? Receive(SafeFileHandle handle, int input, int timeout, string label,
            Func<byte[], int, int?> parse, out byte[] reply)
        {
            var buffer = new byte[input];
            var clock = Stopwatch.StartNew();
            int unmatched = 0;
            while (clock.ElapsedMilliseconds < timeout)
            {
                int count = Hid.Transfer(handle, buffer, false, Math.Max(1, timeout - (int)clock.ElapsedMilliseconds));
                if (count <= 0) break;
                int? level = parse(buffer, count);
                if (level.HasValue)
                {
                    reply = buffer;
                    return level;
                }
                unmatched++;
            }
            BatteryDiagnostics.Add(label + ": no valid battery reply; unmatched reports=" + unmatched);
            reply = null;
            return null;
        }

        internal static int? HarpeLevel(byte[] reply, int count, byte report)
        {
            if (count < 11 || reply[0] != report || reply[1] != 0x12 || reply[2] != 0x07 ||
                reply[5] > 100 || (reply[5] == 0 && reply[10] != 1)) return null;
            return reply[5];
        }

        private static int NovaOffset(byte[] reply) { return reply[0] == 0 ? 1 : 0; }

        internal static int? NovaLevel(byte[] reply, int count, bool original)
        {
            if (count < 4) return null;
            int offset = NovaOffset(reply);
            if (count < offset + 4 || reply[offset] != 0xb0 || reply[offset + 1] != 3 ||
                reply[offset + 3] > 1) return null;
            int level = reply[offset + 2];
            if (original) return level <= 4 ? level * 25 : (int?)null;
            return level <= 100 ? level : (int?)null;
        }

        internal static int? RapooLevel(byte[] reply, int count)
        {
            return count >= 8 && reply[0] == 7 && (reply[6] == 1 || reply[6] == 2) &&
                reply[7] <= 100 ? (int?)reply[7] : null;
        }

        private static int MchoseOffset(byte[] reply, byte report)
        {
            if (reply.Length >= 14 && reply[0] == report && reply[1] == 0xf9 &&
                (((reply[2] ^ 0xff) == 0x53 && (reply[3] ^ 0xff) == 0x52) ||
                 ((reply[2] ^ 0xff) == 0x37 && (reply[3] ^ 0xff) == 0x38))) return 2;
            return -1;
        }

        internal static int? MchoseLevel(byte[] reply, byte report, bool m7 = true)
        {
            int offset = MchoseOffset(reply, report);
            if (offset < 0 || (reply[offset + 2] == 0xff && reply[offset + 3] == 0xff) ||
                (m7 && ((reply[offset + 2] ^ 0xff) != 0x31 || reply[offset + 3] != 0xff)) ||
                (reply[offset + 10] ^ 0xff) > 1) return null;
            int level = reply[offset + 9] ^ 0xff;
            return level <= 100 ? level : (int?)null;
        }

        internal static byte[] CherryRequest(int length)
        {
            var request = new byte[length];
            request[0] = 4;
            request[3] = 0x1a;
            request[4] = 3;
            request[7] = 0x55;
            request[1] = 0x72;
            return request;
        }

        internal static int? CherryLevel(byte[] reply, int count)
        {
            if (count < 10 || reply[0] != 4 || reply[3] != 0x1a || reply[8] > 100) return null;
            int checksum = 0;
            for (int i = 3; i < Math.Min(count, 64); i++) checksum += reply[i];
            return (reply[1] | (reply[2] << 8)) == checksum ? (int?)reply[8] : null;
        }
    }
}
