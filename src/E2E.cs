// E2E.cs - end-to-end link test WITHOUT the remote: a synthesized "voice" is
// pushed through the exact runtime pipeline (TTS -> CABLE Input -> default-mic
// switch -> optional IME hotkey injection), so remote-side and app-side problems
// can be told apart during bring-up.
//
//   MiVoiceMic.exe --e2e              audio path only (safe to run unattended)
//   MiVoiceMic.exe --e2e --inject     also hold the IME voice hotkey while playing
//                                     (first click a text field; needs WeType etc.)
//   MiVoiceMic.exe --e2e --text "..."; custom TTS sentence
// 中文：端到端链路自测（无需遥控器）—— 合成语音走完整运行管线
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;

static class E2E {
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int waveOutGetDevCaps(uint id, ref Caps pwoc, int cb);
    [DllImport("winmm.dll")]
    static extern int waveOutOpen(out IntPtr h, uint id, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, uint fdo);
    [DllImport("winmm.dll")]
    static extern int waveOutPrepareHeader(IntPtr h, IntPtr wh, int cb);
    [DllImport("winmm.dll")]
    static extern int waveOutWrite(IntPtr h, IntPtr wh, int cb);
    [DllImport("winmm.dll")]
    static extern int waveOutUnprepareHeader(IntPtr h, IntPtr wh, int cb);
    [DllImport("winmm.dll")]
    static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")]
    static extern int waveOutClose(IntPtr h);
    [DllImport("winmm.dll")]
    static extern uint waveOutGetNumDevs();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Caps { public ushort a, b; public uint c; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name; public uint df; public ushort ch, r1; public uint sup; }
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort tag; public ushort ch; public uint sr; public uint avg; public ushort blk; public ushort bits; public ushort cb; }

    static int Main2(string[] args) {
        bool inject = HasFlag(args, "--inject");
        string text = ValueOf(args, "--text");
        if (text == null) text = "你好，这是无线麦链路测试，如果你能看到这段文字被识别，说明音频链路正常。";
        var cfg = Config.Load();

        Console.WriteLine("== e2e 链路自测 (遥控器无关) ==");

        // 1. endpoints
        var sw = new DeviceSwitcher();
        if (!sw.FindTarget(cfg.cableCaptureName)) {
            Console.WriteLine("FAIL: 未找到录音设备 \"" + cfg.cableCaptureName + "\" (VB-CABLE 未安装或音频栈异常)");
            return 1;
        }
        // 2. voice source: TTS (zh-CN if available), else 1 kHz beep fallback
        short[] pcm;
        try {
            pcm = Synthesize(text);
            Console.WriteLine("[1] TTS " + pcm.Length / 16000.0 + "s: \"" + (text.Length > 24 ? text.Substring(0, 24) + "..." : text) + "\"");
        } catch (Exception ex) {
            Console.WriteLine("[1] TTS 不可用 (" + ex.Message + ")，改用 2 秒提示音");
            pcm = Beep(2000, 1000);
        }

        // 3. switch default mic -> cable
        string before = DeviceSwitcher.CurrentDefaultCaptureName();
        sw.SwitchToTarget();
        string during = DeviceSwitcher.CurrentDefaultCaptureName();
        bool switched = during != null && during != before;
        Console.WriteLine("[2] 默认麦克风: \"" + before + "\" -> \"" + during + "\" " + (switched ? "OK" : "(未变化!)"));
        if (!switched) { sw.Restore(); Console.WriteLine("FAIL: 默认麦克风切换未生效"); return 1; }

        // 4. open CABLE Input + play
        var wfx = new WAVEFORMATEX { tag = 1, ch = 1, sr = 16000, avg = 32000, blk = 2, bits = 16 };
        IntPtr hWave;
        int hr = waveOutOpen(out hWave, cableIdxFound, ref wfx, IntPtr.Zero, IntPtr.Zero, 0);
        if (hr != 0) { sw.Restore(); Console.WriteLine("FAIL: 打开 " + cfg.cableRenderName + " 失败 err=" + hr); return 1; }
        var injector = inject ? new HotkeyInjector(cfg.hotkey.keys, cfg.hotkey.mode) : null;
        if (injector != null) { Console.WriteLine("[3] 注入语音热键 " + injector.Describe()); injector.OnVoiceDown(); }

        byte[] raw = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, raw, 0, raw.Length);
        IntPtr hdr = Marshal.AllocHGlobal(48);
        IntPtr data = Marshal.AllocHGlobal(raw.Length);
        Marshal.Copy(raw, 0, data, raw.Length);
        for (int i = 0; i < 48; i++) Marshal.WriteByte(hdr, i, 0);
        Marshal.WriteIntPtr(hdr, 0, data);
        Marshal.WriteInt32(hdr, 8, raw.Length);
        waveOutPrepareHeader(hWave, hdr, 48);
        Console.WriteLine("[4] 推流 " + raw.Length + " 字节到 " + cfg.cableRenderName + " ...");
        waveOutWrite(hWave, hdr, 48);

        // 5. wait for playback, then release + restore
        int ms = (int)(raw.Length / 32.0) + 500;
        Thread.Sleep(ms);
        if (injector != null) injector.OnVoiceUp();
        waveOutReset(hWave);
        waveOutUnprepareHeader(hWave, hdr, 48);
        waveOutClose(hWave);
        Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data);
        sw.Restore();
        Console.WriteLine("[5] 已恢复默认麦克风: \"" + DeviceSwitcher.CurrentDefaultCaptureName() + "\"");

        Console.WriteLine();
        Console.WriteLine("PASS: 音频已送入 CABLE。验证方法:");
        if (inject)
            Console.WriteLine("  --inject 模式: 目标输入框里应出现上面那句话的识别结果 (需微信输入法运行中)");
        else
            Console.WriteLine("  重开 CMD 运行: recorder / micstrip / 设置->隐私->麦克风 观察电平；或加 --inject 真注入热键");
        return 0;
    }

    static uint cableIdxFound;

    static short[] Synthesize(string text) {
        string wav = Path.Combine(Path.GetTempPath(), "mivoicemic_e2e.wav");
        try {
            using (var tts = new SpeechSynthesizer()) {
                bool zh = false;
                foreach (InstalledVoice v in tts.GetInstalledVoices())
                    if (v.VoiceInfo.Culture != null && v.VoiceInfo.Culture.Name.StartsWith("zh")) { zh = true; break; }
                if (!zh) throw new Exception("无中文语音包");
                tts.SelectVoiceByHints(VoiceGender.NotSet, VoiceAge.NotSet, 0, new System.Globalization.CultureInfo("zh-CN"));
                tts.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                tts.Speak(text);
            }
            return ReadWav(wav);
        } finally { try { File.Delete(wav); } catch { } }
    }

    static short[] ReadWav(string path) {
        byte[] b = File.ReadAllBytes(path);
        // find "data" chunk
        int off = 12;
        while (off + 8 < b.Length) {
            string id = System.Text.Encoding.ASCII.GetString(b, off, 4);
            int len = b[off + 4] | (b[off + 5] << 8) | (b[off + 6] << 16) | (b[off + 7] << 24);
            if (id == "data") {
                var s = new short[len / 2];
                Buffer.BlockCopy(b, off + 8, s, 0, len);
                return s;
            }
            off += 8 + len + (len & 1);
        }
        throw new Exception("wav data chunk not found");
    }

    static short[] Beep(int ms, int freq) {
        int n = 16000 * ms / 1000;
        var s = new short[n];
        for (int i = 0; i < n; i++) s[i] = (short)(Math.Sin(2 * Math.PI * freq * i / 16000.0) * 8000);
        return s;
    }

    static bool HasFlag(string[] args, string f) {
        foreach (string a in args) if (string.Equals(a, f, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    static string ValueOf(string[] args, string k) {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], k, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    public static int Run(string[] args) {
        // find cable render index first (stored in cableIdxFound for waveOutOpen)
        var cfg = Config.Load();
        uint n = waveOutGetNumDevs();
        bool found = false;
        for (uint i = 0; i < n; i++) {
            var c = new Caps();
            waveOutGetDevCaps(i, ref c, Marshal.SizeOf(c));
            if (c.name != null && c.name.IndexOf(cfg.cableRenderName, StringComparison.OrdinalIgnoreCase) >= 0) {
                cableIdxFound = i; found = true;
            }
        }
        if (!found) {
            Console.WriteLine("FAIL: 未找到播放设备 \"" + cfg.cableRenderName + "\" (waveOut 设备数 " + n + ")");
            Console.WriteLine("      安装 VB-CABLE (setup\\install-vbcable.cmd) 后重试；若刚装完请检查音频服务");
            return 1;
        }
        return Main2(args);
    }
}
