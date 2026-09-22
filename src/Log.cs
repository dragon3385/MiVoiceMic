// Log.cs - timestamped console + file logger, plus a small in-memory ring the
// UI tail view reads from.
// 中文：日志 —— 控制台 + 文件 + 内存环形缓冲（供关于页日志视图读取）
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

static class Log {
    static StreamWriter file;
    static readonly object gate = new object();
    static readonly Queue<string> ring = new Queue<string>(256);

    public static void Init(string path) {
        try {
            file = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        } catch {
            // a previous instance still holds the log (restart race, 2026-09-22):
            // truncate-open throws and a naive catch left the new instance running
            // with NO file log at all - fall back to append instead of going blind
            try { file = new StreamWriter(path, true, new UTF8Encoding(false)) { AutoFlush = true }; } catch { file = null; }
        }
    }

    public static void Close() {
        lock (gate) {
            if (file != null) { try { file.Dispose(); } catch { } file = null; }
        }
    }

    public static void Info(string msg)  { Write("INFO ", msg); }
    public static void Warn(string msg)  { Write("WARN ", msg); }
    public static void Error(string msg) { Write("ERROR", msg); }
    public static void Voice(string msg) { Write("VOICE", msg); }

    /// Last n lines (oldest first) for the UI log view.
    public static List<string> Tail(int n) {
        lock (gate) {
            var list = new List<string>(ring.Count);
            foreach (string s in ring) list.Add(s);
            if (n > 0 && list.Count > n) list.RemoveRange(0, list.Count - n);
            return list;
        }
    }

    static void Write(string level, string msg) {
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + level + " " + msg;
        lock (gate) {
            try { Console.WriteLine(line); } catch { }
            if (file != null) { try { file.WriteLine(line); } catch { } }
            ring.Enqueue(line);
            if (ring.Count > 256) ring.Dequeue();
        }
    }
}

// UiState.cs - cross-thread UI state hub. BLE/decode worker threads push;
// WinForms timers pull on the UI thread (no cross-thread control access).
static class UiState {
    static readonly object gate = new object();
    static bool linkedFlag;
    static string statusText = "启动中...";
    static string deviceName = "";
    static int battery = -1;
    static int charging = -1;             // -1 unknown, 0 no, 1 charging (2BED)
    static bool talking;
    static double level;                  // 0..1 voice level
    static long version;                  // bumped on every change

    // Remote-key lit state for the UI remote art (按下即亮): one entry per
    // pressed key with the tick of its last device-attributed down/repeat.
    // Removed the moment the matching release is attributed; the KEY_LIT_MS
    // timeout is only a fallback for releases that never arrive (remote asleep
    // mid-press, link drop).
    static readonly Dictionary<string, int> litKeys = new Dictionary<string, int>();
    const int KEY_LIT_MS = 1200;          // lost-release fallback window
    static readonly string[] EmptyLit = new string[0];

    /// Remote key activity (device attributed - physical keyboards never reach
    /// this). Press/device-repeat lights and refreshes; release turns the key
    /// off immediately.
    public static void NoteRemoteKey(string keyId, bool down) {
        if (string.IsNullOrEmpty(keyId)) return;
        lock (gate) {
            if (down) {
                litKeys[keyId] = Environment.TickCount;
                version++;
            } else if (litKeys.Remove(keyId)) {
                version++;                                // repaint dark on the next snapshot
            }
        }
    }

    static void PruneLitLocked() {
        if (litKeys.Count == 0) return;
        int now = Environment.TickCount;
        List<string> dead = null;
        foreach (var kv in litKeys)
            if (now - kv.Value >= KEY_LIT_MS) { if (dead == null) dead = new List<string>(); dead.Add(kv.Key); }
        if (dead != null) {
            foreach (string k in dead) litKeys.Remove(k);
            version++;                    // so the next OnSnapshot repaints the art dark
        }
    }

    /// True while any remote key is still lit (drives the canvas repaint timer).
    public static bool HasLitKey() { lock (gate) { PruneLitLocked(); return litKeys.Count > 0; } }

    /// Test hook: age every lit entry so expiry can be checked without waiting.
    internal static void TestAgeLit(int ms) { lock (gate) { var ks = new List<string>(litKeys.Keys); foreach (string k in ks) litKeys[k] -= ms; } }

    public static void SetStatus(bool connected, string detail, string device) {
        lock (gate) {
            linkedFlag = connected;
            statusText = detail ?? "";
            if (device != null && device.Length > 0) deviceName = device;
            version++;
        }
    }
    public static void SetBattery(int percent) { lock (gate) { battery = percent; version++; } }
    public static void SetCharging(int state) { lock (gate) { charging = state; version++; } }
    public static void SetTalking(bool on) { lock (gate) { talking = on; if (!on) level = 0; version++; } }
    public static void SetLevel(double v) { lock (gate) { level = v; version++; } }

    public struct Snapshot {
        public bool Linked; public string Status, Device; public int Battery;
        public int Charging; public bool Talking; public double Level; public long Version;
        public string[] KeysLit;          // remote keys currently lit on the art

        public bool IsLit(string keyId) {
            if (KeysLit == null) return false;
            for (int i = 0; i < KeysLit.Length; i++) if (KeysLit[i] == keyId) return true;
            return false;
        }
    }

    public static Snapshot Take() {
        lock (gate) {
            PruneLitLocked();
            Snapshot s;
            s.Linked = linkedFlag; s.Status = statusText; s.Device = deviceName;
            s.Battery = battery; s.Charging = charging; s.Talking = talking; s.Level = level; s.Version = version;
            s.KeysLit = litKeys.Count > 0 ? new List<string>(litKeys.Keys).ToArray() : EmptyLit;
            return s;
        }
    }
}
