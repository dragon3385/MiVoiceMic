// AudioOut.cs - waveOut streamer: pushes decoded 16 kHz mono PCM into the
// render side of the virtual audio cable (default: "CABLE Input" from VB-CABLE),
// plus a small WAV writer for session dumps.
// 中文：waveOut 推流器 —— 把解码 PCM 写入虚拟声卡播放端；附 WAV 落盘工具
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

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

    IntPtr hWave = IntPtr.Zero;
    readonly IntPtr[] hdr = new IntPtr[NB];
    readonly IntPtr[] data = new IntPtr[NB];
    readonly bool[] used = new bool[NB];
    readonly Queue<short[]> q = new Queue<short[]>();
    readonly object qlock = new object();
    volatile bool running;
    Thread thr;

    public string DeviceUsed { get; private set; }
    public long FramesPlayed { get; private set; }
    public long FramesDropped { get; private set; }

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
        uint n = waveOutGetNumDevs();
        uint idx = 0xFFFFFFFF; bool found = false;
        for (uint i = 0; i < n; i++) {
            var c = new WAVEOUTCAPS();
            waveOutGetDevCaps(i, ref c, Marshal.SizeOf(c));
            if (c.name != null && c.name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0) {
                idx = i; found = true; DeviceUsed = c.name;
            }
        }
        if (!found) return false;

        var wfx = new WAVEFORMATEX { tag = 1, ch = 1, sr = (uint)sr, avg = (uint)(sr * 2), blk = 2, bits = 16, cb = 0 };
        int hr = waveOutOpen(out hWave, idx, ref wfx, IntPtr.Zero, IntPtr.Zero, 0);
        if (hr != 0) { hWave = IntPtr.Zero; return false; }

        for (int i = 0; i < NB; i++) {
            data[i] = Marshal.AllocHGlobal(SAMPLES_PER_FRAME * 2);
            hdr[i] = Marshal.AllocHGlobal(HDR_SIZE);
            for (int o = 0; o < HDR_SIZE; o++) Marshal.WriteByte(hdr[i], o, 0);
            Marshal.WriteIntPtr(hdr[i], 0, data[i]);
            Marshal.WriteInt32(hdr[i], 8, SAMPLES_PER_FRAME * 2);
            waveOutPrepareHeader(hWave, hdr[i], HDR_SIZE);
        }
        running = true;
        thr = new Thread(Pump) { IsBackground = true, Name = "wavout" };
        thr.Start();
        return true;
    }

    public void Enqueue(short[] samples) {
        lock (qlock) {
            q.Enqueue(samples);
            if (q.Count > 40) { q.Dequeue(); FramesDropped++; }  // drop oldest if backed up (>600 ms)
        }
    }

    public void Stop() {
        running = false;
        if (thr != null) thr.Join(500);
        if (hWave != IntPtr.Zero) {
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
    }

    void Pump() {
        while (running) {
            int freeIdx = -1;
            for (int i = 0; i < NB; i++) {
                if (used[i] && (Marshal.ReadInt32(hdr[i], OFF_FLAGS) & WHDR_DONE) != 0)
                    used[i] = false;
                if (!used[i]) { freeIdx = i; break; }
            }
            short[] samples = null;
            if (freeIdx >= 0) lock (qlock) { if (q.Count > 0) samples = q.Dequeue(); }
            if (freeIdx >= 0 && samples != null) {
                int cnt = Math.Min(samples.Length, SAMPLES_PER_FRAME);
                Marshal.Copy(samples, 0, data[freeIdx], cnt);
                Marshal.WriteInt32(hdr[freeIdx], 8, cnt * 2);
                waveOutWrite(hWave, hdr[freeIdx], HDR_SIZE);
                used[freeIdx] = true;
                FramesPlayed++;
            } else {
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
