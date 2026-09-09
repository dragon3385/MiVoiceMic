// f5flood.cs - diagnostic: inject F5 key-down events every ~33 ms for <durationMs>
// (the Xiaomi remote's voice key auto-repeat rate as observed via keyspy),
// then a single key-up. Used together with holdkey.exe to reproduce the real
// remote-session condition: Ctrl+Win injected WHILE F5 is being flooded.
//   f5flood <durationMs> [intervalMs]
// 中文：F5 洪泛模拟器 —— 以遥控器语音键的重复速率灌 F5，配合 holdkey 复现真实遥控条件
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

class F5Flood {
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    const uint KEYEVENTF_KEYUP = 0x0002;

    static int Main(string[] args) {
        int ms = args.Length > 0 ? int.Parse(args[0]) : 5000;
        int gap = args.Length > 1 ? int.Parse(args[1]) : 33;
        var sw = Stopwatch.StartNew();
        keybd_event(0x74, 0x3F, 0, UIntPtr.Zero);   // initial down
        while (sw.ElapsedMilliseconds < ms) {
            keybd_event(0x74, 0x3F, 0, UIntPtr.Zero);   // repeat downs (auto-repeat style)
            Thread.Sleep(gap);
        }
        keybd_event(0x74, 0x3F, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Console.WriteLine("f5flood done ({0}ms, {1}ms interval)", ms, gap);
        return 0;
    }
}
