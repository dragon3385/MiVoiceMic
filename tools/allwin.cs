// allwin.cs - diagnostic: enumerate ALL visible top-level windows of ALL
// processes every 50 ms and print any NEW or CHANGED window (pid, process
// name, class, title, rect). Unlike winwatch (wetype pids only) this catches
// a voice panel owned by any process, so a real remote session can be
// compared against an injected one at the window level.
//   allwin <durationMs>
// 中文：全进程窗口监视 —— 捕捉任意进程新弹出的可见窗口，对比真实会话与注入会话的面板差异
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

class AllWin {
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCb cb, IntPtr lp);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    delegate bool EnumCb(IntPtr h, IntPtr lp);
    struct RECT { public int L, T, R, B; }

    static string ExeName(uint pid) {
        IntPtr h = OpenProcess(0x1000, false, pid);   // QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return "?";
        var sb = new StringBuilder(1024); uint size = 1024;
        string name = QueryFullProcessImageName(h, 0, sb, ref size) ? System.IO.Path.GetFileName(sb.ToString()) : "?";
        CloseHandle(h);
        return name;
    }

    static void Main(string[] args) {
        int ms = args.Length > 0 ? int.Parse(args[0]) : 60000;
        var prev = new Dictionary<IntPtr, string>();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) {
            var cur = new Dictionary<IntPtr, string>();
            EnumWindows((h, lp) => {
                if (IsWindowVisible(h)) {
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    var t = new StringBuilder(256); GetWindowText(h, t, 256);
                    var c = new StringBuilder(256); GetClassName(h, c, 256);
                    RECT r; GetWindowRect(h, out r);
                    string key = c + "|" + t + "|" + pid;
                    cur[h] = "[" + c + "] \"" + t + "\" pid=" + pid + "(" + ExeName(pid) + ") rect=" + r.L + "," + r.T + "," + (r.R - r.L) + "x" + (r.B - r.T);
                }
                return true;
            }, IntPtr.Zero);
            foreach (var kv in cur)
                if (!prev.ContainsKey(kv.Key) || prev[kv.Key] != kv.Value)
                    Console.WriteLine(sw.ElapsedMilliseconds + "ms NEW/CHG " + kv.Value);
            foreach (var kv in prev)
                if (!cur.ContainsKey(kv.Key))
                    Console.WriteLine(sw.ElapsedMilliseconds + "ms GONE     " + kv.Value);
            Console.Out.Flush();
            prev = cur;
            Thread.Sleep(50);
        }
        Console.WriteLine("allwin done");
    }
}
