// advscan.cs - watch BLE advertisements for N seconds; flags the Xiaomi remote
// by MAC. Answers "is the remote awake and advertising right now?" - silent
// output for its MAC means asleep/off/dead battery, not a Windows-side problem.
// 中文：BLE 广播监听 —— 判断遥控器此刻是否醒着在广播（没广播=休眠/关机/没电）
// Usage: advscan <seconds> [<macPrefix like c05d39>]
using System;
using System.Collections.Generic;
using Windows.Devices.Bluetooth.Advertisement;

static class AdvScan {
    static int Main(string[] args) {
        int secs = args.Length >= 1 ? int.Parse(args[0]) : 10;
        string mac = args.Length >= 2 ? args[1].ToLowerInvariant() : "c05d39c32608";
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        var seen = new Dictionary<ulong, int>();
        int targetHits = 0;
        ulong target = Convert.ToUInt64(mac, 16);
        watcher.Received += (BluetoothLEAdvertisementWatcher w, BluetoothLEAdvertisementReceivedEventArgs e) => {
            int rssi;
            if (!seen.TryGetValue(e.BluetoothAddress, out rssi) || e.RawSignalStrengthInDBm > rssi)
                seen[e.BluetoothAddress] = e.RawSignalStrengthInDBm;
            if (e.BluetoothAddress == target) {
                targetHits++;
                string name = e.Advertisement != null && !string.IsNullOrEmpty(e.Advertisement.LocalName)
                    ? e.Advertisement.LocalName : "?";
                Console.WriteLine("REMOTE SEEN: " + Bt(e.BluetoothAddress) + " name=" + name +
                                  " rssi=" + e.RawSignalStrengthInDBm +
                                  " type=" + e.AdvertisementType + " conn=" + e.IsConnectable);
            }
        };
        watcher.Start();
        System.Threading.Thread.Sleep(secs * 1000);
        watcher.Stop();
        Console.WriteLine("--- " + seen.Count + " advertisers in " + secs + "s; remote hits: " + targetHits);
        foreach (var kv in seen)
            Console.WriteLine("  " + Bt(kv.Key) + " rssi=" + kv.Value);
        return targetHits > 0 ? 0 : 1;
    }
    static string Bt(ulong a) {
        var sb = new System.Text.StringBuilder(17);
        for (int i = 5; i >= 0; i--) { sb.Append(((a >> (8 * i)) & 0xFF).ToString("x2")); if (i > 0) sb.Append(':'); }
        return sb.ToString();
    }
}
