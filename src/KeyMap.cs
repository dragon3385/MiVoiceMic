// KeyMap.cs - data model for remote-button remapping: per-key click/hold
// gestures bound to keyboard combos, task view, app launch or shell commands.
// Persisted under config.json -> "keymap". Key names are parsed from the same
// table used for formatting (C#5 / .NET 4.8, no SDK deps).
// 中文：按键映射数据模型 —— 点按/长按手势到单键/组合键/任务视图/打开应用/命令的绑定与序列化
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

enum MapActionKind { None, Combo, TaskView, Launch, Cmd }

class KeyMapAction {
    public string kind = "none";      // none | combo | taskview | launch | cmd
    public bool tap = true;           // combo: true = quick tap, false = hold-through
    public bool single = false;       // combo variant: a plain single key (no modifiers intended)
    public string keys = "";          // combo: "LCTRL+Z"
    public string command = "";       // launch / cmd payload
    public uint ms = 600;             // hold: threshold in ms

    public KeyMapAction Clone() {
        var a = new KeyMapAction();
        a.kind = kind; a.tap = tap; a.single = single; a.keys = keys; a.command = command; a.ms = ms;
        return a;
    }

    public static MapActionKind ParseKind(string s) {
        if (string.IsNullOrEmpty(s)) return MapActionKind.None;
        if (s.Equals("combo", StringComparison.OrdinalIgnoreCase)) return MapActionKind.Combo;
        if (s.Equals("taskview", StringComparison.OrdinalIgnoreCase)) return MapActionKind.TaskView;
        if (s.Equals("launch", StringComparison.OrdinalIgnoreCase)) return MapActionKind.Launch;
        if (s.Equals("cmd", StringComparison.OrdinalIgnoreCase)) return MapActionKind.Cmd;
        return MapActionKind.None;
    }

    public static string KindName(MapActionKind k) {
        switch (k) {
            case MapActionKind.Combo: return "combo";
            case MapActionKind.TaskView: return "taskview";
            case MapActionKind.Launch: return "launch";
            case MapActionKind.Cmd: return "cmd";
            default: return "none";
        }
    }

    [ScriptIgnore]
    public MapActionKind Kind { get { return ParseKind(kind); } }

    [ScriptIgnore]
    public bool HasPayload {
        get {
            MapActionKind k = Kind;
            if (k == MapActionKind.None) return false;
            if (k == MapActionKind.TaskView) return true;
            if (k == MapActionKind.Launch || k == MapActionKind.Cmd) return !string.IsNullOrWhiteSpace(command);
            ushort[] combo;
            return KeyMapNames.TryParseCombo(keys, out combo) && combo.Length > 0;
        }
    }

    public KeyMapAction Sanitized() {
        // drop half-entered payloads so partial UI edits cannot arm a broken action
        if (Kind == MapActionKind.Combo && !HasPayload) return new KeyMapAction();
        if ((Kind == MapActionKind.Launch || Kind == MapActionKind.Cmd) && string.IsNullOrWhiteSpace(command))
            return new KeyMapAction();
        return this;
    }
}

class KeyMapEntry {
    public string id = "";            // voice / up / down / left / right / ok / back / ...
    public string name = "";
    public string vk = "";            // key name from KeyMapNames ("F5", "UP", ...)
    public bool needsDriver = false;  // key never reaches Windows without the RemoteMapper KMDF driver
    public KeyMapAction click = new KeyMapAction();
    public KeyMapAction hold;         // null = no long-press gesture

    [ScriptIgnore]
    public ushort VkCode {
        get { ushort v; return KeyMapNames.TryParse(vk, out v) ? v : (ushort)0; }
    }

    [ScriptIgnore]
    public bool Mapped {
        get {
            if (click != null && click.HasPayload) return true;
            if (hold != null && hold.HasPayload) return true;
            return false;
        }
    }

    public KeyMapEntry Clone() {
        var e = new KeyMapEntry();
        e.id = id; e.name = name; e.vk = vk;
        e.click = click != null ? click.Clone() : new KeyMapAction();
        e.hold = hold != null ? hold.Clone() : null;
        return e;
    }
}

class KeyMapConfig {
    public bool enabled = false;
    public List<string> matchVidPid = new List<string> { "2717:32B8" };   // Xiaomi RC003 HID
    public List<KeyMapEntry> keys = DefaultKeys();

    public static List<KeyMapEntry> DefaultKeys() {
        var list = new List<KeyMapEntry>();
        list.Add(NewEntry("voice", "语音键", "F5"));
        list.Add(NewEntry("up", "方向上", "UP"));
        list.Add(NewEntry("down", "方向下", "DOWN"));
        list.Add(NewEntry("left", "方向左", "LEFT"));
        list.Add(NewEntry("right", "方向右", "RIGHT"));
        list.Add(NewEntry("ok", "确定键", "ENTER"));
        // Without the RemoteMapper KMDF driver these keys never reach Windows.
        // With the driver installed they arrive remapped to F13-F19
        // (see reference/RemoteMapper/keymap.json), so binding those vk codes
        // here makes the mappings work as soon as the driver is present.
        list.Add(NewEntry("back", "返回键", "F15", true));
        list.Add(NewEntry("home", "主页键", "F16", true));
        list.Add(NewEntry("volup", "音量加", "F13", true));
        list.Add(NewEntry("voldown", "音量减", "F14", true));
        list.Add(NewEntry("menu", "菜单键", "F17", true));
        list.Add(NewEntry("tv", "直播键", "F18", true));
        list.Add(NewEntry("power", "电源键", "F19", true));
        return list;
    }

    static KeyMapEntry NewEntry(string id, string name, string vk, bool needsDriver = false) {
        var e = new KeyMapEntry();
        e.id = id; e.name = name; e.vk = vk; e.needsDriver = needsDriver;
        return e;
    }

    /// Ensure every known remote key exists exactly once and unknown ids are dropped.
    public void Normalize() {
        if (keys == null) { keys = DefaultKeys(); return; }
        var merged = DefaultKeys();
        foreach (KeyMapEntry e in keys) {
            if (e == null) continue;
            foreach (KeyMapEntry def in merged) {
                if (def.id.Equals(e.id, StringComparison.OrdinalIgnoreCase)) {
                    def.name = string.IsNullOrEmpty(e.name) ? def.name : e.name;
                    def.vk = string.IsNullOrEmpty(e.vk) ? def.vk : e.vk;
                    def.click = e.click != null ? e.click.Sanitized() : new KeyMapAction();
                    def.hold = e.hold != null ? e.hold.Sanitized() : null;
                }
            }
        }
        keys = merged;
        if (matchVidPid == null || matchVidPid.Count == 0) matchVidPid = new List<string> { "2717:32B8" };
    }

    public KeyMapEntry Find(string id) {
        if (keys == null) return null;
        foreach (KeyMapEntry e in keys)
            if (e.id.Equals(id, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    public KeyMapEntry FindByVk(ushort vk) {
        if (keys == null) return null;
        foreach (KeyMapEntry e in keys)
            if (e.VkCode == vk) return e;
        return null;
    }

    public KeyMapConfig Clone() {
        var c = new KeyMapConfig();
        c.enabled = enabled;
        c.matchVidPid = new List<string>(matchVidPid);
        c.keys = new List<KeyMapEntry>();
        foreach (KeyMapEntry e in keys) c.keys.Add(e.Clone());
        return c;
    }
}

// ---- key name table -------------------------------------------------------
static class KeyMapNames {
    static readonly Dictionary<string, ushort> names = BuildNames();
    static readonly Dictionary<ushort, string> vkToName = BuildReverse(names);

    static Dictionary<string, ushort> BuildNames() {
        var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
        for (ushort c = (ushort)'A'; c <= (ushort)'Z'; c++) d[((char)c).ToString()] = c;
        for (ushort c = (ushort)'0'; c <= (ushort)'9'; c++) d[((char)c).ToString()] = c;
        for (ushort i = 1; i <= 24; i++) d["F" + i.ToString()] = (ushort)(0x6F + i);
        d["BACK"] = 0x08; d["TAB"] = 0x09; d["ENTER"] = 0x0D; d["ESC"] = 0x1B; d["SPACE"] = 0x20;
        d["PGUP"] = 0x21; d["PGDN"] = 0x22; d["END"] = 0x23; d["HOME"] = 0x24;
        d["LEFT"] = 0x25; d["UP"] = 0x26; d["RIGHT"] = 0x27; d["DOWN"] = 0x28;
        d["PRTSCR"] = 0x2C; d["INSERT"] = 0x2D; d["DELETE"] = 0x2E;
        d["LWIN"] = 0x5B; d["RWIN"] = 0x5C;
        d["LSHIFT"] = 0xA0; d["RSHIFT"] = 0xA1; d["LCTRL"] = 0xA2; d["RCTRL"] = 0xA3;
        d["LALT"] = 0xA4; d["RALT"] = 0xA5;
        d["CTRL"] = 0xA2; d["ALT"] = 0xA4; d["SHIFT"] = 0xA0; d["WIN"] = 0x5B;
        d["OEM_1"] = 0xBA; d["OEM_PLUS"] = 0xBB; d["OEM_COMMA"] = 0xBC; d["COMMA"] = 0xBC;
        d["OEM_MINUS"] = 0xBD; d["OEM_PERIOD"] = 0xBE; d["PERIOD"] = 0xBE; d["OEM_2"] = 0xBF;
        d["OEM_3"] = 0xC0; d["OEM_4"] = 0xDB; d["OEM_5"] = 0xDC; d["OEM_6"] = 0xDD; d["OEM_7"] = 0xDE;
        return d;
    }

    static Dictionary<ushort, string> BuildReverse(Dictionary<string, ushort> src) {
        var d = new Dictionary<ushort, string>();
        // prefer short canonical spellings when several names map to one vk
        string[] preferred = { "LCTRL","RCTRL","LALT","RALT","LSHIFT","RSHIFT","LWIN","RWIN",
            "COMMA","PERIOD","SPACE","ENTER","TAB","ESC","BACKSPACE","DELETE","INSERT","HOME","END",
            "PGUP","PGDN","LEFT","UP","RIGHT","DOWN","OEM_PLUS","OEM_MINUS","OEM_1","OEM_2","OEM_3",
            "OEM_4","OEM_5","OEM_6","OEM_7","PRTSCR" };
        foreach (string p in preferred) { ushort v; if (src.TryGetValue(p, out v) && !d.ContainsKey(v)) d[v] = p; }
        foreach (var kv in src) if (!d.ContainsKey(kv.Value)) d[kv.Value] = kv.Key;
        return d;
    }

    public static bool TryParse(string text, out ushort vk) {
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
            ushort v;
            if (ushort.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) { vk = v; return true; }
            return false;
        }
        return names.TryGetValue(text, out vk);
    }

    public static bool TryParseCombo(string text, out ushort[] combo) {
        combo = new ushort[0];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
        var list = new List<ushort>();
        foreach (string p in parts) {
            ushort v;
            if (!TryParse(p.Trim(), out v)) return false;
            if (!list.Contains(v)) list.Add(v);
        }
        if (list.Count == 0) return false;
        combo = list.ToArray();
        return true;
    }

    /// Parse a hotkey name list for injection ("LCTRL","WIN","H"...). Unknown
    /// names are warned and skipped; an all-empty result falls back to
    /// Ctrl+Win (WeType default) so a stale config can never silently disable
    /// the voice hotkey. Single source of truth for BOTH the keymap and the
    /// hotkey injector - there used to be a second, smaller table here and
    /// keys like F10+/OEM_*/PGUP were silently dropped at injection time.
    public static ushort[] ParseList(IList<string> names) {
        var list = new List<ushort>();
        if (names != null)
            foreach (string n in names) {
                ushort v;
                if (TryParse(n, out v)) { if (!list.Contains(v)) list.Add(v); }
                else if (!string.IsNullOrEmpty(n)) Log.Warn("[CFG] unknown key name: " + n);
            }
        if (list.Count == 0) { list.Add(0xA2); list.Add(0x5B); }   // Ctrl+Win fallback
        return list.ToArray();
    }

    /// Reorder a parsed combo for injection: Win-class modifiers first, then
    /// other modifiers, trigger keys last (so the caller's reverse-order release
    /// puts the trigger key up first and Win up last). Real-device finding
    /// (2026-09-08, WeType 2.1.3.18): the Ctrl+Win voice hotkey only completes
    /// when Ctrl is the LAST key pressed - injecting the config order
    /// [LCTRL,LWIN] never opens the voice panel, [LWIN,LCTRL] triggers it
    /// instantly. Stable: relative order inside each group is preserved.
    public static ushort[] NormalizeForInject(ushort[] keys) {
        if (keys == null || keys.Length < 2) return keys;
        var win = new List<ushort>();
        var mod = new List<ushort>();
        var rest = new List<ushort>();
        foreach (ushort k in keys) {
            if (k == 0x5B || k == 0x5C) win.Add(k);
            else if ((k >= 0xA0 && k <= 0xA5)) mod.Add(k);
            else rest.Add(k);
        }
        var ordered = new List<ushort>(keys.Length);
        ordered.AddRange(win); ordered.AddRange(mod); ordered.AddRange(rest);
        return ordered.ToArray();
    }

    public static string FormatCombo(ushort[] combo) {
        if (combo == null || combo.Length == 0) return "";
        var parts = new List<string>();
        foreach (ushort v in combo) parts.Add(Name(v));
        return string.Join("+", parts.ToArray());
    }

    public static string Name(ushort vk) {
        string s;
        return vkToName.TryGetValue(vk, out s) ? s : ("0x" + vk.ToString("X2"));
    }

    /// Human-friendly single key label (Win, Ctrl, "," ...).
    public static string Friendly(ushort vk) {
        switch (vk) {
            case 0x08: return "Backspace"; case 0x09: return "Tab"; case 0x0D: return "Enter";
            case 0x1B: return "Esc"; case 0x20: return "Space"; case 0x2E: return "Delete";
            case 0x5B: case 0x5C: return "Win";
            case 0xA0: case 0xA1: return "Shift"; case 0xA2: case 0xA3: return "Ctrl";
            case 0xA4: case 0xA5: return "Alt";
            case 0xBC: return ","; case 0xBE: return "."; case 0xBB: return "+"; case 0xBD: return "-";
            default: return Name(vk);
        }
    }

    public static string FriendlyCombo(ushort[] combo) {
        if (combo == null || combo.Length == 0) return "未设置";
        var parts = new List<string>();
        foreach (ushort v in combo) parts.Add(Friendly(v));
        return string.Join("+", parts.ToArray());
    }

    public static string Describe(KeyMapAction a) {
        if (a == null || !a.HasPayload) return "未设置";
        switch (a.Kind) {
            case MapActionKind.TaskView: return "任务视图";
            case MapActionKind.Launch: return "打开 " + PathDisplayName(a.command);
            case MapActionKind.Cmd: return "运行 " + a.command;
            default: {
                ushort[] combo;
                if (!TryParseCombo(a.keys, out combo)) return "未设置";
                if (a.single) return "按键 " + FriendlyCombo(combo);
                return FriendlyCombo(combo) + (a.tap ? "" : "（按住）");
            }
        }
    }

    public static string PathDisplayName(string command) {
        if (string.IsNullOrWhiteSpace(command)) return "";
        string path = command.Trim();
        if (path.StartsWith("\"")) {
            int end = path.IndexOf('"', 1);
            if (end > 1) path = path.Substring(1, end - 1);
        } else {
            int sp = path.IndexOf(' ');
            if (sp > 0) path = path.Substring(0, sp);
        }
        try { return System.IO.Path.GetFileNameWithoutExtension(path); } catch { return path; }
    }
}
