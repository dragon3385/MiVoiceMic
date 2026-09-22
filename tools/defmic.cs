// defmic.cs - print the current default capture endpoint's exact ID (and
// name), or set the default by exact endpoint ID. setdefmic matches by NAME,
// which is ambiguous when two endpoints share a friendly name (two 麦克风 on
// this machine) - this tool restores by ID so a test can always undo itself.
//   defmic            print current default (id + name)
//   defmic <id>       set default capture to this endpoint id (all 3 roles)
using System;
using System.Runtime.InteropServices;
using System.Threading;

static class DefMic {
    enum ERole : uint { eConsole = 0, eMultimedia = 1, eCommunications = 2 }
    enum EDataFlow { eRender = 0, eCapture = 1 }
    enum EDeviceState : uint { ACTIVE = 1 }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
        void EnumAudioEndpoints(IntPtr f, IntPtr s, IntPtr c);   // vtable slot, unused
        void GetDefaultAudioEndpoint(EDataFlow f, ERole r, out IMMDevice d);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice d);
        void R1(IntPtr p); void R2(IntPtr p);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
        int Activate();
        void OpenPropertyStore(uint stgm, out IPropertyStore ps);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out EDeviceState st);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        void GetCount(out uint n); void GetAt(uint i, out PROPERTYKEY k);
        void GetValue([In] ref PROPERTYKEY k, IntPtr pv); int SetValue(); void Commit();
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

    static string NameOf(IMMDevice d) {
        IPropertyStore ps; d.OpenPropertyStore(0, out ps);
        var key = new PROPERTYKEY { fmtid = PKEY_Device_FriendlyName, pid = 2 };
        IntPtr pv = Marshal.AllocCoTaskMem(40);
        for (int o = 0; o < 40; o++) Marshal.WriteByte(pv, o, 0);
        try {
            ps.GetValue(ref key, pv);
            if (Marshal.ReadInt16(pv) == 31) return Marshal.PtrToStringUni(Marshal.ReadIntPtr(pv, 8));
        } finally { PropVariantClear(pv); Marshal.FreeCoTaskMem(pv); }
        return "?";
    }

    static void Run(Action body) {
        var t = new Thread((ThreadStart)delegate { body(); }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
    }

    static int Main(string[] args) {
        if (args.Length == 0) {
            Run(delegate {
                var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator));
                IMMDevice d; e.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eConsole, out d);
                string id; d.GetId(out id);
                Console.WriteLine(id + "  " + NameOf(d));
            });
            return 0;
        }
        string want = args[0];
        Run(delegate {
            var p = (IPolicyConfig)new CPolicyConfigClient();
            p.SetDefaultEndpoint(want, ERole.eConsole);
            p.SetDefaultEndpoint(want, ERole.eMultimedia);
            p.SetDefaultEndpoint(want, ERole.eCommunications);
        });
        Console.WriteLine("set -> " + want);
        return 0;
    }
}
