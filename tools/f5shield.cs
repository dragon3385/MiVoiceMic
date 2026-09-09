// f5shield.cs - diagnostic: install a WH_KEYBOARD_LL hook that swallows ONLY
// F5 (any origin, injected included) for <durationMs>, then exits. Proves
// whether keeping the remote's F5 auto-repeat away from the IME restores the
// Ctrl+Win voice-panel trigger (standalone replica of the app's shield hook,
// testable without touching the running app).
//   f5shield <durationMs>
// 中文：F5 盾原型 —— 独立进程版只吞 F5 的 LL 钩子，验证盾机制能否救回触发
using System;
using System.Runtime.InteropServices;

class F5Shield {
    const int WH_KEYBOARD_LL = 13;
    struct KBDLLHOOKDATA { public uint vk, scan, flags, time; public UIntPtr extra; }
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr mod, uint tid);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int c, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr w, uint min, uint max);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    static HookProc proc = Cb;
    static IntPtr hook;

    static IntPtr Cb(int code, IntPtr wParam, IntPtr lParam) {
        if (code >= 0) {
            var k = (KBDLLHOOKDATA)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKDATA));
            if (k.vk == 0x74) return (IntPtr)1;   // swallow F5, everything else passes
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    static void Main(string[] args) {
        int ms = args.Length > 0 ? int.Parse(args[0]) : 9000;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) { Console.WriteLine("hook FAILED err=" + Marshal.GetLastWin32Error()); return; }
        Console.WriteLine("f5shield armed 0x" + hook.ToString("X") + " for " + ms + "ms");
        var killer = new System.Threading.Timer(delegate(object s) { Environment.Exit(0); }, null, ms, System.Threading.Timeout.Infinite);
        MSG m;
        while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
        Environment.Exit(0);
    }
}
