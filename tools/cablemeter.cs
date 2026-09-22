// cablemeter.cs - diagnostic: poll the peak meter of a render endpoint (the
// cable's "CABLE Input") to prove whether the running MiVoiceMic is actually
// pushing audio during a real remote press. Prints a line whenever the peak
// rises above silence, so a session shows up as a burst of lines.
//   cablemeter <durationMs> [nameContains]
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class CableMeter {
    enum ERole : uint { eConsole = 0 }
    enum EDataFlow { eRender = 0 }
    enum EDeviceState : uint { ACTIVE = 1 }
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
        void Activate(ref Guid iid, uint clsctx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        void OpenPropertyStore(uint stgm, out IPropertyStore ps);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out EDeviceState st);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        void GetCount(out uint n); void GetAt(uint i, out PROPERTYKEY k);
        void GetValue([In] ref PROPERTYKEY k, IntPtr pv); int SetValue(); void Commit();
    }
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation {
        float GetPeakValue();
        int GetMeteringChannelCount();
    }
    [DllImport("ole32.dll")] static extern int PropVariantClear(IntPtr pv);
    static readonly Guid PKEY_Device_FriendlyName = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IID_IAudioMeterInformation = new Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    const uint CLSCTX_ALL = 0x17;

    static int Main(string[] args) {
        int ms = args.Length > 0 ? int.Parse(args[0]) : 10000;
        string match = args.Length > 1 ? args[1] : "CABLE Input";
        var box = new Holder();
        var t = new Thread((ThreadStart)delegate {
            try {
                var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
                IMMDevice dev = null;
                IMMDeviceCollection c; e.EnumAudioEndpoints(EDataFlow.eRender, EDeviceState.ACTIVE, out c);
                uint n; c.GetCount(out n);
                for (uint i = 0; i < n; i++) {
                    IMMDevice d; c.Item(i, out d);
                    IPropertyStore ps; d.OpenPropertyStore(0, out ps);
                    var key = new PROPERTYKEY { fmtid = PKEY_Device_FriendlyName, pid = 2 };
                    IntPtr pv = Marshal.AllocCoTaskMem(40);
                    for (int o = 0; o < 40; o++) Marshal.WriteByte(pv, o, 0);
                    string name = null;
                    try { ps.GetValue(ref key, pv); if (Marshal.ReadInt16(pv) == 31) name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8)); }
                    finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
                    if (name != null && name.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0) { dev = d; box.Name = name; break; }
                }
                if (dev == null) { box.Err = "no render endpoint matches \"" + match + "\""; return; }
                object o2; Guid iid = IID_IAudioMeterInformation;
                dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o2);
                box.Meter = (IAudioMeterInformation)o2;
            } catch (Exception ex) { box.Err = ex.Message; }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        if (box.Meter == null) { Console.WriteLine("meter init failed: " + box.Err); return 1; }
        Console.WriteLine("metering render endpoint: " + box.Name);
        for (int el = 0; el < ms; el += 100) {
            float p = box.Meter.GetPeakValue();
            if (p > 0.001f) Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " peak=" + p.ToString("F3"));
            Thread.Sleep(100);
        }
        Console.WriteLine("done");
        return 0;
    }
    class Holder { public IAudioMeterInformation Meter; public string Name; public string Err; }
}
