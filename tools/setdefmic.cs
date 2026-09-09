// setdefmic.cs - one-shot default capture device restorer (diagnostic cleanup
// companion for e2etail.cs). Lists capture endpoints, then sets the default to
// the first one whose name does NOT look like the virtual cable.
//   setdefmic            list only
//   setdefmic <name>     set default to endpoint containing <name>
// 中文：默认录音设备一键恢复 —— 诊断工具用完把默认麦克风还回真实设备
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

static class SetDefMic {
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

    class Ep { public string Id, Name; }
    static List<Ep> List() {
        var r = new List<Ep>();
        var t = new Thread((ThreadStart)delegate {
            try {
                var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
                IMMDeviceCollection c; e.EnumAudioEndpoints(EDataFlow.eCapture, EDeviceState.ACTIVE, out c);
                uint n; c.GetCount(out n);
                for (uint i = 0; i < n; i++) {
                    IMMDevice d; c.Item(i, out d);
                    var ep = new Ep(); d.GetId(out ep.Id);
                    IPropertyStore ps; d.OpenPropertyStore(0, out ps);
                    var key = new PROPERTYKEY { fmtid = PKEY_Device_FriendlyName, pid = 2 };
                    IntPtr pv = Marshal.AllocCoTaskMem(40);
                    for (int o = 0; o < 40; o++) Marshal.WriteByte(pv, o, 0);
                    try {
                        ps.GetValue(ref key, pv);
                        if (Marshal.ReadInt16(pv) == 31) ep.Name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
                    } finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
                    r.Add(ep);
                }
            } catch (Exception ex) { Console.WriteLine("enum failed: " + ex.Message); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        return r;
    }
    static string Current() {
        string name = null;
        var t = new Thread((ThreadStart)delegate {
            try {
                var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
                IMMDevice d; e.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out d);
                IPropertyStore ps; d.OpenPropertyStore(0, out ps);
                var key = new PROPERTYKEY { fmtid = PKEY_Device_FriendlyName, pid = 2 };
                IntPtr pv = Marshal.AllocCoTaskMem(40);
                for (int o = 0; o < 40; o++) Marshal.WriteByte(pv, o, 0);
                try {
                    ps.GetValue(ref key, pv);
                    if (Marshal.ReadInt16(pv) == 31) name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
                } finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
            } catch (Exception ex) { Console.WriteLine("current failed: " + ex.Message); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
        return name;
    }

    static int Main(string[] args) {
        var eps = List();
        for (int i = 0; i < eps.Count; i++)
            Console.WriteLine("[" + i + "] " + eps[i].Name + "  id=" + eps[i].Id);
        Console.WriteLine("current default: " + Current());
        if (args.Length == 0) return 0;
        string want = args[0];
        for (int i = 0; i < eps.Count; i++) {
            if (eps[i].Name != null && eps[i].Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) {
                int idx = i;
                var t = new Thread((ThreadStart)delegate {
                    var p = (IPolicyConfig)new CPolicyConfigClient();
                    p.SetDefaultEndpoint(eps[idx].Id, ERole.eConsole);
                    p.SetDefaultEndpoint(eps[idx].Id, ERole.eMultimedia);
                    p.SetDefaultEndpoint(eps[idx].Id, ERole.eCommunications);
                }) { IsBackground = true };
                t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
                Console.WriteLine("set default -> [" + idx + "] " + eps[idx].Name);
                Console.WriteLine("now default: " + Current());
                return 0;
            }
        }
        Console.WriteLine("no endpoint matches \"" + want + "\"");
        return 1;
    }
}
