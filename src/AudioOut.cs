// AudioOut.cs - waveOut streamer: pushes decoded 16 kHz mono PCM into the
// render side of the virtual audio cable (default: "CABLE Input" from VB-CABLE),
// plus a small WAV writer for session dumps.
//
// Self-heal (real-device post-mortem 2026-09-22): a run that had been up for
// ~20 h silently stopped rendering - frames were decoded and enqueued, but
// nothing came out of the cable, so the IME's voice panel heard pure silence
// while the keyboard path (physical mic) still worked. An invalidated waveOut
// handle does not reliably fail waveOutWrite; it just never sets WHDR_DONE on
// its headers, so the pump spun forever with zero log output. The pump now
// (a) writes a 15 ms silence probe every 15 s while idle, (b) treats "a write
// was issued (or failed) but no header completed for STALL_MS" as a dead
// device, and (c) closes and reopens the endpoint (retrying every few seconds
// until it succeeds), logging each reopen. RenderHealth holds the pure
// decision so SelfTest can drive it with a fake clock.
// 中文：waveOut 推流器 —— 把解码 PCM 写入虚拟声卡播放端；句柄静默失效时自愈重开
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

// Pure stall-decision logic for the render pump (unit-testable: time is a
// parameter, not the wall clock). "Expecting" = at least one waveOutWrite has
// been issued and no WHDR_DONE has been observed since.
sealed class RenderHealth {
    int expectSince;
    bool expecting;
    bool forced;

    public void OnWrite(int nowMs) { if (!expecting) { expecting = true; expectSince = nowMs; } }
    /// waveOutWrite returned an error: the device is dead now, not after the
    /// timeout - force the stall verdict until a completion clears it.
    public void OnWriteFailed() { forced = true; }
    public void OnProgress() { expecting = false; forced = false; }
    public bool Stalled(int nowMs, int stallMs) { return forced || (expecting && nowMs - expectSince > stallMs); }
}

sealed class AudioOut {
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int waveOutGetDevCaps(uint id, ref WAVEOUTCAPS pwoc, int cbwoc);
    [DllImport("winmm.dll")]
    static extern int waveOutOpen(out IntPtr phwi, uint id, ref WAVEFORMATEX pwfx, IntPtr cb, IntPtr inst, uint fdo);
    [DllImport("winmm.dll")]
    static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")]
    static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")]
    static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")]
    static extern uint waveOutGetNumDevs();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WAVEOUTCAPS {
        public ushort wMid, wPid; public uint v;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
        public uint df; public ushort ch, r1; public uint sup;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort tag; public ushort ch; public uint sr; public uint avg; public ushort blk; public ushort bits; public ushort cb; }

    const int NB = 10;                 // queued buffers (~150 ms at 15 ms/frame)
    const int HDR_SIZE = 48;           // WAVEHDR on x64
    const int OFF_FLAGS = 24;
    const int WHDR_DONE = 1;
    const int SAMPLES_PER_FRAME = 240; // 120 bytes ADPCM = 240 samples
    const int STALL_MS = 800;          // no completion this long after a write -> dead device
    const int PROBE_EVERY_MS = 15000;  // idle liveness probe interval
    const int REOPEN_RETRY_MS = 5000;  // failed reopen: wait this long before retrying

    IntPtr hWave = IntPtr.Zero;
    readonly IntPtr[] hdr = new IntPtr[NB];
    readonly IntPtr[] data = new IntPtr[NB];
    readonly bool[] used = new bool[NB];
    readonly Queue<short[]> q = new Queue<short[]>();
    readonly object qlock = new object();
    volatile bool running;
    Thread thr;
    string devMatch;                   // reopen search key (config name, e.g. "CABLE Input")
    uint rate;
    readonly RenderHealth health = new RenderHealth();
    int lastProbe;
    int lastReopenFail;
    int reopens;
    int probes;

    public string DeviceUsed { get; private set; }
    public long FramesPlayed { get; private set; }
    public long FramesDropped { get; private set; }
    public int Reopens { get { return reopens; } }
    public int Probes { get { return probes; } }

    // headers/buffers live for the object lifetime (reused across reopens)
    public AudioOut() {
        for (int i = 0; i < NB; i++) {
            data[i] = Marshal.AllocHGlobal(SAMPLES_PER_FRAME * 2);
            hdr[i] = Marshal.AllocHGlobal(HDR_SIZE);
        }
    }

    /// List render device names (for diagnostics).
    public static List<string> ListRenderDevices() {
        var names = new List<string>();
        uint n = waveOutGetNumDevs();
        for (uint i = 0; i < n; i++) {
            var c = new WAVEOUTCAPS();
            if (waveOutGetDevCaps(i, ref c, Marshal.SizeOf(c)) == 0 && c.name != null)
                names.Add(c.name);
        }
        return names;
    }

    public bool Start(string nameContains, int sr) {
        if (running) return true;
        devMatch = nameContains;
        rate = (uint)sr;
        if (!OpenDevice()) return false;
        running = true;
        thr = new Thread(Pump) { IsBackground = true, Name = "wavout" };
        thr.Start();
        return true;
    }

    bool OpenDevice() {
        uint n = waveOutGetNumDevs();
        uint idx = 0xFFFFFFFF; bool found = false;
        for (uint i = 0; i < n; i++) {
            var c = new WAVEOUTCAPS();
            waveOutGetDevCaps(i, ref c, Marshal.SizeOf(c));
            if (c.name != null && c.name.IndexOf(devMatch, StringComparison.OrdinalIgnoreCase) >= 0) {
                idx = i; found = true; DeviceUsed = c.name;
            }
        }
        if (!found) return false;

        var wfx = new WAVEFORMATEX { tag = 1, ch = 1, sr = rate, avg = rate * 2, blk = 2, bits = 16, cb = 0 };
        int hr = waveOutOpen(out hWave, idx, ref wfx, IntPtr.Zero, IntPtr.Zero, 0);
        if (hr != 0) { hWave = IntPtr.Zero; return false; }
        for (int i = 0; i < NB; i++) ResetHeader(i);
        return true;
    }

    /// Zero the WAVEHDR, re-point it at its buffer and prepare it against the
    /// CURRENT handle (headers are prepared once per open/reopen).
    void ResetHeader(int i) {
        for (int o = 0; o < HDR_SIZE; o++) Marshal.WriteByte(hdr[i], o, 0);
        Marshal.WriteIntPtr(hdr[i], 0, data[i]);
        Marshal.WriteInt32(hdr[i], 8, SAMPLES_PER_FRAME * 2);
        used[i] = false;
        waveOutPrepareHeader(hWave, hdr[i], HDR_SIZE);
    }

    public void Enqueue(short[] samples) {
        lock (qlock) {
            q.Enqueue(samples);
            if (q.Count > 40) { q.Dequeue(); FramesDropped++; }  // drop oldest if backed up (>600 ms)
        }
    }

    public void Stop() {
        running = false;
        if (thr != null) thr.Join(1500);   // a reopen in flight needs the settle sleep
        CloseDevice();
    }

    void CloseDevice() {
        if (hWave == IntPtr.Zero) return;
        waveOutReset(hWave);
        for (int i = 0; i < NB; i++) {
            if (hdr[i] != IntPtr.Zero) {
                waveOutUnprepareHeader(hWave, hdr[i], HDR_SIZE);   // API contract: unprepare before freeing
                Marshal.FreeHGlobal(hdr[i]); Marshal.FreeHGlobal(data[i]); hdr[i] = IntPtr.Zero;
            }
        }
        waveOutClose(hWave);
        hWave = IntPtr.Zero;
    }

    /// Close and reopen the endpoint (runs on the pump thread). Headers keep
    /// their allocations. A failed reopen leaves hWave zeroed; the caller
    /// retries every REOPEN_RETRY_MS until it succeeds.
    bool ReopenDevice() {
        if (hWave != IntPtr.Zero) {
            waveOutReset(hWave);
            for (int i = 0; i < NB; i++) waveOutUnprepareHeader(hWave, hdr[i], HDR_SIZE);
            waveOutClose(hWave);
            hWave = IntPtr.Zero;
        }
        for (int i = 0; i < NB; i++) used[i] = false;
        Thread.Sleep(150);          // let the audio stack settle before reopening
        if (!OpenDevice()) {
            Log.Warn("[AUDIO] render 重开失败: 未找到 \"" + devMatch + "\"（" + (REOPEN_RETRY_MS / 1000) + " 秒后重试）");
            return false;
        }
        reopens++;
        Log.Warn("[AUDIO] render 推流停滞，已重开设备 \"" + DeviceUsed + "\"（第 " + reopens + " 次）");
        health.OnProgress();        // fresh handle: drop the stale stall verdict
        lastProbe = Environment.TickCount;
        return true;
    }

    void Pump() {
        while (running) {
            int now = Environment.TickCount;

            // dead-device watchdog: a write went out (or failed) and nothing completed
            if (health.Stalled(now, STALL_MS)) {
                if (lastReopenFail == 0 || now - lastReopenFail > REOPEN_RETRY_MS) {
                    if (!ReopenDevice()) lastReopenFail = now;
                } else {
                    Thread.Sleep(200);   // reopen retry backoff
                }
                continue;
            }

            int freeIdx = -1;
            for (int i = 0; i < NB; i++) {
                if (used[i] && (Marshal.ReadInt32(hdr[i], OFF_FLAGS) & WHDR_DONE) != 0) {
                    used[i] = false;
                    health.OnProgress();
                }
                if (!used[i]) { freeIdx = i; break; }
            }
            short[] samples = null;
            if (freeIdx >= 0) lock (qlock) { if (q.Count > 0) samples = q.Dequeue(); }
            if (freeIdx >= 0 && samples != null) {
                int cnt = Math.Min(samples.Length, SAMPLES_PER_FRAME);
                Marshal.Copy(samples, 0, data[freeIdx], cnt);
                Marshal.WriteInt32(hdr[freeIdx], 8, cnt * 2);
                if (waveOutWrite(hWave, hdr[freeIdx], HDR_SIZE) == 0) {
                    used[freeIdx] = true;
                    health.OnWrite(now);
                    FramesPlayed++;
                } else {
                    health.OnWriteFailed();   // failed write: dead device, reopen now
                }
            } else {
                // idle liveness probe: a 15 ms silence write every PROBE_EVERY_MS
                // keeps an expectation pending, so a handle that died BETWEEN
                // sessions is detected (and reopened) before the next press
                if (freeIdx >= 0 && hWave != IntPtr.Zero && now - lastProbe > PROBE_EVERY_MS) {
                    lastProbe = now;
                    for (int o = 0; o < SAMPLES_PER_FRAME * 2; o++) Marshal.WriteByte(data[freeIdx], o, 0);
                    Marshal.WriteInt32(hdr[freeIdx], 8, SAMPLES_PER_FRAME * 2);
                    if (waveOutWrite(hWave, hdr[freeIdx], HDR_SIZE) == 0) {
                        used[freeIdx] = true;
                        health.OnWrite(now);
                        probes++;
                    } else {
                        health.OnWriteFailed();
                    }
                }
                Thread.Sleep(3);
            }
        }
    }
}

static class WavWriter {
    public static void Write(string path, List<short> pcm, int sr) {
        int ds = pcm.Count * 2;
        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs)) {
            var a = System.Text.Encoding.ASCII;
            w.Write(a.GetBytes("RIFF")); w.Write(36 + ds); w.Write(a.GetBytes("WAVE"));
            w.Write(a.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(sr); w.Write(sr * 2); w.Write((short)2); w.Write((short)16);
            w.Write(a.GetBytes("data")); w.Write(ds);
            byte[] b = new byte[ds];
            System.Buffer.BlockCopy(pcm.ToArray(), 0, b, 0, ds);
            w.Write(b);
        }
    }
}
