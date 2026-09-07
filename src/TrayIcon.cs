// TrayIcon.cs - WinForms notify icon: status, preset switching, toggles, reconnect.
// The main window owns the message loop; this class only creates the icon/menu.
// Status updates arrive from BLE threads; they are cached here and applied on the
// UI thread by a timer tick (no cross-thread control access).
// 中文：托盘图标 —— 状态/预设/开关快捷菜单；含 AutoStart 开机自启（HKCU Run 键）助手
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

static class TrayIcon {
    static NotifyIcon icon;
    static ToolStripMenuItem miStatus, miBattery, miBlockF5, miSwitchMic;
    static List<ToolStripMenuItem> presetItems = new List<ToolStripMenuItem>();
    static App app;

    public static System.Windows.Forms.Form MainWindow;

    public static NotifyIcon Instance { get { return icon; } }

    // cross-thread cached state
    static readonly object gate = new object();
    static volatile string statusText = "启动中...";
    static volatile bool linkedFlag;
    static volatile int battery = -1;
    static volatile int charging = -1;
    static volatile bool dirty = true;

    public static NotifyIcon Create(App application) {
        app = application;
        var menu = new ContextMenuStrip();

        menu.Items.Add(new ToolStripMenuItem("打开主界面", null, delegate { Safe(ShowMain); }));
        menu.Items.Add(new ToolStripSeparator());

        miStatus = new ToolStripMenuItem("状态: 启动中...") { Enabled = false };
        miBattery = new ToolStripMenuItem("电量: --") { Enabled = false, Visible = false };
        menu.Items.Add(miStatus);
        menu.Items.Add(miBattery);
        menu.Items.Add(new ToolStripSeparator());

        var miPreset = new ToolStripMenuItem("语音热键");
        var presets = new[] {
            new { Text = "微信输入法 (Ctrl+Win 按住)", Key = "wetype" },
            new { Text = "Windows 语音键入 (Win+H 点按)", Key = "winh" },
            new { Text = "不注入热键 (仅推流音频)", Key = "none" },
        };
        foreach (var p in presets) {
            string key = p.Key;
            var item = new ToolStripMenuItem(p.Text, null, delegate { SetPreset(key); });
            presetItems.Add(item);
            miPreset.DropDownItems.Add(item);
        }
        menu.Items.Add(miPreset);

        miBlockF5 = new ToolStripMenuItem("拦截遥控器语音键的 F5", null, delegate { ToggleBlockF5(); });
        miBlockF5.Checked = app.Config.blockF5;
        miSwitchMic = new ToolStripMenuItem("说话时自动切换默认麦克风", null, delegate { ToggleSwitchMic(); });
        miSwitchMic.Checked = app.Config.switchDefaultMic;
        menu.Items.Add(miBlockF5);
        menu.Items.Add(miSwitchMic);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem("重新连接遥控器", null, delegate { Safe(delegate { app.Reconnect(); }); }));
        menu.Items.Add(new ToolStripMenuItem("编辑配置 (config.json)", null, delegate { OpenConfig(); }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, delegate {
            Safe(delegate { app.Shutdown(); });
            if (icon != null) icon.Visible = false;
            Application.ExitThread();
            Environment.Exit(0);
        }));

        System.Drawing.Icon trayIcon = null;
        try { trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        icon = new NotifyIcon {
            Icon = trayIcon ?? System.Drawing.SystemIcons.Information,
            Text = "MiVoiceMic - 小米遥控器语音",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += delegate { Safe(ShowMain); };

        var timer = new Timer { Interval = 800 };
        timer.Tick += delegate { ApplyPending(); };
        timer.Start();
        ApplyConfigChecks();
        return icon;
    }

    static void ShowMain() {
        if (MainWindow != null) { MainWindow.Show(); MainWindow.WindowState = FormWindowState.Normal; ActivateWindow(MainWindow); }
    }

    static void ActivateWindow(Form f) {
        try {
            uint fore = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), IntPtr.Zero);
            uint mine = Native.GetCurrentThreadId();
            if (fore != mine) Native.AttachThreadInput(fore, mine, true);
            Native.SetForegroundWindow(f.Handle);
            if (fore != mine) Native.AttachThreadInput(fore, mine, false);
        } catch { }
    }

    static void Safe(Action a) {
        try { a(); } catch (Exception ex) { Log.Error("[TRAY] " + ex.Message); }
    }

    static void SetPreset(string preset) {
        Safe(delegate {
            var cfg = app.Config;
            cfg.hotkey.preset = preset;
            if (preset == "wetype") {
                cfg.hotkey.mode = "hold";
                cfg.hotkey.keys = new List<string> { "LCTRL", "LWIN" };
                cfg.hotkeyEnabled = true;
            } else if (preset == "winh") {
                cfg.hotkey.mode = "tap";
                cfg.hotkey.keys = new List<string> { "LWIN", "H" };
                cfg.hotkeyEnabled = true;
            } else {
                cfg.hotkeyEnabled = false;
            }
            cfg.Save();
            app.ApplyConfig(cfg);
            ApplyConfigChecks();
            Log.Info("[TRAY] preset -> " + preset);
        });
    }

    static void ToggleBlockF5() {
        Safe(delegate {
            var cfg = app.Config;
            cfg.blockF5 = !cfg.blockF5;
            cfg.Save();
            app.ApplyConfig(cfg);
            ApplyConfigChecks();
        });
    }

    static void ToggleSwitchMic() {
        Safe(delegate {
            var cfg = app.Config;
            cfg.switchDefaultMic = !cfg.switchDefaultMic;
            cfg.Save();
            app.ApplyConfig(cfg);
            ApplyConfigChecks();
        });
    }

    static void ApplyConfigChecks() {
        string preset = app.Config.hotkeyEnabled ? app.Config.hotkey.preset : "none";
        for (int i = 0; i < presetItems.Count; i++)
            presetItems[i].Checked = (i == 0 && preset == "wetype") || (i == 1 && preset == "winh") || (i == 2 && preset == "none");
        miBlockF5.Checked = app.Config.blockF5;
        miSwitchMic.Checked = app.Config.switchDefaultMic;
    }

    static void OpenConfig() {
        try { Process.Start("notepad.exe", Config.ConfigPath); } catch (Exception ex) { Log.Error("[TRAY] " + ex.Message); }
    }

    /// Called from any thread.
    public static void SetStatus(bool connected, string detail) {
        lock (gate) {
            linkedFlag = connected;
            string text = connected ? "已连接: " + detail : detail;
            if (text.Length > 40) text = text.Substring(0, 40) + "...";
            statusText = text;
            dirty = true;
        }
    }

    /// Called from any thread.
    public static void SetBattery(int percent) {
        lock (gate) { battery = percent; dirty = true; }
    }

    /// Called from any thread.
    public static void SetCharging(int state) {
        lock (gate) { charging = state; dirty = true; }
    }

    static void ApplyPending() {
        if (!dirty) return;
        string st; bool linked; int bat; int chg;
        lock (gate) {
            st = statusText; linked = linkedFlag; bat = battery; chg = charging;
            dirty = false;
        }
        miStatus.Text = "状态: " + st;
        if (bat >= 0) { miBattery.Visible = true; miBattery.Text = "电量: " + bat + "%" + (chg == 1 ? " (充电中)" : ""); }
    }
}

// HKCU Run-key autostart toggle used by the remote-behaviour card.
static class AutoStart {
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MiVoiceMic";

    public static bool IsEnabled() {
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(KeyPath))
                return k != null && k.GetValue(ValueName) != null;
        } catch { return false; }
    }

    public static void Set(bool on) {
        try {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(KeyPath)) {
                if (on) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(ValueName, false);
            }
        } catch (Exception ex) { Log.Error("[AUTOSTART] " + ex.Message); }
    }
}

static class Native {
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
