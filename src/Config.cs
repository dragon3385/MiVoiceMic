// Config.cs - JSON config load/save (JavaScriptSerializer, no external deps)
// 中文：JSON 配置读写 —— 热键、按键映射、统计等全部设置（无外部依赖）
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

class HotkeyConfig {
    public string mode = "hold";          // "hold" = press&release the combo with the voice key,
                                          // "tap"  = tap combo on press AND on release (toggle-style tools)
    public List<string> keys = new List<string> { "LCTRL", "LWIN" };  // WeType default: Ctrl+Win
    public string preset = "wetype";      // informational label: wetype / winh / custom
}

class StatsConfig {
    public string day = "";               // yyyy-MM-dd of the daily counters
    public int daySessions = 0;
    public double daySeconds = 0;
    public int totalSessions = 0;
    public double totalSeconds = 0;

    public void AddSession(double seconds) {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (!today.Equals(day, StringComparison.Ordinal)) { day = today; daySessions = 0; daySeconds = 0; }
        daySessions++;
        daySeconds += seconds;
        totalSessions++;
        totalSeconds += seconds;
    }
}

class Config {
    // BLE matching
    public List<string> deviceNames = new List<string> {
        "MI RC", "Xiaomi Bluetooth Remote 2 Pro", "Xiaomi Bluetooth Remote 2", "小米蓝牙语音遥控器"
    };
    public string deviceMacPrefix = "C0:5D:39";
    public bool autoPair = true;          // try to pair when an unpaired remote is seen advertising

    // audio
    public string cableRenderName = "CABLE Input";    // waveOut target (render side of the loopback cable)
    public string cableCaptureName = "CABLE Output";  // default-mic switch target (capture side)
    public bool switchDefaultMic = true;  // auto-switch default capture device while talking
    public int switchLeadMs = 120;        // wait after switching default mic before injecting hotkey
    public bool agc = true;               // adaptive gain (recommended)
    public double gainDb = 0;             // fixed gain in dB, used when agc=false (-24..24)

    // voice hotkey / trigger
    public HotkeyConfig hotkey = new HotkeyConfig();

    // key mapping (remote buttons -> actions)
    public KeyMapConfig keymap = new KeyMapConfig();

    // usage statistics
    public StatsConfig stats = new StatsConfig();

    // keyboard
    public bool blockF5 = true;           // swallow the remote's voice-key F5 while linked

    // misc
    public bool dumpAudio = false;        // save each voice session to wav (next to the exe)
    public bool showTray = true;
    public bool hotkeyEnabled = true;     // master switch for injection (audio-only mode when false)

    public static string OverridePath;   // for tests

    public static string ConfigPath {
        get { return OverridePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"); }
    }

    public static Config Load() {
        var cfg = new Config();
        string path = ConfigPath;
        if (!File.Exists(path)) {
            cfg.Save();
            Log.Info("config: created default " + path);
            return cfg;
        }
        try {
            var ser = new JavaScriptSerializer();
            var dict = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            if (dict == null) return cfg;
            cfg.deviceNames   = GetStringList(dict, "deviceNames", cfg.deviceNames);
            cfg.deviceMacPrefix = GetStr(dict, "deviceMacPrefix", cfg.deviceMacPrefix);
            cfg.autoPair      = GetBool(dict, "autoPair", cfg.autoPair);
            cfg.cableRenderName = GetStr(dict, "cableRenderName", cfg.cableRenderName);
            cfg.cableCaptureName = GetStr(dict, "cableCaptureName", cfg.cableCaptureName);
            cfg.switchDefaultMic = GetBool(dict, "switchDefaultMic", cfg.switchDefaultMic);
            cfg.switchLeadMs  = GetInt(dict, "switchLeadMs", cfg.switchLeadMs);
            cfg.agc           = GetBool(dict, "agc", cfg.agc);
            cfg.gainDb        = GetDouble(dict, "gainDb", cfg.gainDb);
            cfg.blockF5       = GetBool(dict, "blockF5", cfg.blockF5);
            cfg.dumpAudio     = GetBool(dict, "dumpAudio", cfg.dumpAudio);
            cfg.showTray      = GetBool(dict, "showTray", cfg.showTray);
            cfg.hotkeyEnabled = GetBool(dict, "hotkeyEnabled", cfg.hotkeyEnabled);
            object kmObj;
            if (dict.TryGetValue("keymap", out kmObj)) cfg.keymap = ConvertSection<KeyMapConfig>(kmObj) ?? cfg.keymap;
            object stObj;
            if (dict.TryGetValue("stats", out stObj)) cfg.stats = ConvertSection<StatsConfig>(stObj) ?? cfg.stats;
            object hkObj;
            if (dict.TryGetValue("hotkey", out hkObj) && hkObj is Dictionary<string, object>) {
                var hk = (Dictionary<string, object>)hkObj;
                cfg.hotkey.mode  = GetStr(hk, "mode", cfg.hotkey.mode);
                cfg.hotkey.keys  = GetStringList(hk, "keys", cfg.hotkey.keys);
                cfg.hotkey.preset = GetStr(hk, "preset", cfg.hotkey.preset);
            }
            cfg.keymap.Normalize();
        } catch (Exception ex) {
            Log.Error("config: failed to parse " + path + " (" + ex.Message + "), using defaults");
        }
        return cfg;
    }

    static T ConvertSection<T>(object obj) where T : class {
        try {
            if (obj is T) return (T)obj;
            var ser = new JavaScriptSerializer();
            return ser.ConvertToType<T>(obj);
        } catch (Exception ex) {
            Log.Error("config: section " + typeof(T).Name + " invalid (" + ex.Message + "), using defaults");
            return null;
        }
    }

    static readonly object saveGate = new object();

    public void Save() {
      lock (saveGate) {
        var ser = new JavaScriptSerializer();
        var root = new Dictionary<string, object>();
        root["deviceNames"] = deviceNames;
        root["deviceMacPrefix"] = deviceMacPrefix;
        root["autoPair"] = autoPair;
        root["cableRenderName"] = cableRenderName;
        root["cableCaptureName"] = cableCaptureName;
        root["switchDefaultMic"] = switchDefaultMic;
        root["switchLeadMs"] = switchLeadMs;
        root["agc"] = agc;
        root["gainDb"] = gainDb;
        var hk = new Dictionary<string, object>();
        hk["mode"] = hotkey.mode;
        hk["keys"] = hotkey.keys;
        hk["preset"] = hotkey.preset;
        root["hotkey"] = hk;
        root["blockF5"] = blockF5;
        root["dumpAudio"] = dumpAudio;
        root["showTray"] = showTray;
        root["hotkeyEnabled"] = hotkeyEnabled;
        root["keymap"] = keymap;
        root["stats"] = stats;
        try {
            // atomic replace: a crash mid-write must never leave a half config
            string path = ConfigPath, tmp = path + ".tmp";
            File.WriteAllText(tmp, ser.Serialize(root), System.Text.Encoding.UTF8);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        } catch (Exception ex) {
            Log.Error("config: save failed: " + ex.Message);
        }
      }
    }

    static string GetStr(IDictionary<string, object> d, string k, string def) {
        object v; if (d.TryGetValue(k, out v) && v is string && ((string)v).Length > 0) return (string)v; return def;
    }
    static bool GetBool(IDictionary<string, object> d, string k, bool def) {
        object v; if (d.TryGetValue(k, out v) && v is bool) return (bool)v; return def;
    }
    static int GetInt(IDictionary<string, object> d, string k, int def) {
        object v; if (d.TryGetValue(k, out v) && v is int) return (int)v;
        if (d.TryGetValue(k, out v) && v is double) return (int)(double)v;
        return def;
    }
    static double GetDouble(IDictionary<string, object> d, string k, double def) {
        object v;
        if (d.TryGetValue(k, out v)) {
            if (v is double) return (double)v;                    // "12.5"
            if (v is int) return (int)v;                          // "12" parses as Int32
        }
        return def;
    }
    static List<string> GetStringList(IDictionary<string, object> d, string k, List<string> def) {
        object v;
        if (d.TryGetValue(k, out v) && v is ArrayList) {
            var result = new List<string>();
            foreach (object item in (ArrayList)v) if (item is string && ((string)item).Length > 0) result.Add((string)item);
            if (result.Count > 0) return result;
        }
        return def;
    }
}
