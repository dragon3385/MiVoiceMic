// SelfTest.cs - offline logic tests (run with: MiVoiceMic.exe --selftest)
// 中文：离线单元测试 —— 解码/配置/键名/手势引擎/引擎复位/钩子判定/F20 归一/充电状态解析/按键归因/按键点亮（91 项）
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

static class SelfTest {
    static int passed, failed;

    static void Check(bool cond, string name) {
        if (cond) { passed++; Console.WriteLine("  PASS  " + name); }
        else { failed++; Console.WriteLine("  FAIL  " + name); }
    }

    public static int Run() {
        Console.WriteLine("== selftest ==");
        TestAdpcmRoundtrip();
        TestNibbleOrder();
        TestFrameAccumulation();
        TestSyncResetsDecoder();
        TestKeyNames();
        TestConfigRoundtrip();
        TestWavWriter();
        TestF5Blocker();
        TestKeyMap();
        TestEngineReset();
        TestChargeState();
        TestLooksFromRemote();
        TestDeferredAttribution();
        TestVoiceKeyF20();
        TestKeyLitState();
        Console.WriteLine("== " + passed + " passed, " + failed + " failed ==");
        return failed == 0 ? 0 : 1;
    }

    static short[] MakeSine(int n, double freqHz, int amp) {
        var s = new short[n];
        for (int i = 0; i < n; i++)
            s[i] = (short)Math.Round(amp * Math.Sin(2 * Math.PI * freqHz * i / 16000.0));
        return s;
    }

    static void TestAdpcmRoundtrip() {
        var decoder = new AdpcmDecoder(false, 0);
        var signal = MakeSine(4800, 440, 6000);
        byte[] encoded = AdpcmDecoder.Encode(signal);
        Check(encoded.Length == (signal.Length + 1) / 2,
            "adpcm encode length (" + encoded.Length + " bytes for " + signal.Length + " samples)");
        var all = new List<short>();
        var decoderOut = decoder.Feed(encoded);
        int frames = decoderOut == null ? 0 : decoderOut.Count;
        if (decoderOut != null) foreach (var block in decoderOut) all.AddRange(block);
        int maxErr = 0;
        int skip = 480;   // decoder cold-start transient: step ramps 7 -> tracking in ~2 frames
        for (int i = skip; i < Math.Min(all.Count, signal.Length); i++)
            maxErr = Math.Max(maxErr, Math.Abs(all[i] - signal[i]));
        Check(all.Count == signal.Length, "adpcm sample count " + all.Count + "/" + signal.Length);
        Check(maxErr < 1500, "adpcm roundtrip max error " + maxErr + " < 1500");
        Check(frames > 0, "adpcm frames decoded: " + frames);
    }

    static void TestNibbleOrder() {
        // one byte 0x0F: high nibble 0 -> predictor += step>>3 = 0 (step 7: 7>>3=0) -> sample 0
        //                 low nibble F -> predictor -= 7+3+1+0? step 7: diff = 0+1+3+7=11 -> -11
        var decoder = new AdpcmDecoder(false, 0);
        decoder.FrameSize = 1;
        var outFrames = decoder.Feed(new byte[] { 0x0F });
        Check(outFrames != null && outFrames.Count == 1 && outFrames[0].Length == 2, "nibble: 2 samples from 1 byte");
        if (outFrames != null && outFrames.Count == 1) {
            // raw decode of byte 0x0F is [0, -11]; the 3-tap lowpass blends the
            // neighbour into sample 0: (0 + 2*0 + (-11)) >> 2 = -3
            Check(outFrames[0][0] == -3 && outFrames[0][1] == -11,
                "nibble order high-first + lowpass (" + outFrames[0][0] + "," + outFrames[0][1] + ")");
        }
    }

    static void TestFrameAccumulation() {
        // 120-byte frames delivered in odd chunks must decode identically to whole frames
        var signal = MakeSine(2400, 200, 4000);
        byte[] encoded = AdpcmDecoder.Encode(signal);
        var a = new AdpcmDecoder(false, 0);
        var whole = new List<short>();
        foreach (var b in a.Feed(encoded)) whole.AddRange(b);

        var c = new AdpcmDecoder(false, 0);
        var frag = new List<short>();
        int chunk = 53;
        for (int off = 0; off < encoded.Length; off += chunk) {
            int len = Math.Min(chunk, encoded.Length - off);
            var piece = new byte[len];
            Array.Copy(encoded, off, piece, 0, len);
            var r = c.Feed(piece);
            if (r != null) foreach (var b in r) frag.AddRange(b);
        }
        bool equal = whole.Count == frag.Count;
        if (equal) for (int i = 0; i < whole.Count; i++) if (whole[i] != frag[i]) { equal = false; break; }
        Check(equal, "fragmented feed == whole feed (" + frag.Count + " vs " + whole.Count + " samples)");
    }

    static void TestSyncResetsDecoder() {
        var d = new AdpcmDecoder(false, 0);
        d.FrameSize = 2;
        d.ApplySync(-1234, 30);
        // two zero nibbles: STEP[30]=130, diff=130>>3=16 -> -1218; then idx 29 (118): diff 14 -> -1204
        var r = d.Feed(new byte[] { 0x00, 0x00 });
        bool ok = false;
        if (r != null && r.Count == 1 && r[0].Length == 4) {
            // raw nibbles (all zero, step 130->29->28->27): [-1218,-1204,-1191,-1179]
            // lowpass: s0=(0+2*-1218+-1204)>>2=-910, s1=(-1218+2*-1204+-1191)>>2=-1205,
            //          s2=-1192, s3=-1179 (last sample untouched)
            ok = r[0][0] == -910 && r[0][1] == -1205 && r[0][2] == -1192 && r[0][3] == -1179;
        }
        Check(ok, "AUDIO_SYNC applied before next frame (" +
            (r != null && r.Count == 1 ? r[0][0] + "," + r[0][1] : "no output") + ")");
    }

    static void TestKeyNames() {
        // the SINGLE key-name table: hotkey injection and keymap parsing must
        // agree (an older private injector table silently dropped F10+/OEM keys)
        ushort vk;
        Check(KeyMapNames.TryParse("RALT", out vk) && vk == 0xA5, "keyname RALT=0xA5");
        Check(KeyMapNames.TryParse("H", out vk) && vk == 0x48, "keyname H=0x48");
        Check(!KeyMapNames.TryParse("NOPE", out vk), "keyname unknown rejected");
        Check(KeyMapNames.TryParse("OEM_PLUS", out vk) && vk == 0xBB, "keyname OEM_PLUS=0xBB");
        Check(KeyMapNames.TryParse("F12", out vk) && vk == 0x7B, "keyname F12=0x7B");
        var list = KeyMapNames.ParseList(new List<string> { "LWIN", "H" });
        Check(list.Length == 2 && list[0] == 0x5B && list[1] == 0x48, "keyname parse list LWIN+H");
        var empty = KeyMapNames.ParseList(new List<string>());
        Check(empty.Length == 2 && empty[0] == 0xA2, "keyname empty list falls back to Ctrl+Win");
        var f12 = KeyMapNames.ParseList(new List<string> { "F12" });
        Check(f12.Length == 1 && f12[0] == 0x7B, "keyname ParseList preserves F12（注入不再丢键）");
        // NormalizeForInject: WeType 2.1.3.18's Ctrl+Win voice hotkey only completes
        // when Ctrl is the LAST key pressed - config order [LCTRL,LWIN] must inject
        // as [LWIN,LCTRL] (real-device test 2026-09-08: Ctrl-first never opened the
        // voice panel).
        var norm = KeyMapNames.NormalizeForInject(new ushort[] { 0xA2, 0x5B });
        Check(norm.Length == 2 && norm[0] == 0x5B && norm[1] == 0xA2, "inject normalize Win before Ctrl（WeType 顺序敏感）");
        var normKeep = KeyMapNames.NormalizeForInject(new ushort[] { 0x5B, 0x48 });
        Check(normKeep.Length == 2 && normKeep[0] == 0x5B && normKeep[1] == 0x48, "inject normalize keeps LWIN+H");
        var normAlt = KeyMapNames.NormalizeForInject(new ushort[] { 0xA5, 0xBC });
        Check(normAlt.Length == 2 && normAlt[0] == 0xA5 && normAlt[1] == 0xBC, "inject normalize keeps RALT+comma (modifier first)");
        var normStable = KeyMapNames.NormalizeForInject(new ushort[] { 0x41, 0x42 });
        Check(normStable.Length == 2 && normStable[0] == 0x41 && normStable[1] == 0x42, "inject normalize stable for non-modifiers");
    }

    static void TestConfigRoundtrip() {
        string tmp = Path.Combine(Path.GetTempPath(), "mivoicemic_test_cfg.json");
        string prevOverride = Config.OverridePath;
        try {
            Config.OverridePath = tmp;
            var cfg = new Config();
            cfg.hotkey.keys = new List<string> { "LWIN", "H" };
            cfg.hotkey.mode = "tap";
            cfg.agc = false;
            cfg.gainDb = 12;
            cfg.blockF5 = false;
            cfg.Save();
            var loaded = Config.Load();
            Check(loaded.hotkey.mode == "tap" && loaded.hotkey.keys.Count == 2 && loaded.hotkey.keys[0] == "LWIN",
                "config json roundtrip hotkey");
            Check(!loaded.agc && Math.Abs(loaded.gainDb - 12) < 0.001, "config json roundtrip audio");
            Check(!loaded.blockF5, "config json roundtrip blockF5");
        } finally {
            Config.OverridePath = prevOverride;
            try { File.Delete(tmp); } catch { }
        }
    }

    static void TestWavWriter() {
        var pcm = new List<short>();
        for (int i = 0; i < 100; i++) pcm.Add((short)i);
        string path = Path.Combine(Path.GetTempPath(), "mivoicemic_test.wav");
        WavWriter.Write(path, pcm, 16000);
        var bytes = File.ReadAllBytes(path);
        bool ok = bytes.Length == 44 + 200 &&
                  bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
                  bytes[8] == 'W' && bytes[9] == 'A' && bytes[10] == 'V' && bytes[11] == 'E';
        Check(ok, "wav header (" + bytes.Length + " bytes)");
        try { File.Delete(path); } catch { }
    }

    static void TestF5Blocker() {
        // drive the hook callback directly (SendInput itself is denied on a locked desktop)
        InputRouter.SetMappingEnabled(false);
        InputRouter.SetBlockF5(true);
        InputRouter.SetLinked(true);
        long before = InputRouter.SwallowedCount;
        IntPtr swallowed = InputRouter.TestDispatch(0x74, true, true);
        long mid = InputRouter.SwallowedCount;
        IntPtr passed = InputRouter.TestDispatch(0x41, true, true);
        long afterLinked = InputRouter.SwallowedCount;
        InputRouter.SetLinked(false);
        IntPtr passedWhenUnlinked = InputRouter.TestDispatch(0x74, true, true);
        long end = InputRouter.SwallowedCount;
        InputRouter.SetLinked(true);
        Check(swallowed == (IntPtr)1 && mid == before + 1, "hook swallows F5 while linked");
        Check(passed != (IntPtr)1 && afterLinked == mid, "hook passes non-F5 while linked");
        Check(passedWhenUnlinked != (IntPtr)1 && end == afterLinked, "hook passes F5 when unlinked");
    }

    static void TestKeyMap() {
        // JSON model roundtrip via Config
        string prevOverride = Config.OverridePath;
        string tmp = Path.Combine(Path.GetTempPath(), "mivoicemic_km.json");
        Config.OverridePath = tmp;
        try {
            var cfg = new Config();
            cfg.keymap.enabled = true;
            cfg.keymap.Find("up").click.kind = "combo";
            cfg.keymap.Find("up").click.keys = "LCTRL+Z";
            cfg.keymap.Find("ok").hold = new KeyMapAction { kind = "taskview", ms = 500 };
            cfg.keymap.Find("voice").click.kind = "combo";       // payload-less -> must normalize away
            cfg.keymap.Find("voice").click.keys = "";
            cfg.Save();
            var loaded = Config.Load();
            var up = loaded.keymap.Find("up");
            var ok = loaded.keymap.Find("ok");
            var voice = loaded.keymap.Find("voice");
            Check(loaded.keymap.enabled && loaded.keymap.keys.Count == 13, "keymap roundtrip shape");
            Check(up != null && up.click.Kind == MapActionKind.Combo && up.click.keys == "LCTRL+Z", "keymap roundtrip combo");
            Check(ok != null && ok.hold != null && ok.hold.Kind == MapActionKind.TaskView && ok.hold.ms == 500, "keymap roundtrip hold");
            Check(voice != null && !voice.Mapped, "keymap empty payload dropped");
        } finally {
            Config.OverridePath = prevOverride;
            try { File.Delete(tmp); } catch { }
        }

        // gesture engine: immediate click when no hold, hold threshold, early-release click
        var mk = new KeyMapConfig();
        mk.Find("up").click.kind = "combo";
        mk.Find("up").click.keys = "LCTRL+Z";
        mk.Find("up").click.tap = false;                          // hold-through combo
        mk.Find("ok").click.kind = "combo";
        mk.Find("ok").click.keys = "LALT+TAB";
        mk.Find("ok").hold = new KeyMapAction { kind = "taskview", ms = 400 };

        var bound = new List<KeyMapEntry>();
        foreach (KeyMapEntry e in mk.keys) if (e.Mapped) bound.Add(e);
        var eng = new GestureEngine(bound);
        var outc = new List<GestureEngine.OutAction>();

        eng.Feed(0x26, true, 1000, outc);                         // UP down (no hold gesture)
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Down && outc[0].Action.keys == "LCTRL+Z",
            "gesture: no-hold combo presses on down");
        outc.Clear();
        eng.Feed(0x26, false, 1100, outc);
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Up, "gesture: combo releases on up");
        outc.Clear();

        eng.Feed(0x0D, true, 2000, outc);
        Check(outc.Count == 0, "gesture: hold candidate waits");
        eng.Tick(2300, outc);
        Check(outc.Count == 0, "gesture: hold not fired before threshold");
        eng.Tick(2500, outc);
        Check(outc.Count == 1 && outc[0].Action.Kind == MapActionKind.TaskView, "gesture: hold fires at threshold");
        outc.Clear();
        eng.Feed(0x0D, false, 3000, outc);
        Check(outc.Count == 0, "gesture: hold release no repeat click");
        outc.Clear();

        eng.Feed(0x0D, true, 5000, outc);
        outc.Clear();
        eng.Feed(0x0D, false, 5200, outc);                        // released early -> click (tap combo = single fire)
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Once && outc[0].Action.keys == "LALT+TAB",
            "gesture: early release taps click combo");
    }

    static void TestEngineReset() {
        // link drop / table swap: the engine must release every combo it holds
        // (their UP is never coming) but never fire half-pressed actions
        var mk = new KeyMapConfig();
        mk.Find("up").click.kind = "combo";
        mk.Find("up").click.keys = "LCTRL+Z";
        mk.Find("up").click.tap = false;                          // hold-through click
        mk.Find("ok").hold = new KeyMapAction { kind = "combo", keys = "LALT+TAB", tap = false, ms = 400 };
        var bound = new List<KeyMapEntry>();
        foreach (KeyMapEntry e in mk.keys) if (e.Mapped) bound.Add(e);
        var eng = new GestureEngine(bound);
        var outc = new List<GestureEngine.OutAction>();

        eng.Feed(0x26, true, 1000, outc);                         // hold-through click pressed (Down went out)
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Down, "reset: 按住时 click 组合键已按下");
        outc.Clear();
        eng.Reset(outc);
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Up && outc[0].Action.keys == "LCTRL+Z",
            "reset: 断链释放已按下的 click 组合键");
        outc.Clear();
        eng.Feed(0x26, false, 1100, outc);                        // late UP after reset: inert
        Check(outc.Count == 0, "reset: 复位后的 UP 不再补发动作");
        outc.Clear();

        eng.Feed(0x0D, true, 2000, outc);
        eng.Tick(2500, outc);                                     // hold threshold crossed
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Down, "reset: hold 已触发按下");
        outc.Clear();
        eng.Reset(outc);
        Check(outc.Count == 1 && outc[0].Phase == GestureEngine.Phase.Up && outc[0].Action.keys == "LALT+TAB",
            "reset: 断链释放已触发的 hold-through");
        outc.Clear();

        eng.Feed(0x0D, true, 5000, outc);                         // armed, below threshold
        Check(outc.Count == 0, "reset: hold 未到阈值无动作");
        eng.Reset(outc);
        Check(outc.Count == 0, "reset: 未触发的 armed 按下不补发动作（断链不触发）");
    }

    static void TestVoiceKeyF20() {
        // with the bundled filter driver the voice key arrives as F20; the hook
        // and the raw sink must treat it exactly like F5 (blockF5 + mapping)
        Check(InputRouter.NormalizeVoiceKey(0x83) == 0x74, "F20 归一化为 F5（驱动语音键）");
        Check(InputRouter.NormalizeVoiceKey(0x74) == 0x74, "F5 保持不变");
        InputRouter.SetMappingEnabled(false);
        InputRouter.SetBlockF5(true);
        InputRouter.SetLinked(true);
        long before = InputRouter.SwallowedCount;
        IntPtr r = InputRouter.TestDispatch(0x83, true, true);
        InputRouter.SetLinked(true);
        Check(r == (IntPtr)1 && InputRouter.SwallowedCount == before + 1, "hook: 驱动下语音键 F20 同样被拦截");
    }

    static void TestChargeState() {
        // 00 61 00: RC003 real-device sample (reference/remote-mic-app bug doc 2026-08-08)
        Check(BleVoiceLink.ParseChargeState(new byte[] { 0x00, 0x61, 0x00 }) == BleVoiceLink.CHG_DISCHARGING,
            "2BED 00 61 00 -> 未充电");
        Check(BleVoiceLink.ParseChargeState(new byte[] { 0x00, 0x21, 0x00 }) == BleVoiceLink.CHG_CHARGING,
            "2BED 00 21 00 -> 充电中");
        Check(BleVoiceLink.ParseChargeState(new byte[] { 0x00, 0x01, 0x00 }) == BleVoiceLink.CHG_UNKNOWN,
            "2BED charge-state=unknown -> 未知");
        Check(BleVoiceLink.ParseChargeState(new byte[] { 0x00, 0x61 }) == BleVoiceLink.CHG_UNKNOWN,
            "2BED short frame rejected");
        Check(BleVoiceLink.ParseChargeState(null) == BleVoiceLink.CHG_UNKNOWN,
            "2BED null rejected");
        Check(BleVoiceLink.ChargeText(BleVoiceLink.CHG_CHARGING) == "充电中" &&
              BleVoiceLink.ChargeText(BleVoiceLink.CHG_DISCHARGING) == "未充电" &&
              BleVoiceLink.ChargeText(BleVoiceLink.CHG_UNKNOWN) == "未知",
            "charge text mapping");
    }

    static void TestLooksFromRemote() {
        // mapped-key device decision: latest speaker wins; no evidence -> the
        // DOWN is deferred (swallowed) and the UP's own WM_INPUT will decide
        RawSink.SeedEvidence(-1, -1);
        Check(!RawSink.LooksFromRemote(), "无证据（安静期）-> 延迟归因");
        RawSink.SeedEvidence(300, -1);
        Check(RawSink.LooksFromRemote(), "遥控器 300ms 前按过 -> 遥控器");
        RawSink.SeedEvidence(-1, 300);
        Check(!RawSink.LooksFromRemote(), "只有物理键盘 300ms 前按过 -> 物理键");
        RawSink.SeedEvidence(500, 100);
        Check(!RawSink.LooksFromRemote(), "物理键盘(100ms)比遥控器(500ms)新 -> 物理键");
        RawSink.SeedEvidence(100, 500);
        Check(RawSink.LooksFromRemote(), "遥控器(100ms)比物理键盘(500ms)新 -> 遥控器");
        RawSink.SeedEvidence(5000, 5000);
        Check(!RawSink.LooksFromRemote(), "双方证据都过期 -> 延迟归因");
        RawSink.SeedEvidence(-1, 5000);
        Check(!RawSink.LooksFromRemote(), "物理键盘证据过期、无遥控器证据 -> 延迟归因");
        RawSink.SeedEvidence(5000, -1);
        Check(!RawSink.LooksFromRemote(), "遥控器证据过期、无物理键盘证据 -> 延迟归因（首按不透传）");
        RawSink.SeedEvidence(2100, 2500);
        Check(!RawSink.LooksFromRemote(), "双方证据都过期(2100/2500ms) -> 延迟归因");
        RawSink.SeedEvidence(2100, 100);
        Check(!RawSink.LooksFromRemote(), "遥控器证据过期但物理键盘新鲜 -> 物理键");
        // latch: a swallowed remote key refreshes its own clock (swallowed keys
        // produce no WM_INPUT), keeping a burst remapped without gaps
        RawSink.SeedEvidence(-1, -1);
        RawSink.NoteRemoteActivity();
        Check(RawSink.LooksFromRemote(), "吞键闩锁续期 -> 遥控器");
        RawSink.SeedEvidence(5000, 5000);
        RawSink.NoteRemoteActivity();
        Check(RawSink.LooksFromRemote(), "证据过期后吞键闩锁 -> 遥控器（连发不断链）");
        RawSink.NoteRemoteActivity();
        RawSink.SeedEvidence(150, 100); // 闩锁发生在150ms前、之后物理键盘100ms前按过
        Check(!RawSink.LooksFromRemote(), "闩锁后物理键盘更新 -> 物理键（打字随时夺回）");
        RawSink.SeedEvidence(-1, -1);   // leave the clean default for other tests
    }

    static void TestDeferredAttribution() {
        // ring lookup: the passed orphan UP's own WM_INPUT is per-event truth
        long now = RawSink.TicksNow();
        RawSink.SeedRing(0x25, false, true, 100);
        Check(RawSink.FindRecent(0x25, false, now - 300, now + 1000) == 1, "ring: 命中遥控器 up");
        Check(RawSink.FindRecent(0x26, false, now - 300, now + 1000) == -1, "ring: vk 不符未命中");
        Check(RawSink.FindRecent(0x25, true, now - 300, now + 1000) == -1, "ring: down/up 类型不符未命中");
        RawSink.SeedRing(0x25, false, false, 50);
        Check(RawSink.FindRecent(0x25, false, now - 300, now + 1000) == 0, "ring: 更新的物理键盘 up 优先");
        RawSink.SeedRing(0x27, true, true, 80);
        Check(RawSink.FindRecent(0x27, true, now - 300, now + 1000) == 1, "ring: 命中遥控器 down（重复键探测用）");

        // hook deferred path: ambiguous mapped DOWN is swallowed (no ghost) and
        // its UP passes so the sink can attribute it; confident remote presses
        // still swallow down+up and feed the gesture engine
        InputRouter.SetBlockF5(false);
        InputRouter.SetLinked(true);
        RawSink.SeedEvidence(-1, -1);
        var map = new KeyMapConfig();
        map.enabled = true;
        map.Find("left").click.kind = "combo";
        map.Find("left").click.keys = "BACK";
        InputRouter.SetKeyMap(map, null);
        IntPtr defDown = InputRouter.TestDispatch(0x25, true);
        IntPtr defUp = InputRouter.TestDispatch(0x25, false);
        Check(defDown == (IntPtr)1, "延迟归因：无证据 DOWN 吞下（不透传防 ghost）");
        Check(defUp != (IntPtr)1, "延迟归因：UP 放行（孤儿抬起，供 WM_INPUT 归因）");
        // hold-to-repeat: the first auto-repeat is passed as the device probe;
        // a physical verdict keeps the repeat stream real, a remote one stops it
        RawSink.SeedEvidence(-1, -1);
        Check(InputRouter.TestDispatch(0x25, true) == (IntPtr)1, "重复键：按下吞下");
        Check(InputRouter.TestDispatch(0x25, true) != (IntPtr)1, "重复键：首条自动重复放行（探测）");
        InputRouter.TestSetProbeVerdict(0x25, 0);
        Check(InputRouter.TestDispatch(0x25, true) != (IntPtr)1, "重复键：物理键盘判定 → 后续重复键放行");
        Check(InputRouter.TestDispatch(0x25, false) != (IntPtr)1, "重复键：物理键盘 UP 放行");
        RawSink.SeedEvidence(-1, -1);
        Check(InputRouter.TestDispatch(0x25, true) == (IntPtr)1, "重复键(遥控器)：按下吞下");
        Check(InputRouter.TestDispatch(0x25, true) != (IntPtr)1, "重复键(遥控器)：首条自动重复放行（探测）");
        InputRouter.TestSetProbeVerdict(0x25, 1);
        Check(InputRouter.TestDispatch(0x25, true) == (IntPtr)1, "重复键(遥控器)：遥控器判定 → 后续重复键吞下");
        Check(InputRouter.TestDispatch(0x25, false) != (IntPtr)1, "重复键(遥控器)：UP 仍放行（避免应用卡键）");
        RawSink.SeedEvidence(100, -1);
        IntPtr remDown = InputRouter.TestDispatch(0x25, true);
        IntPtr remUp = InputRouter.TestDispatch(0x25, false);
        Check(remDown == (IntPtr)1 && remUp == (IntPtr)1, "证据明确遥控器：down/up 都吞下并映射");
        InputRouter.SetKeyMap(null);
        RawSink.SeedEvidence(-1, -1);
        InputRouter.SetBlockF5(true);
    }

    static void TestKeyLitState() {
        // the lit state is fed only from device-attributed remote events; the
        // vk->keyId table comes from the keymap (all keys, mapped or not)
        UiState.TestAgeLit(100000); UiState.Take();              // clear residue lit by the hook tests above
        var map = new KeyMapConfig();
        map.enabled = false;                                     // lighting must not depend on mapping being on
        InputRouter.SetKeyMap(map, null);
        long v0 = UiState.Take().Version;
        InputRouter.NoteRemoteKey(0x27, true);                   // VK_RIGHT down = 遥控器方向右
        var s = UiState.Take();
        Check(s.IsLit("right"), "按键点亮：遥控器方向右按下点亮");
        Check(s.Version > v0, "按键点亮：版本号推进（触发重绘）");
        long v1 = s.Version;
        InputRouter.NoteRemoteKey(0x99, true);                   // unbound vk: no key, no repaint
        s = UiState.Take();
        Check(!s.IsLit("ok") && s.Version == v1, "按键点亮：未知 vk 不点亮、不触发重绘");
        Check(UiState.HasLitKey(), "按键点亮：HasLitKey 报告点亮中");
        long v2 = s.Version;
        InputRouter.NoteRemoteKey(0x27, false);                  // up: off immediately, no 1.2s linger
        s = UiState.Take();
        Check(!s.IsLit("right"), "按键点亮：松开立即熄灭");
        Check(!UiState.HasLitKey(), "按键点亮：松开后 HasLitKey 为 false");
        Check(s.Version > v2, "按键点亮：松开触发重绘");
        long v3 = s.Version;
        InputRouter.NoteRemoteKey(0x27, false);                  // duplicate up: no repaint churn
        Check(UiState.Take().Version == v3, "按键点亮：重复松开不触发重绘");
        // lost-release fallback: a down whose up never arrives expires on its own
        InputRouter.NoteRemoteKey(0x27, true);
        Check(UiState.Take().IsLit("right"), "按键点亮：再次按下点亮");
        UiState.TestAgeLit(1300);                                // past the 1200ms holdover
        s = UiState.Take();
        Check(!s.IsLit("right"), "按键点亮：丢失松开时超时自动熄灭");
        Check(!UiState.HasLitKey(), "按键点亮：超时后 HasLitKey 为 false");
        InputRouter.SetKeyMap(null);                             // no table -> nothing lights
        InputRouter.NoteRemoteKey(0x27, true);
        Check(!UiState.Take().IsLit("right"), "按键点亮：空映射表不点亮");
    }
}
