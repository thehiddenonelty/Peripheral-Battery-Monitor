using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar
{
    internal static class BatteryDiagnostics
    {
        [ThreadStatic] private static List<string> lines;

        internal static void Add(string message)
        {
            if (lines != null) lines.Add(message);
        }

        internal static bool Write(SafeFileHandle handle, byte[] request, string stage)
        {
            int count = Hid.Transfer(handle, request, true, 200);
            Add(stage + ": write " + count + "/" + request.Length);
            return count == request.Length;
        }

        private static string FormatDeviceIds(IEnumerable<string> devicePaths)
        {
            var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var pattern = new Regex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
            foreach (string devicePath in devicePaths)
            {
                Match id = pattern.Match(devicePath ?? "");
                if (id.Success) ids.Add((id.Groups[1].Value + ":" + id.Groups[2].Value).ToUpperInvariant());
            }
            return string.Join(", ", ids);
        }

        internal static int Run()
        {
            lines = new List<string> { "BatteryBar Device Edition 0.1.3 - device battery diagnostics" };
            try
            {
                var snapshot = new DiscoverySnapshot(Hid.Discover());
                Add("HID interface count: " + snapshot.Interfaces.Count);
                Add("HID VID:PID: " + FormatDeviceIds(snapshot.Paths));
                Add("USB VID:PID: " + FormatDeviceIds(Pnp.PresentNodes("USB").Select(Pnp.InstanceId)));
                foreach (IBatteryProtocol protocol in Devices.Protocols)
                {
                    var result = new List<Reading>();
                    Add("");
                    Add(protocol.ToString());
                    try { protocol.Scan(snapshot, result); }
                    catch (Exception error) { Add("Scan error: " + error.GetType().Name); }
                    int valid = result.Count(reading => reading.Level.HasValue);
                    Add("Result: " + valid + " valid battery value(s)");
                }
                File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BatteryBar-Device-Diagnostics.txt"),
                    lines, new UTF8Encoding(true));
                return 0;
            }
            catch (Exception) { return 33; }
            finally { lines = null; }
        }
    }
}
