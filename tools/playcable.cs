// playcable.cs - diagnostic: play a 16 kHz mono WAV file into the render side
// of the virtual audio cable ("CABLE Input"), the same waveOut path AudioOut
// uses. Companion for the no-remote full-chain test (setdefmic + holdkey +
// playcable + a focused text box = what the remote path does end to end).
//   playcable <file.wav> [leadInMs]
using System;
using System.IO;
using System.Runtime.InteropServices;

class PlayCable {
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

    static int Main(string[] args) {
        if (args.Length < 1) { Console.WriteLine("usage: playcable <file.wav> [leadInMs]"); return 2; }
        int lead = args.Length > 1 ? int.Parse(args[1]) : 0;

        // read wav (16-bit PCM mono expected; data chunk hunted like E2E.ReadWav)
        byte[] b = File.ReadAllBytes(args[0]);
        int off = 12; byte[] raw = null;
        while (off + 8 < b.Length) {
            string id = System.Text.Encoding.ASCII.GetString(b, off, 4);
            int len = b[off + 4] | (b[off + 5] << 8) | (b[off + 6] << 16) | (b[off + 7] << 24);
            if (id == "data") { raw = new byte[len]; Buffer.BlockCopy(b, off + 8, raw, 0, len); break; }
            off += 8 + len + (len & 1);
        }
        if (raw == null) { Console.WriteLine("no data chunk"); return 1; }

        uint n = waveOutGetNumDevs();
        uint idx = 0xFFFFFFFF;
        for (uint i = 0; i < n; i++) {
            var c = new Caps();
            waveOutGetDevCaps(i, ref c, Marshal.SizeOf(c));
            if (c.name != null && c.name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) idx = i;
        }
        if (idx == 0xFFFFFFFF) { Console.WriteLine("CABLE Input not found"); return 1; }

        var wfx = new WAVEFORMATEX { tag = 1, ch = 1, sr = 16000, avg = 32000, blk = 2, bits = 16 };
        IntPtr hWave;
        int hr = waveOutOpen(out hWave, idx, ref wfx, IntPtr.Zero, IntPtr.Zero, 0);
        if (hr != 0) { Console.WriteLine("waveOutOpen err=" + hr); return 1; }

        if (lead > 0) System.Threading.Thread.Sleep(lead);

        IntPtr hdr = Marshal.AllocHGlobal(48);
        IntPtr data = Marshal.AllocHGlobal(raw.Length);
        Marshal.Copy(raw, 0, data, raw.Length);
        for (int i = 0; i < 48; i++) Marshal.WriteByte(hdr, i, 0);
        Marshal.WriteIntPtr(hdr, 0, data);
        Marshal.WriteInt32(hdr, 8, raw.Length);
        waveOutPrepareHeader(hWave, hdr, 48);
        waveOutWrite(hWave, hdr, 48);
        Console.WriteLine("playing " + raw.Length + " bytes (" + raw.Length / 32000.0 + "s) err=" + Marshal.GetLastWin32Error());
        int ms = (int)(raw.Length / 32.0) + 500;
        System.Threading.Thread.Sleep(ms);
        waveOutReset(hWave);
        waveOutUnprepareHeader(hWave, hdr, 48);
        waveOutClose(hWave);
        Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data);
        Console.WriteLine("done");
        return 0;
    }
}
