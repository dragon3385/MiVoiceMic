// holdkey.cs - diagnostic: replicate HotkeyInjector hold semantics exactly
// (scancode injection, extended-key flag, clean-slate keyup + 50ms, reverse-order
// release) so the injected Ctrl+Win can be tested WITHOUT the remote.
//   Usage: holdkey <holdMs> <vkHex> [<vkHex> ...]   e.g. holdkey 2500 A2 5B
using System;
using System.Runtime.InteropServices;
using System.Threading;

class HoldKey {
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
    static int Main(string[] args) {
        if (args.Length < 2) { Console.WriteLine("usage: holdkey <holdMs> <vkHex> [...]"); return 2; }
        int ms = int.Parse(args[0]);
        var keys = new ushort[args.Length - 1];
        for (int i = 0; i < keys.Length; i++) keys[i] = Convert.ToUInt16(args[i + 1], 16);
        foreach (ushort vk in keys)   // clean slate, same as OnVoiceDown
            keybd_event((byte)vk, (byte)MapVirtualKey(vk, 0), KEYEVENTF_KEYUP | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0), UIntPtr.Zero);
        Thread.Sleep(50);
        var down = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) down[i] = Make(keys[i], true);
        Console.WriteLine("t=0 down=" + SendInput((uint)down.Length, down, 40) + "/" + down.Length + " hold=" + ms + "ms");
        Console.Out.Flush();
        Thread.Sleep(ms);
        var up = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) up[i] = Make(keys[keys.Length - 1 - i], false);
        Console.WriteLine("t=" + ms + " up=" + SendInput((uint)up.Length, up, 40) + "/" + up.Length);
        return 0;
    }
}
