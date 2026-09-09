// App.cs - orchestrator: BLE voice events -> ADPCM decode -> cable push,
// default-mic switch + IME hotkey injection on a dedicated worker thread.
// 中文：主编排器 —— 串联 BLE 语音事件、解码、声卡推流、默认麦克风切换、热键注入与按键映射路由
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

sealed class App : BleVoiceLink.IHandler {
    readonly Config cfg;
    readonly BleVoiceLink link;
    readonly AdpcmDecoder decoder;
    readonly AudioOut audioOut = new AudioOut();
    readonly DeviceSwitcher switcher = new DeviceSwitcher();
    HotkeyInjector injector;
    HotkeyInjector pendingInjector;    // hotkey edited mid-session: swapped in after release

    // key/voice action worker (never inject SendInput from hook or WinRT threads)
    enum KeyActionKind { VoiceDown, VoiceUp }
    sealed class KeyAction { public KeyActionKind Kind; }
    readonly BlockingCollection<KeyAction> keyQueue = new BlockingCollection<KeyAction>();
    Thread keyWorker;

    // decode worker (serializes BLE audio notifications)
    readonly BlockingCollection<byte[]> audioQueue = new BlockingCollection<byte[]>(200);
    Thread audioThread;

    volatile bool talking;
    volatile bool hotkeyHeld;          // physical voice-key session (HTT, 0x03); drives mic switch + hotkey
    DateTime talkStart;
    long framesDecoded;
    List<short> dumpBuffer;
    DateTime lastAudioRetry = DateTime.MinValue;   // late-attach: VB-CABLE may be installed while we run

    public bool AudioOk { get; private set; }
    public bool SwitcherOk { get { return switcher.TargetFound; } }
    public Config Config { get { return cfg; } }

    public App(Config cfg) {
        this.cfg = cfg;
        decoder = new AdpcmDecoder(cfg.agc, cfg.gainDb);
        injector = new HotkeyInjector(cfg.hotkey.keys, cfg.hotkey.mode);
        link = new BleVoiceLink(cfg, this);
    }

    public void Run() {
        Log.Info("== MiVoiceMic - 小米蓝牙遥控器 2 Pro -> 微信输入法语音 ==");
        Log.Info("config: " + Config.ConfigPath);

        // 1. cable render side
        AudioOk = audioOut.Start(cfg.cableRenderName, 16000);
        if (!AudioOk) {
            Log.Error("[AUDIO] 未找到虚拟声卡 \"" + cfg.cableRenderName + "\" - 请安装 VB-CABLE (见 README)");
            Log.Error("[AUDIO] 无声卡模式下仍可用：热键注入照常，但语音将使用电脑自带麦克风");
        } else {
            Log.Info("[AUDIO] 推流目标: " + audioOut.DeviceUsed);
        }

        // 2. cable capture side (default-mic switching)
        bool capOk = switcher.FindTarget(cfg.cableCaptureName);
        if (!capOk) Log.Warn("[AUDIO] 未找到录音设备 \"" + cfg.cableCaptureName + "\" - 说话期间将不切换默认麦克风");

        // 3. hotkey
        Log.Info("[KEY] 语音热键: " + injector.Describe() + (cfg.hotkeyEnabled ? "" : " (注入已禁用，切麦/推流照常)"));

        // 4. input router (F5 blocker + key mapping) + workers
        InputRouter.SetBlockF5(cfg.blockF5);
        InputRouter.SetKeyMap(cfg.keymap, cfg.deviceMacPrefix);
        InputRouter.Start();
        keyWorker = new Thread(KeyWorkerLoop) { IsBackground = true, Name = "keyworker" };
        keyWorker.Start();
        audioThread = new Thread(AudioLoop) { IsBackground = true, Name = "decode" };
        audioThread.Start();

        // 5. BLE
        link.Start();
    }

    public void Shutdown() {
        try { injector.ForceRelease(); } catch { }
        try { switcher.Restore(); } catch { }
        try { link.Stop(); } catch { }
        try { audioOut.Stop(); } catch { }
        InputRouter.Stop();
        keyQueue.CompleteAdding();
        audioQueue.CompleteAdding();
    }

    public void Reconnect() { link.Reconnect(); }

    /// Runtime gain change from the settings UI.
    public void SetAudioGain(bool agcOn, double gainDb) { decoder.SetGain(agcOn, gainDb); }

    /// Push 1 s of 440 Hz tone through the cable so the user can verify the
    /// audio path end to end (mute the real mic first if unsure).
    public void PlayTestTone() {
        if (!AudioOk) { Log.Warn("[AUDIO] 测试音未播放：未找到 " + cfg.cableRenderName); return; }
        const int sr = 16000, seg = 320;
        for (int off = 0; off < sr; off += seg) {
            var buf = new short[seg];
            for (int i = 0; i < seg; i++) {
                int t = off + i;
                double env = Math.Min(1.0, Math.Min(t / 800.0, (sr - t) / 1600.0));
                buf[i] = (short)(11000 * env * Math.Sin(2 * Math.PI * 440 * t / sr));
            }
            audioOut.Enqueue(buf);
        }
        Log.Info("[AUDIO] 已发送 1 秒测试音到 " + audioOut.DeviceUsed);
    }

    /// Re-apply config changes that can take effect at runtime.
    public void ApplyConfig(Config updated) {
        var next = new HotkeyInjector(updated.hotkey.keys, updated.hotkey.mode);
        if (talking && hotkeyHeld) pendingInjector = next;   // the held combo must be released by the injector that pressed it
        else injector = next;
        InputRouter.SetBlockF5(updated.blockF5);
        InputRouter.SetKeyMap(updated.keymap, updated.deviceMacPrefix);
        Log.Info("[CFG] 热键: " + next.Describe() + " | 拦截F5: " + (updated.blockF5 ? "开" : "关") +
                 " | 按键映射: " + (updated.keymap.enabled ? "开" : "关"));
    }

    void KeyWorkerLoop() {
        foreach (var act in keyQueue.GetConsumingEnumerable()) {
            try {
                if (act.Kind == KeyActionKind.VoiceDown) {
                    if (cfg.switchDefaultMic && AudioOk && switcher.TargetFound) {
                        switcher.SwitchToTarget();
                        if (cfg.switchLeadMs > 0) Thread.Sleep(cfg.switchLeadMs);
                    }
                    // The held voice key floods F5 repeats (~33 ms) that reach
                    // the IME's own hook ahead of our swallow and break its
                    // "trigger key is the LAST key pressed" rule. Re-install
                    // the F5 shield right before the combo so it is the newest
                    // hook (first in chain) at that moment.
                    if (cfg.hotkeyEnabled) {
                        InputRouter.ReArmF5Shield();
                        injector.OnVoiceDown();
                    }
                } else {
                    if (cfg.hotkeyEnabled) injector.OnVoiceUp();
                    switcher.Restore();
                    if (pendingInjector != null) {             // release was sent with the OLD combo; now swap
                        injector = pendingInjector;
                        pendingInjector = null;
                    }
                }
            } catch (Exception ex) { Log.Error("[KEY] worker: " + ex.Message); }
        }
    }

    void AudioLoop() {
        foreach (byte[] frame in audioQueue.GetConsumingEnumerable()) {
            try {
                var blocks = decoder.Feed(frame);
                if (blocks != null) {
                    for (int i = 0; i < blocks.Count; i++) {
                        audioOut.Enqueue(blocks[i]);
                        framesDecoded++;
                        if (dumpBuffer != null) dumpBuffer.AddRange(blocks[i]);
                        UpdateLevel(blocks[i]);
                    }
                }
            } catch (Exception ex) { Log.Error("[DECODE] " + ex.Message); }
        }
    }

    static void UpdateLevel(short[] block) {
        if (block == null || block.Length == 0) return;
        int peak = 0;
        for (int i = 0; i < block.Length; i++) {
            int v = block[i] < 0 ? -block[i] : block[i];
            if (v > peak) peak = v;
        }
        UiState.SetLevel(peak / 32768.0);
    }

    // ======================= BleVoiceLink.IHandler =======================

    public void OnLinkState(bool connected, string detail) {
        InputRouter.SetLinked(connected);
        if (connected) {
            Log.Info("[LINK] 已连接: " + detail);
            TryLateAttachAudio("link");      // VB-CABLE / audio stack may have come up since startup
        } else {
            Log.Info("[LINK] " + detail);
        }
        UiState.SetStatus(connected, detail, link.RemoteName);
        TrayIcon.SetStatus(connected, detail);
    }

    /// Retry opening the cable if it was missing at startup (throttled to 1/30s).
    void TryLateAttachAudio(string reason) {
        if (AudioOk && switcher.TargetFound) return;
        if ((DateTime.Now - lastAudioRetry).TotalSeconds < 30) return;
        lastAudioRetry = DateTime.Now;
        if (!AudioOk) {
            AudioOk = audioOut.Start(cfg.cableRenderName, 16000);
            if (AudioOk) Log.Info("[AUDIO] late-attach OK (" + reason + "): " + audioOut.DeviceUsed);
        }
        if (!switcher.TargetFound && switcher.FindTarget(cfg.cableCaptureName))
            Log.Info("[AUDIO] late-attach capture OK (" + reason + ")");
    }

    public void OnCaps(int version, int frameSize, int codec) {
        decoder.FrameSize = frameSize;
    }

    public void OnVoiceStart(byte sessionId, byte interaction) {
        TryLateAttachAudio("voice");         // first real use: make sure the cable is there
        talking = true;
        framesDecoded = 0;
        talkStart = DateTime.Now;
        decoder.ResetSession();
        byte[] stale;
        while (audioQueue.TryTake(out stale)) { }         // drain stale audio
        if (cfg.dumpAudio) dumpBuffer = new List<short>(16000 * 2);
        // 0x03 = HTT (voice key physically held); other reasons are firmware-initiated
        // sessions (e.g. right after MIC_OPEN) and must NOT hold the IME hotkey.
        hotkeyHeld = interaction == 0x03;
        // firmware-initiated sessions carry no user audio and no matching AUDIO_STOP,
        // so only a real key press may light up the talking UI (else it sticks on)
        UiState.SetTalking(hotkeyHeld);
        Log.Voice(">>> 语音会话开始 (session " + sessionId + ", interaction " + interaction +
                  (hotkeyHeld ? ", 语音键按下" : ", 固件自启(不注入热键)") + ")");
        if (hotkeyHeld)
            keyQueue.Add(new KeyAction { Kind = KeyActionKind.VoiceDown });   // mic switch runs even with injection disabled
    }

    public void OnVoiceStop() {
        if (!talking) return;
        talking = false;
        double secs = (DateTime.Now - talkStart).TotalSeconds;
        Log.Voice("<<< 松开语音键 (" + framesDecoded + " 帧, " + secs.ToString("0.0") + "s)");
        UiState.SetTalking(false);
        if (framesDecoded > 0) {             // sessions with no decoded audio (firmware self-start) don't count
            lock (cfg) {
                cfg.stats.AddSession(secs);
                cfg.Save();                  // persist usage counters after each session
            }
        }
        if (dumpBuffer != null && dumpBuffer.Count > 0) {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "dump_" + DateTime.Now.ToString("HHmmss") + ".wav");
            try { WavWriter.Write(path, dumpBuffer, 16000); Log.Info("[DUMP] " + path); }
            catch (Exception ex) { Log.Warn("[DUMP] " + ex.Message); }
        }
        dumpBuffer = null;
        if (hotkeyHeld)
            keyQueue.Add(new KeyAction { Kind = KeyActionKind.VoiceUp });
        hotkeyHeld = false;
    }

    public void OnSync(int predictor, int stepIndex) {
        decoder.ApplySync(predictor, stepIndex);
    }

    public void OnAudioFrame(byte[] frame) {
        if (!talking) return;
        if (!audioQueue.TryAdd(frame)) { /* queue full: drop, streamer keeps up from buffered frames */ }
    }

    public void OnBattery(int percent) {
        Log.Info("[BAT] 遥控器电量: " + percent + "%");
        UiState.SetBattery(percent);
        TrayIcon.SetBattery(percent);
    }

    public void OnCharging(int chargeState) {
        UiState.SetCharging(chargeState);
        TrayIcon.SetCharging(chargeState);
    }
}
