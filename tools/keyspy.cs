// keyspy.cs - diagnostic: low-level keyboard hook logger from a neutral third
// process. Records EVERY keyboard event (physical and injected, any sender)
// with full raw fields so two injection streams can be diffed byte-for-byte:
// vkCode, scanCode, flags (INJECTED/LOWER_IL/EXTENDED/ALTDOWN/UP), time stamp,
// dwExtraInfo.
//   keyspy <durationMs> [vkFilterHex ...]   e.g. keyspy 30000 5B A2 74
// 中文：键盘事件原始流记录器 —— 第三方钩子视角逐字节对比两条注入流
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

class KeySpy {
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    struct KBDLLHOOKDATA { public uint vk, scan, flags, time; public UIntPtr extra; }
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetMessage(out MSG m, IntPtr w, uint min, uint max);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    static IntPtr hook;
    static void Main(string[] args) {
        int ms = args.Length > 0 ? int.Parse(args[0]) : 30000;
        var filter = new System.Collections.Generic.List<int>();
        for (int i = 1; i < args.Length; i++) filter.Add(Convert.ToInt32(args[i], 16));
        var sw = Stopwatch.StartNew();
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, (code, wp, lp) => {
            if (code >= 0) {
                var k = (KBDLLHOOKDATA)Marshal.PtrToStructure(lp, typeof(KBDLLHOOKDATA));
                if (filter.Count == 0 || filter.Contains((int)k.vk)) {
                    string what = wp.ToInt32() == WM_KEYDOWN ? "DOWN" : wp.ToInt32() == WM_SYSKEYDOWN ? "SYSDOWN"
                                : wp.ToInt32() == WM_KEYUP ? "UP" : "SYSUP";
                    Console.WriteLine("{0,7}ms vk=0x{1:X2} scan=0x{2:X2} {3} flags=0x{4:X2} extra=0x{5} t={6}",
                        sw.ElapsedMilliseconds, k.vk, k.scan, what, k.flags, k.extra, k.time);
                    Console.Out.Flush();
                }
            }
            return CallNextHookEx(hook, code, wp, lp);
        }, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) { Console.WriteLine("hook failed"); return; }
        var t = new Thread((ThreadStart)delegate {
            Thread.Sleep(ms);
            Environment.Exit(0);
        });
        t.Start();
        MSG m;
        while (true) { GetMessage(out m, IntPtr.Zero, 0, 0); }
        UnhookWindowsHookEx(hook);
    }
}
