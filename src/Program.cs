// Program.cs - entry point: default run (GUI + tray), --check (environment
// self-check), --selftest (offline logic tests), --sniff (keyboard sniffer),
// --screenshot (render UI pages to PNG without showing a window).
// 中文：入口 —— 默认 GUI 启动，附 --check/--selftest/--e2e/--sniff/--screenshot 工具模式
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

static class Program {
    [DllImport("kernel32.dll")] static extern bool SetConsoleOutputCP(uint cp);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] static extern bool AllocConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

    const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [STAThread]
    static int Main(string[] args) {
        string mode = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
        // winexe build has no console; tool modes re-attach to the calling
        // terminal (or allocate one) so --check/--sniff/--e2e output is visible
        if (mode.Length > 0 && !AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
        try { SetConsoleOutputCP(65001); } catch { }
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        if (mode == "--selftest") return SelfTest.Run();
        if (mode == "--check") return Diag.Run();
        if (mode == "--sniff") return Sniffer.Run(args.Length > 1 ? int.Parse(args[1]) : 20);
        if (mode == "--e2e") return E2E.Run(args);
        if (mode == "--screenshot") return Screenshot.Run(args);

        Log.Init(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MiVoiceMic.log"));
        Log.Info("MiVoiceMic starting (pid " + Process.GetCurrentProcess().Id + ")");

        try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { try { SetProcessDPIAware(); } catch { } }

        // WinForms housekeeping must precede any window/control creation
        System.Windows.Forms.Application.EnableVisualStyles();
        MacTheme.Init();
        var cfg = Config.Load();
        var app = new App(cfg);
        app.Run();
        var main = new MainWindow(app);
        TrayIcon.MainWindow = main;
        TrayIcon.Create(app);
        System.Windows.Forms.Application.Run(main);
        app.Shutdown();

        Log.Info("bye");
        Log.Close();
        return 0;
    }
}

// Screenshot.cs - render UI pages offscreen to PNG
// (usage: MiVoiceMic.exe --screenshot connect|keymap|diag|about|all [outdir])
static class Screenshot {
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    public static int Run(string[] args) {
        string what = args.Length > 1 ? args[1].ToLowerInvariant() : "all";
        string outDir = args.Length > 2 ? args[2] : AppDomain.CurrentDomain.BaseDirectory;
        try { SetProcessDpiAwarenessContext((IntPtr)(-4)); } catch { try { SetProcessDPIAware(); } catch { } }
        System.Windows.Forms.Application.EnableVisualStyles();
        MacTheme.Init();
        var cfg = Config.Load();
        var app = new App(cfg);              // constructed but not Run(): no BLE, no hooks
        using (var form = new MainWindow(app)) {
            form.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            form.Location = new System.Drawing.Point(-32000, -32000);   // offscreen: force full layout/paint
            form.Show();
            form.CreateControl();
            if (what == "editor") {
                var entry = app.Config.keymap.Find("up");
                using (var dlg = new KeyMapEditor(entry, true)) {
                    dlg.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                    dlg.Location = new System.Drawing.Point(-32000, -32000);
                    dlg.Show(form);
                    dlg.SelectGesture(1);                    // 长按
                    dlg.SelectType(1);                       // 组合键 (empty payload)
                    dlg.Refresh();
                    System.Windows.Forms.Application.DoEvents();
                    System.Threading.Thread.Sleep(200);
                    using (var bmp = new System.Drawing.Bitmap(dlg.Width, dlg.Height)) {
                        dlg.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, dlg.Width, dlg.Height));
                        bmp.Save(System.IO.Path.Combine(outDir, "shot_editor.png"), System.Drawing.Imaging.ImageFormat.Png);
                    }
                    dlg.Close();
                }
                return 0;
            }
            string[] names = new string[] { "connect", "keymap", "diag", "about" };
            int saved = 0;
            for (int i = 0; i < names.Length; i++) {
                if (what != "all" && what != names[i]) continue;
                form.SelectPage(i);
                System.Windows.Forms.Application.DoEvents();
                System.Threading.Thread.Sleep(150);
                var diagPage = form.CurrentPage as DiagPage;
                for (int spin = 0; diagPage != null && diagPage.IsBusy && spin < 80; spin++) {
                    System.Threading.Thread.Sleep(100);
                    System.Windows.Forms.Application.DoEvents();
                }
                using (var bmp = new System.Drawing.Bitmap(form.Width, form.Height)) {
                    form.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                    string path = System.IO.Path.Combine(outDir, "shot_" + names[i] + ".png");
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine("saved " + path);
                    saved++;
                }
            }
            return saved > 0 ? 0 : 1;
        }
    }
}

// Diag.cs - environment self-check (run with: MiVoiceMic.exe --check)
static class Diag {
    public static int Run() {
        Console.WriteLine("== MiVoiceMic 环境自检 ==");
        var items = EnvChecks.Run();
        bool ok = true;
        foreach (var item in items) {
            Console.WriteLine();
            Console.WriteLine("[" + item.Title + "]");
            string mark = item.State == CheckState.Ok ? "  ✓ " : item.State == CheckState.Warn ? "  ! " : "  ✗ ";
            if (item.State == CheckState.Fail) ok = false;
            for (int i = 0; i < item.Lines.Count; i++)
                Console.WriteLine((i == 0 ? mark : "    ") + item.Lines[i]);
        }
        Console.WriteLine();
        Console.WriteLine("== 结论 ==");
        Console.WriteLine("  " + (ok ? "环境就绪，直接运行 MiVoiceMic.exe，按住遥控器语音键说话。"
                                      : "完成上述缺失项后重新运行 MiVoiceMic.exe 即可。"));
        return ok ? 0 : 1;
    }
}

// Sniffer.cs - low-level keyboard sniffer for debugging key delivery (run with: MiVoiceMic.exe --sniff [seconds])
static class Sniffer {
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int px, py; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr extra; }

    public static int Run(int seconds) {
        Console.WriteLine("== keyboard sniffer (" + seconds + "s) - press keys on the remote ==");
        Console.WriteLine("   (voice key should show vk=0x74 F5; WeType hotkey uses RAlt+Comma)");
        uint hookTid = 0;
        var ready = new AutoResetEvent(false);
        var t = new Thread((ThreadStart)delegate {
            hookTid = GetCurrentThreadId();
            HookProc proc = delegate (int nCode, IntPtr wParam, IntPtr lParam) {
                if (nCode >= 0) {
                    var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    bool injected = (k.flags & 0x10) != 0;
                    Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " msg=" + ((int)wParam).ToString("X4") +
                        " vk=0x" + k.vkCode.ToString("X2") + " scan=0x" + k.scanCode.ToString("X2") +
                        (injected ? " [injected]" : ""));
                }
                return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
            };
            IntPtr hhk = SetWindowsHookEx(13, proc, GetModuleHandle(null), 0);
            ready.Set();
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(hhk);
        }) { IsBackground = false };
        t.Start();
        ready.WaitOne();
        Thread.Sleep(seconds * 1000);
        PostThreadMessage(hookTid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(2000);
        Console.WriteLine("== done ==");
        return 0;
    }
}
