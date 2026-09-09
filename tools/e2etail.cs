// e2etail.cs - diagnostic: bisect WHY real remote voice sessions land in WeType's
// panel-preview mode (whole-text insert, no end punctuation) while keyboard
// sessions stream live into the text field. Pushes audio (TTS or a dump_*.wav
// replay) through the exact runtime pipeline (CABLE Input -> default-mic switch
// -> Ctrl+Win hold) with each suspect as a switch:
//
//   e2etail <offsetMs> [--wav <file>] [--agcsim] [--pre] [--pump [jitterMs]] [sentence...]
//     offsetMs < 0 : release that many ms BEFORE the audio finishes AND cut
//                    playback - emulates the remote path where AUDIO_STOP fires
//                    at button release and the buffered tail never arrives.
//     offsetMs > 0 : hold the combo that many ms after the audio ends (drain).
//     --wav <file> : replay a dump_*.wav instead of TTS (16k/16bit/mono).
//     --agcsim     : apply ADPCM-sized noise floor + the runtime AGC to TTS.
//     --pre        : start audio 300 ms BEFORE the hotkey (cable already live
//                    when the panel opens, like the real path).
//     --pump [ms]  : deliver in 15 ms buffers on a real-time drip through a
//                    10-buffer waveOut pump with Sleep(3) starvation polling -
//                    replicating AudioOut.Pump - instead of one big write.
//                    Optional arrival jitter in ms (BLE connection-event bursts).
// 中文：语音热键上屏模式对照实验 —— TTS/dump 回放走真实管线，逐项复刻遥控路径的可疑差异
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;

static class E2eTail {
    // ---- waveOut to a named render device ----
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
    struct WAVEFORMATEX { public ushort tag; public ushort ch; public uint sr; public uint avg; public ushort blk; public ushort bits; public uint cb; }

    const int HDR_SIZE = 48, OFF_FLAGS = 24, WHDR_DONE = 1;

    // ---- SendInput combo (same semantics as HotkeyInjector) ----
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    const int INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_SCANCODE = 0x0008;
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT keyboard; }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }

    static bool IsExtended(ushort vk) {
        return vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 || vk == 0x25 || vk == 0x26 || vk == 0x27 ||
               vk == 0x28 || vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C || vk == 0x5D || vk == 0xA3 || vk == 0xA5;
    }
    static INPUT Make(ushort vk, bool down) {
        uint flags = KEYEVENTF_SCANCODE;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;
        return new INPUT { type = INPUT_KEYBOARD, keyboard = new KEYBDINPUT { vk = vk, scan = (ushort)MapVirtualKey(vk, 0), flags = flags } };
    }

    // runtime combo order after NormalizeForInject([LCTRL,LWIN]) = [LWIN,LCTRL]
    static readonly ushort[] Combo = { 0x5B, 0xA2 };

    static void ComboDown() {
        foreach (ushort vk in Combo)   // clean slate, same as OnVoiceDown
            keybd_event((byte)vk, (byte)MapVirtualKey(vk, 0), KEYEVENTF_KEYUP | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0), UIntPtr.Zero);
        Thread.Sleep(50);
        var inputs = new INPUT[Combo.Length];
        for (int i = 0; i < Combo.Length; i++) inputs[i] = Make(Combo[i], true);
        Console.WriteLine("t={0,5}ms combo DOWN x{1}", Env(), SendInput((uint)inputs.Length, inputs, 40));
        Console.Out.Flush();
    }
    static void ComboUp() {
        var inputs = new INPUT[Combo.Length];
        for (int i = 0; i < Combo.Length; i++) inputs[i] = Make(Combo[Combo.Length - 1 - i], false);
        Console.WriteLine("t={0,5}ms combo UP x{1}", Env(), SendInput((uint)inputs.Length, inputs, 40));
        Console.Out.Flush();
    }

    static Stopwatch sw = Stopwatch.StartNew();
    static long Env() { return sw.ElapsedMilliseconds; }

    // ---- audio degradation switch: replicate the runtime decode chain ----
    static void AgcSim(short[] pcm) {
        var rnd = new Random(1234);
        for (int i = 0; i < pcm.Length; i++) {
            int v = pcm[i] + rnd.Next(-150, 150);
            if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
            pcm[i] = (short)v;
        }
        double agcPeak = 1000;
        for (int i = 0; i < pcm.Length; i++) {
            double v = pcm[i];
            double a = v < 0 ? -v : v;
            if (a > agcPeak) agcPeak = a; else agcPeak *= 0.9997;
            double g = Math.Min(30, 28000 / Math.Max(agcPeak, 200));
            v *= g;
            if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
            pcm[i] = (short)v;
        }
    }

    // ---- default capture switch (minimal copy of DeviceSwitcher) ----
    enum ERole : uint { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    enum EDataFlow { eRender = 0, eCapture = 1 }
    [Flags] enum EDeviceState : uint { ACTIVE = 1 }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
        void EnumAudioEndpoints(EDataFlow f, EDeviceState s, out IMMDeviceCollection c);
        void GetDefaultAudioEndpoint(EDataFlow f, ERole r, out IMMDevice d);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice d);
        void R1(IntPtr p); void R2(IntPtr p);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection { void GetCount(out uint n); void Item(uint i, out IMMDevice d); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
        int Activate();
        void OpenPropertyStore(uint stgm, out IPropertyStore ps);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out EDeviceState st);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        void GetCount(out uint n);
        void GetAt(uint i, out PROPERTYKEY k);
        void GetValue([In] ref PROPERTYKEY k, IntPtr pv);
        int SetValue();
        void Commit();
    }
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] internal class CPolicyConfigClient { }
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig {
        int M0(); int M1(); int M2(); int M3(); int M4(); int M5(); int M6(); int M7(); int M8(); int M9();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole r);
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(IntPtr pv);
    static readonly Guid PKEY_Device_FriendlyName = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

    static void RunSta(Action a) {
        Exception err = null;
        var t = new Thread((ThreadStart)delegate { try { a(); } catch (Exception e) { err = e; } }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (err != null) throw err;
    }
    static string ReadName(IPropertyStore ps) {
        var key = new PROPERTYKEY { fmtid = PKEY_Device_FriendlyName, pid = 2 };
        IntPtr pv = Marshal.AllocCoTaskMem(40);
        for (int i = 0; i < 40; i++) Marshal.WriteByte(pv, i, 0);
        try {
            ps.GetValue(ref key, pv);
            short vt = Marshal.ReadInt16(pv);
            if (vt == 31) return Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
            return null;
        } finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
    }
    static string CurrentDefaultCaptureId() {
        string id = null;
        RunSta(delegate {
            var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
            IMMDevice d; e.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out d);
            d.GetId(out id);
        });
        return id;
    }
    static string FindCaptureId(string contains) {
        string found = null;
        RunSta(delegate {
            var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
            IMMDeviceCollection c; e.EnumAudioEndpoints(EDataFlow.eCapture, EDeviceState.ACTIVE, out c);
            uint n; c.GetCount(out n);
            for (uint i = 0; i < n; i++) {
                IMMDevice d; c.Item(i, out d);
                IPropertyStore ps; d.OpenPropertyStore(0, out ps);
                string nm = ReadName(ps);
                if (nm != null && nm.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) { d.GetId(out found); return; }
            }
        });
        return found;
    }
    static void SetDefaultCapture(string id) {
        RunSta(delegate {
            var p = (IPolicyConfig)new CPolicyConfigClient();
            p.SetDefaultEndpoint(id, ERole.eConsole);
            p.SetDefaultEndpoint(id, ERole.eMultimedia);
            p.SetDefaultEndpoint(id, ERole.eCommunications);
        });
    }

    // ---- TTS / wav ----
    static short[] Synthesize(string text) {
        string wav = Path.Combine(Path.GetTempPath(), "e2etail.wav");
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

    // ---- pump delivery: 15 ms chunks on a real-time drip through a 10-buffer
    // waveOut ring with Sleep(3) starvation polling - an AudioOut.Pump replica.
    // Stops early (leaving the tail unqueued) when the release deadline passes. ----
    static void PumpDelivery(IntPtr h, IntPtr[] ph, IntPtr[] pd, short[] pcm, int releaseAt, int jitterMs) {
        const int CHUNK = 240;
        int total = (pcm.Length + CHUNK - 1) / CHUNK;
        var used = new bool[ph.Length];
        var rnd = new Random(42);
        var t0 = Stopwatch.StartNew();
        for (int i = 0; i < total; i++) {
            double at = i * 15.0;
            if (jitterMs > 0) at += (rnd.NextDouble() * 2.0 - 1.0) * jitterMs;
            while (t0.ElapsedMilliseconds < at) Thread.Sleep(1);
            if (t0.ElapsedMilliseconds >= releaseAt) { Console.WriteLine("pump: deadline at chunk {0}/{1}", i, total); break; }
            int free = -1;
            while (free < 0) {
                for (int b = 0; b < ph.Length; b++) {
                    if (used[b] && (Marshal.ReadInt32(ph[b], OFF_FLAGS) & WHDR_DONE) != 0) used[b] = false;
                    if (!used[b]) { free = b; break; }
                }
                if (free < 0) Thread.Sleep(3);
            }
            int cnt = Math.Min(CHUNK, pcm.Length - i * CHUNK);
            Marshal.Copy(pcm, i * CHUNK, pd[free], cnt);
            Marshal.WriteInt32(ph[free], 8, cnt * 2);
            waveOutWrite(h, ph[free], HDR_SIZE);
            used[free] = true;
        }
        while (t0.ElapsedMilliseconds < releaseAt) Thread.Sleep(2);   // hold through the drain window
    }

    static string dBUnit() { return "dBFS"; }

    static int Main(string[] args) {
        if (args.Length < 1) {
            Console.WriteLine("usage: e2etail <offsetMs> [--wav <file>] [--agcsim] [--pre] [--pump [jitterMs]] [sentence...]");
            return 2;
        }
        int offset = int.Parse(args[0]);
        bool agcSim = Array.IndexOf(args, "--agcsim") >= 0;
        bool pre = Array.IndexOf(args, "--pre") >= 0;   // audio BEFORE the hotkey (cable already live when the panel opens)
        bool pump = Array.IndexOf(args, "--pump") >= 0; // 15 ms drip through a 10-buffer ring (AudioOut replica)
        int jitterMs = 0;
        for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--jitter") jitterMs = int.Parse(args[i + 1]);
        string wavPath = null;
        for (int i = 1; i < args.Length - 1; i++) if (args[i] == "--wav") wavPath = args[i + 1];
        int textStart = 1;
        while (textStart < args.Length && (args[textStart].StartsWith("--") || args[textStart - 1] == "--wav" || args[textStart - 1] == "--jitter")) textStart++;
        string text = args.Length > textStart ? string.Join(" ", args, textStart, args.Length - textStart)
                                              : "你觉得明天会不会下雨呢";
        string cableRender = "CABLE Input", cableCapture = "CABLE Output";

        // 1. render device index
        uint devId = uint.MaxValue;
        for (uint i = 0, n = waveOutGetNumDevs(); i < n; i++) {
            var c = new Caps();
            waveOutGetDevCaps(i, ref c, Marshal.SizeOf(typeof(Caps)));
            if (c.name != null && c.name.IndexOf(cableRender, StringComparison.OrdinalIgnoreCase) >= 0) devId = i;
        }
        if (devId == uint.MaxValue) { Console.WriteLine("FAIL: no " + cableRender); return 1; }

        // 2. capture switch target
        string cableId = FindCaptureId(cableCapture);
        if (cableId == null) { Console.WriteLine("FAIL: no " + cableCapture); return 1; }
        string saved = CurrentDefaultCaptureId();   // endpoint ID, not name (name is display-only)

        // 3. audio source
        short[] pcm;
        if (wavPath != null) {
            pcm = ReadWav(wavPath);
            double peak = 0, sum = 0;
            for (int i = 0; i < pcm.Length; i++) {
                double a = pcm[i] < 0 ? -(double)pcm[i] : pcm[i];
                if (a > peak) peak = a;
                sum += (double)pcm[i] * pcm[i];
            }
            double rms = Math.Sqrt(sum / Math.Max(1, pcm.Length));
            Console.WriteLine("[1] WAV {0}s peak={1} ({2}{3}) rms={4} ({5}{6})",
                (pcm.Length / 16000.0).ToString("0.0"), (int)peak,
                (20 * Math.Log10(Math.Max(1, peak) / 32767.0)).ToString("0.0"), dBUnit(),
                (int)rms, (20 * Math.Log10(Math.Max(1, rms) / 32767.0)).ToString("0.0"), dBUnit());
        } else {
            pcm = Synthesize(text);
            if (agcSim) { AgcSim(pcm); Console.WriteLine("[1b] AGC-sim applied (noise floor + runtime AGC)"); }
        }
        byte[] raw = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, raw, 0, raw.Length);
        int durMs = raw.Length / 32;
        Console.WriteLine("[1] source ready: {0}ms \"{1}\"", durMs, wavPath ?? text);

        // 4. runtime order: switch mic, lead 120ms
        SetDefaultCapture(cableId);
        Thread.Sleep(120);
        Console.WriteLine("[2] default capture -> cable");

        // 5. open device + prepare buffers
        var wfx = new WAVEFORMATEX { tag = 1, ch = 1, sr = 16000, avg = 32000, blk = 2, bits = 16, cb = 0 };
        IntPtr h;
        int hr = waveOutOpen(out h, devId, ref wfx, IntPtr.Zero, IntPtr.Zero, 0);
        if (hr != 0) { SetDefaultCapture(saved); Console.WriteLine("FAIL: waveOutOpen " + hr); return 1; }

        IntPtr hdr = IntPtr.Zero, data = IntPtr.Zero;
        IntPtr[] ph = null, pd = null;
        if (!pump) {
            hdr = Marshal.AllocHGlobal(HDR_SIZE);
            data = Marshal.AllocHGlobal(raw.Length);
            Marshal.Copy(raw, 0, data, raw.Length);
            for (int i = 0; i < HDR_SIZE; i++) Marshal.WriteByte(hdr, i, 0);
            Marshal.WriteIntPtr(hdr, 0, data);
            Marshal.WriteInt32(hdr, 8, raw.Length);
            waveOutPrepareHeader(h, hdr, HDR_SIZE);
            waveOutWrite(h, hdr, HDR_SIZE);
        } else {
            const int NB = 10, CHUNK = 240;
            ph = new IntPtr[NB]; pd = new IntPtr[NB];
            for (int i = 0; i < NB; i++) {
                pd[i] = Marshal.AllocHGlobal(CHUNK * 2);
                ph[i] = Marshal.AllocHGlobal(HDR_SIZE);
                for (int o = 0; o < HDR_SIZE; o++) Marshal.WriteByte(ph[i], o, 0);
                Marshal.WriteIntPtr(ph[i], 0, pd[i]);
                Marshal.WriteInt32(ph[i], 8, CHUNK * 2);
                waveOutPrepareHeader(h, ph[i], HDR_SIZE);
            }
        }
        Console.WriteLine("[3] t={0}ms playing {1}ms of audio ({2}{3})",
            Env(), durMs, pump ? "pump 15ms drip" : "one-shot", pump ? " jitter=" + jitterMs + "ms" : "");

        // 6. combo + release timing - variables under test
        int releaseAt = durMs + offset + (pre ? 300 : 0);
        if (!pre && releaseAt < 500) releaseAt = 500;      // sanity: never release during combo settle
        bool cut = offset < 0;
        if (pre) Thread.Sleep(300);
        ComboDown();
        if (pump) {
            PumpDelivery(h, ph, pd, pcm, releaseAt, jitterMs);
        } else {
            Thread.Sleep(Math.Max(0, releaseAt - (int)Env()));
        }
        if (cut) waveOutReset(h);                  // remote path: tail never arrives
        ComboUp();
        SetDefaultCapture(saved);
        Console.WriteLine("[4] t={0}ms released (offset={1}ms, tail {2})", Env(), offset, cut ? "CUT" : "played out");

        // 7. give the IME time to finalize + insert, then clean up
        Thread.Sleep(3000);
        waveOutReset(h);
        if (!pump) {
            waveOutUnprepareHeader(h, hdr, HDR_SIZE);
            Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data);
        } else {
            for (int i = 0; i < ph.Length; i++) {
                waveOutUnprepareHeader(h, ph[i], HDR_SIZE);
                Marshal.FreeHGlobal(ph[i]); Marshal.FreeHGlobal(pd[i]);
            }
        }
        waveOutClose(h);
        Console.WriteLine("PASS: offset {0}ms pump={1} wav={2}. Check the focused field for mode + punctuation.",
            offset, pump, wavPath != null ? wavPath : "-");
        return 0;
    }
}
