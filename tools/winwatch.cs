// winwatch.cs - fast panel-flash oracle: enumerate all visible top-level windows
// of wetype* processes every 80ms, print ONLY changes (appear/disappear) with
// timestamps. Usage: winwatch <durationMs>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class WinWatch {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    delegate bool EnumCb(IntPtr h, IntPtr lp);
    struct RECT { public int L, T, R, B; }

    static void Main(string[] args) {
        int ms = args.Length > 0 ? int.Parse(args[0]) : 5000;
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcesses()) {
            if (p.ProcessName.ToLower().StartsWith("wetype")) pids.Add((uint)p.Id);
        }
        Console.WriteLine("watching wetype pids: " + string.Join(",", pids) + " for " + ms + "ms");
        var prev = new Dictionary<IntPtr, string>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) {
            var cur = new Dictionary<IntPtr, string>();
            EnumWindows((h, lp) => {
                if (IsWindowVisible(h)) {
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    if (pids.Contains(pid)) {
                        var t = new StringBuilder(256); GetWindowText(h, t, 256);
                        var c = new StringBuilder(256); GetClassName(h, c, 256);
                        RECT r; GetWindowRect(h, out r);
                        cur[h] = "[" + c + "] \"" + t + "\" rect=" + r.L + "," + r.T + "," + (r.R - r.L) + "x" + (r.B - r.T);
                    }
                }
                return true;
            }, IntPtr.Zero);
            foreach (var kv in cur)
                if (!prev.ContainsKey(kv.Key) || prev[kv.Key] != kv.Value)
                    Console.WriteLine(sw.ElapsedMilliseconds + "ms APPEAR/CHANGE " + kv.Value);
            foreach (var kv in prev)
                if (!cur.ContainsKey(kv.Key))
                    Console.WriteLine(sw.ElapsedMilliseconds + "ms GONE " + kv.Value);
            Console.Out.Flush();
            prev = cur;
            Thread.Sleep(80);
        }
        Console.WriteLine("done");
    }
}
