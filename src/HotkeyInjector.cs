// HotkeyInjector.cs - SendInput-based combo hold/release/tap for the IME voice
// hotkey (WeType default: Ctrl+Win), plus key-name parsing.
//
// Two proven subtleties (from RemoteMapper NOTES.md):
//  1. Right Alt is an EXTENDED key - must set KEYEVENTF_EXTENDEDKEY or the IME
//     sees Left Alt and triggers its own behaviour.
//  2. (Historical) Injections used to unhook the LL hook and reinstall it via a
//     posted message afterwards. Real-device logs (2026-09-07) showed both
//     failure modes this creates: the rehook message eaten during a concurrent
//     injection window left input unhooked until the next voice session, and an
//     immediate same-burst reinstall could leave the hook registered but
//     receiving NO events (mapped keys passed as raw arrows for seconds). The
//     hook is now simply NEVER removed: HookCb ignores injected keys via
//     LLKHF_INJECTED, so our own SendInput can never loop through it.
//  3. Combo ORDER matters for WeType 2.1.3.18: its Ctrl+Win voice hotkey only
//     completes when Ctrl is the LAST key pressed (Win held first). Config
//     order [LCTRL,LWIN] produced a perfect-looking but dead injection - the
//     voice panel never opened (real-device test 2026-09-08). Combo is now
//     normalized Win-first via KeyMapNames.NormalizeForInject.
// 中文：SendInput 组合键注入 —— 输入法语音热键的按住/点按控制
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

sealed class HotkeyInjector {
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    const int INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;
    const uint MAPVK_VK_TO_VSC = 0;

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }

    readonly ushort[] combo;      // e.g. { RAlt, Comma } - modifiers first, trigger last
    readonly bool tapMode;        // true: tap combo on press and again on release

    public HotkeyInjector(IList<string> keyNames, string mode) {
        combo = KeyMapNames.NormalizeForInject(KeyMapNames.ParseList(keyNames));
        tapMode = mode != null && mode.Equals("tap", StringComparison.OrdinalIgnoreCase);
    }

    public string Describe() {
        return KeyMapNames.FriendlyCombo(combo) + (tapMode ? "（点按 x2）" : "（按住）");
    }

    static bool IsExtended(ushort vk) {
        return vk == 0x21 || vk == 0x22 || vk == 0x23 || vk == 0x24 ||
               vk == 0x25 || vk == 0x26 || vk == 0x27 || vk == 0x28 ||
               vk == 0x2D || vk == 0x2E || vk == 0x5B || vk == 0x5C ||
               vk == 0x5D || vk == 0xA3 || vk == 0xA5;
    }

    static INPUT Make(ushort vk, bool down) {
        uint flags = KEYEVENTF_SCANCODE;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;
        return new INPUT {
            type = INPUT_KEYBOARD,
            keyboard = new KEYBDINPUT { vk = vk, scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC), flags = flags }
        };
    }

    static bool PressAll(ushort[] keys) {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Make(keys[i], true);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) == inputs.Length;
    }
    static bool ReleaseAll(ushort[] keys) {
        var inputs = new INPUT[keys.Length];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Make(keys[keys.Length - 1 - i], false);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) == inputs.Length;
    }
    static bool TapOnce(ushort[] keys) {
        var inputs = new INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Make(keys[i], true);
        for (int i = 0; i < keys.Length; i++) inputs[keys.Length + i] = Make(keys[keys.Length - 1 - i], false);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))) == (uint)inputs.Length;
    }

    static void ForcedRelease(ushort vk) {
        uint flags = KEYEVENTF_KEYUP | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
        keybd_event((byte)vk, (byte)MapVirtualKey(vk, MAPVK_VK_TO_VSC), flags, UIntPtr.Zero);
    }

    /// Voice key pressed: default-mic already switched by caller.
    public void OnVoiceDown() {
        foreach (ushort vk in combo) ForcedRelease(vk);   // clean slate
        ForcedRelease(0x74);   // F5-up: the remote's voice key sends F5 to Windows and the
                               // shield cannot guarantee the IME never saw its down (the IME
                               // re-installs its hook ahead of ours). Without a matching up
                               // the IME sees F5 held forever and its voice trigger - which
                               // requires no other non-modifier key held - never completes.
                               // Harmless when the IME never saw the down (lone keyup).
        System.Threading.Thread.Sleep(50);
        if (tapMode) {
            if (!TapOnce(combo)) Log.Warn("[KEY] tap down failed");
        } else {
            if (!PressAll(combo)) Log.Warn("[KEY] press failed");
        }
    }

    /// Voice key released.
    public void OnVoiceUp() {
        if (tapMode) {
            if (!TapOnce(combo)) Log.Warn("[KEY] tap up failed");
        } else {
            if (!ReleaseAll(combo)) Log.Warn("[KEY] release failed");
        }
    }

    /// Safety net: release held keys (on exit / link loss).
    public void ForceRelease() {
        try {
            foreach (ushort vk in combo) ForcedRelease(vk);
        } catch (Exception ex) { Log.Warn("[KEY] force release: " + ex.Message); }
    }
}
