using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    internal sealed class AtkZeroProtocol : IBatteryProtocol
    {
        private const byte ReportId = 0x08;
        private const string WiredId = "vid_373b&pid_1154";
        private const string ReceiverId = "vid_373b&pid_1155";
        private const string ReportedReceiverId = "vid_373b&pid_12cc";

        private static bool IsDevice(string path, string id)
        {
            return path.IndexOf(id + "&", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf(id + "#", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsCandidate(string path)
        {
            return IsDevice(path, WiredId) || IsDevice(path, ReceiverId) || IsDevice(path, ReportedReceiverId);
        }

        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            var candidates = snapshot.Interfaces.Where(device => IsCandidate(device.Path))
                .OrderBy(device => IsDevice(device.Path, WiredId) ? 0 : 1).ToArray();
            BatteryDiagnostics.Add("ATK: matching HID collections=" + candidates.Length);
            foreach (HidDevice device in candidates)
            {
                using (SafeFileHandle handle = Hid.Open(device.Path, true))
                {
                    int page, usage, input, length;
                    if (handle.IsInvalid || !Hid.ReportShape(handle, out page, out usage, out input, out length))
                    {
                        BatteryDiagnostics.Add("ATK: cannot open HID collection");
                        continue;
                    }
                    BatteryDiagnostics.Add(string.Format("ATK: {0:X4}:{1:X4} input={2} output={3}",
                        page, usage, input, length));
                    bool reportedReceiver = IsDevice(device.Path, ReportedReceiverId);
                    bool wireless = !IsDevice(device.Path, WiredId);
                    bool charging;
                    int? level;
                    if (reportedReceiver && page == 0xff02 && usage == 2 && input == 17 && length == 17)
                    {
                        if (!Hid.HasReportId(handle, 0, 8) || !Hid.HasReportId(handle, 1, 8))
                        {
                            BatteryDiagnostics.Add("ATK CompX: report ID 08 not found for input and output");
                            continue;
                        }
                        byte[] reply = CompXMouse.Exchange(handle, CompXMouse.BuildFrame(4), true);
                        level = ReadCompXLevel(reply);
                        charging = level.HasValue && reply[7] == 1;
                        BatteryDiagnostics.Add(level.HasValue ? "ATK CompX: battery " + level.Value + "%" :
                            (reply == null ? "ATK CompX: no battery reply" : "ATK CompX: battery fields unavailable"));
                    }
                    else
                    {
                        if (page != 0xff05 || usage != 1 || input < 9 || length < 64) continue;
                        level = Query(handle, length, input, wireless, false, out charging);
                        if (!level.HasValue) level = Query(handle, length, input, wireless, true, out charging);
                    }
                    if (!level.HasValue) continue;

                    snapshot.ClaimedPaths.Add(device.Path);
                    output.Add(new Reading(level, charging ? "充电中" : "已读取", wireless ? "2.4 GHz" : "USB 有线",
                        "gearpair:atk:zero", reportedReceiver ? "ATK 无线鼠标" : "ATK 烈空 ZERO", DeviceKind.Mouse, "ATK"));
                    return;
                }
            }
        }

        internal static int? ReadCompXLevel(byte[] reply)
        {
            if (reply == null || !CompXMouse.IsReply(reply, reply.Length, 4) || reply[6] > 100 || reply[7] > 1)
                return null;
            // A receiver can return an empty power block when the mouse is asleep.
            if (reply[6] == 0 && reply[7] == 0 && reply[8] == 0 && reply[9] == 0) return null;
            return reply[6];
        }

        private static int? Query(SafeFileHandle handle, int outputSize, int inputSize, bool wireless,
            bool legacy, out bool charging)
        {
            charging = false;
            string stage = legacy ? "ATK legacy" : "ATK checksummed";
            byte[] request = BuildRequest(outputSize, wireless, legacy);
            if (!BatteryDiagnostics.Write(handle, request, stage)) return null;

            var response = new byte[inputSize];
            var clock = Stopwatch.StartNew();
            int unmatched = 0;
            while (clock.ElapsedMilliseconds < 500)
            {
                int count = Hid.Transfer(handle, response, false, Math.Max(1, 500 - (int)clock.ElapsedMilliseconds));
                if (count <= 0) break;
                int? level = ReadLevel(response, count);
                if (level.HasValue)
                {
                    charging = response[1] == 0x72 && response[3] == 0x3b;
                    BatteryDiagnostics.Add(stage + ": battery " + level.Value + "%");
                    return level;
                }
                unmatched++;
            }
            BatteryDiagnostics.Add(stage + ": no valid battery reply; unmatched reports=" + unmatched);
            return null;
        }

        internal static byte[] BuildRequest(int size, bool wireless, bool legacy = false)
        {
            var request = new byte[size];
            request[0] = ReportId;
            request[2] = 0x72;
            request[5] = wireless ? (byte)1 : (byte)0;
            request[6] = 0x07;
            request[7] = 0x01;
            if (!legacy)
            {
                request[3] = 0x02;
                request[1] = Checksum(request);
            }
            return request;
        }

        private static byte Checksum(byte[] report)
        {
            int sum = 0;
            for (int i = 2; i < 64; i++) sum += report[i];
            return unchecked((byte)sum);
        }

        internal static int? ReadLevel(byte[] response, int count)
        {
            if (count >= 64 && response[0] == ReportId && response[2] == 0x72 &&
                response[1] == Checksum(response) && response[3] != 0xff && response[6] == 0x07 &&
                response[7] >= 1 && response[8] <= 100) return response[8];

            if (count < 10 || response[0] != ReportId || response[1] != 0x72 ||
                response[5] != 0x07 || response[6] != 0x01 || response[7] > 100) return null;
            return response[7];
        }
    }

    internal sealed class HyperXCloudThreeSProtocol : IBatteryProtocol
    {
        private const byte ReportId = 0x0c;
        private const byte Battery = 0x06;
        private const string ReceiverA = "vid_03f0&pid_02cc";
        private const string ReceiverB = "vid_03f0&pid_06be";

        private sealed class Channel : IDisposable
        {
            internal readonly SafeFileHandle Handle;
            internal readonly int InputLength, OutputLength;
            internal readonly bool Preferred;

            internal Channel(SafeFileHandle handle, int inputLength, int outputLength, bool preferred)
            {
                Handle = handle;
                InputLength = inputLength;
                OutputLength = outputLength;
                Preferred = preferred;
            }

            public void Dispose() { Handle.Dispose(); }
        }

        public void Scan(DiscoverySnapshot snapshot, List<Reading> output)
        {
            var groups = snapshot.Interfaces.Where(device => device.Path.Contains(ReceiverA) ||
                device.Path.Contains(ReceiverB)).GroupBy(device => Pnp.ContainerId(device.Node) ??
                    (device.Path.Contains(ReceiverA) ? ReceiverA : ReceiverB)).ToArray();
            BatteryDiagnostics.Add("HyperX: matching receiver groups=" + groups.Length + " (03F0:02CC or 03F0:06BE)");
            foreach (var group in groups)
            {
                var channels = new List<Channel>();
                try
                {
                    foreach (HidDevice device in group)
                    {
                        Channel channel = OpenChannel(device.Path);
                        if (channel != null) channels.Add(channel);
                    }
                    if (!channels.Any(channel => channel.OutputLength > 0)) BatteryDiagnostics.Add("HyperX: no output channel");
                    foreach (Channel writer in channels.Where(channel => channel.OutputLength > 0)
                        .OrderByDescending(channel => channel.Preferred))
                    {
                        int? level = ReadBattery(channels, writer);
                        if (!level.HasValue) continue;
                        foreach (HidDevice device in group) snapshot.ClaimedPaths.Add(device.Path);
                        output.Add(new Reading(level, "已读取", "2.4 GHz", "gearpair:hyperx:cloud3s:" + group.Key,
                            "Cloud III S Wireless", DeviceKind.Headset, "HyperX"));
                        break;
                    }
                }
                finally
                {
                    foreach (Channel channel in channels) channel.Dispose();
                }
            }
        }

        private static Channel OpenChannel(string path)
        {
            SafeFileHandle handle = Hid.Open(path, true);
            bool keep = false;
            try
            {
                int page, usage, input, output;
                if (handle.IsInvalid || !Hid.ReportShape(handle, out page, out usage, out input, out output))
                {
                    BatteryDiagnostics.Add("HyperX: cannot open HID collection");
                    return null;
                }
                // Known vendor collection; use Windows' declared size even when it is below 64.
                bool primary = page == 0x01c0 && usage == 1;
                int inputLength = input >= 7 && (primary || Hid.HasReportId(handle, 0, ReportId)) ? input : 0;
                int outputLength = output >= 6 && (primary || Hid.HasReportId(handle, 1, ReportId)) ? output : 0;
                BatteryDiagnostics.Add(string.Format("HyperX: {0:X4}:{1:X4} input={2} output={3}", page, usage,
                    inputLength, outputLength));
                if (inputLength == 0 && outputLength == 0) return null;
                var channel = new Channel(handle, inputLength, outputLength, primary);
                keep = true;
                return channel;
            }
            finally { if (!keep) handle.Dispose(); }
        }

        private static int? ReadBattery(List<Channel> channels, Channel writer)
        {
            var readers = channels.Where(channel => channel.InputLength > 0)
                .OrderBy(channel => channel == writer ? 0 : 1).ToArray();
            if (readers.Length == 0)
            {
                BatteryDiagnostics.Add("HyperX: no input channel");
                return null;
            }
            byte[] request = BuildRequest(writer.OutputLength);
            if (!BatteryDiagnostics.Write(writer.Handle, request, "HyperX")) return null;

            // The reply can be queued on another collection of the same receiver.
            foreach (Channel reader in readers)
            {
                var clock = Stopwatch.StartNew();
                int timeout = reader == writer || readers.Length == 1 ? 750 : 200;
                int unmatched = 0, unavailable = 0;
                var response = new byte[reader.InputLength];
                while (clock.ElapsedMilliseconds < timeout)
                {
                    int count = Hid.Transfer(reader.Handle, response, false,
                        Math.Max(1, timeout - (int)clock.ElapsedMilliseconds));
                    if (count <= 0) break;
                    int? level = ReadLevel(response, count);
                    if (level.HasValue)
                    {
                        BatteryDiagnostics.Add("HyperX: battery " + level.Value + "%");
                        return level;
                    }
                    if (count >= 7 && response[0] == ReportId && response[5] == Battery) unavailable++;
                    else unmatched++;
                }
                BatteryDiagnostics.Add("HyperX: no valid battery reply; unavailable=" + unavailable +
                    "; unmatched reports=" + unmatched);
            }
            return null;
        }

        internal static byte[] BuildRequest(int size)
        {
            var request = new byte[size];
            request[0] = ReportId;
            request[1] = 0x02;
            request[2] = 0x03;
            request[3] = 0x01;
            request[5] = Battery;
            return request;
        }

        internal static int? ReadLevel(byte[] response, int count)
        {
            if (count < 7 || response[0] != ReportId || response[5] != Battery || response[6] > 100) return null;
            return response[6];
        }
    }
}
