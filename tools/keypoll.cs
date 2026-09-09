// keypoll.cs - diagnostic: poll GetAsyncKeyState for the voice-combo keys at
// high frequency and log every down/up transition with timestamps. Verifies
// whether the app's injected Ctrl+Win actually reaches the system key state
// during a real remote session (hooks can't lie here - this reads the global
// async key state).
//   keypoll <durationMs>
// 中文：组合键到达性探针 —— 高频轮询全局异步键状态，验证注入是否真正抵达系统
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

class KeyPoll {
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    static void Main(string[] args) {
        int ms = args.Length > 0 ? int.Parse(args[0]) : 60000;
        var keys = new[] { new { vk = 0x5B, name = "LWIN" }, new { vk = 0xA2, name = "LCTRL" }, new { vk = 0x74, name = "F5" } };
        var down = new bool[keys.Length];
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) {
            for (int i = 0; i < keys.Length; i++) {
                bool d = (GetAsyncKeyState(keys[i].vk) & 0x8000) != 0;
                if (d != down[i]) {
                    down[i] = d;
                    Console.WriteLine("{0,6}ms {1} {2}", sw.ElapsedMilliseconds, keys[i].name, d ? "DOWN" : "UP");
                }
            }
            Thread.Sleep(4);
        }
        Console.WriteLine("keypoll done");
    }
}
