// RemoteKeys.cs - driverless key remapping for the remote's Windows-visible keys
// (voice=F5, arrows, OK/Enter). Replaces VoiceKeyBlocker as the single
// WH_KEYBOARD_LL owner.
//
// Attribution: the low-level hook cannot see which device a key came from, but
// Raw Input can. A hidden sink window (own thread, RIDEV_INPUTSINK) records
// device-evidence clocks (remote vs physical, see RawSink). Swallowed keys
// produce no WM_INPUT of their own, so per-event correlation of a swallowed
// key is impossible - the hook decides from which device spoke more recently
// (a swallowed remote key latches the clock via NoteRemoteActivity, a physical
// keystroke always passes and refreshes its clock). With NO fresh evidence the
// DOWN is swallowed and deferred attribution kicks in: the UP is passed as a
// harmless orphan keyup, its own WM_INPUT names the device, and the worker
// either fires the mapped action (remote) or replays the original key
// (physical). No ghost keys, no misattributed physical presses.
//
// Gestures: click / long-press per key, actions = combo (tap or hold-through),
// task view, launch app, shell command. Executed on a dedicated worker thread,
// never inside the hook callback.
// 中文：输入路由器 —— Raw Input 设备归因 + WH_KEYBOARD_LL 拦截 + 点按/长按手势引擎
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

static class InputRouter {
    // ---- hook -------------------------------------------------------------
    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int px, py; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr extra; }

    const int WH_KEYBOARD_LL = 13;
    const uint WM_APP_REHOOK = 0x8000;
    const uint LLKHF_INJECTED = 0x10;
    const ushort VK_F5 = 0x74;
    const ushort VK_F20 = 0x83;           // the bundled filter driver's voice-key spelling

    /// The bundled filter driver remaps the remote's voice key F5 -> F20 (so it
    /// can never collide with the physical F5, see driver/remap.c). Both
    /// spellings must behave identically in the hook and in raw-input
    /// attribution, so F20 is normalized to F5 before any decision. A physical
    /// F20 keyboard is vanishingly rare and gets the same treatment (the same
    /// tradeoff as the physical F5 today).
    internal static uint NormalizeVoiceKey(uint vk) { return vk == VK_F20 ? VK_F5 : vk; }

    static readonly HookProc proc = HookCb;
    static volatile IntPtr hhk = IntPtr.Zero;
    static Thread pump;
    static uint pumpTid;
    static volatile bool blockF5;         // swallow remote/physical F5 while linked
    static volatile bool linked;          // BLE link connected
    public static long SwallowedCount;
    static int lastPassLogTick;           // hook thread only: throttle passthrough notes
    static int lastTraceLogTick;          // hook thread only: throttle the live-state trace

    /// Throttled (1/s) trace of every real (non-injected) key event with the
    /// hook's live decision state - proves the callback runs at all and shows
    /// WHY a key passed (linked/blockF5/mapping state, installed handle).
    static void HookTrace(KBDLLHOOKSTRUCT k, bool down) {
        try {
            uint vk = NormalizeVoiceKey(k.vkCode);
            int t = Environment.TickCount;
            if (t - lastTraceLogTick > 1000 || t < lastTraceLogTick) {
                lastTraceLogTick = t;
                bool mapCase = linked && mappingEnabled && engine.HasBinding(vk);
                Log.Info("[INPUT] hook trace vk=0x" + vk.ToString("X2") + (down ? " down" : " up") +
                         " linked=" + (linked ? 1 : 0) + " blockF5=" + (blockF5 ? 1 : 0) +
                         " map=" + (mappingEnabled ? 1 : 0) + " bind=" + (mapCase ? 1 : 0) +
                         " flags=0x" + k.flags.ToString("X") + " hhk=0x" + hhk.ToString("X"));
            }
        } catch { }
    }

    // ---- mapping state ----------------------------------------------------
    static readonly object mapGate = new object();
    static volatile bool mappingEnabled;
    static GestureEngine engine = new GestureEngine(new KeyMapEntry[0]);
    static Thread mapWorker;
    static readonly BlockingCollection<GestureEngine.RawEvent> mapQueue =
        new BlockingCollection<GestureEngine.RawEvent>();

    /// UI callback (worker thread): (keyId, description) when a mapped action fires.
    public static event Action<string, string> ActionFired;

    static void FireAction(string id, string desc) {
        var h = ActionFired;
        if (h != null) { try { h(id, desc); } catch { } }
    }

    // ---- remote key lit state (UI feedback) ----------------------------------
    // vk -> keymap entry id, rebuilt with every mapping table swap. Fed from
    // device-attributed remote events (raw sink WM_INPUT + hook swallow
    // decisions) so the UI art can light the pressed key. Physical keyboards
    // and injected keys never reach these feed points. Read-only after build,
    // swapped by reference - safe to read from hook/sink threads without locks.
    static volatile Dictionary<ushort, string> vkToId = new Dictionary<ushort, string>();

    /// The given vk was just seen from the REMOTE: light its key on the art
    /// (down / device repeat) or turn it off (release).
    public static void NoteRemoteKey(uint vk, bool down) {
        string id;
        var t = vkToId;
        if (t.TryGetValue((ushort)vk, out id)) UiState.NoteRemoteKey(id, down);
    }

    // ---- deferred attribution ----------------------------------------------
    // A mapped key with no fresh device evidence must NOT be passed through
    // (a remote first press would ghost into the app) and cannot be attributed
    // as a swallowed key (swallowed keys generate no WM_INPUT). So: swallow the
    // DOWN silently, and decide from events we CAN pass:
    //   - the OS auto-repeat (held key, after the repeat delay): the FIRST
    //     repeat is passed as the probe, REWRITTEN to VK_F24 (0x87) so nothing
    //     a remote press does can ever ghost a real arrow into the app; its
    //     WM_INPUT names the device. Physical -> all further repeats flow as
    //     real keys, so hold-to-repeat works; remote -> stop passing, arm the
    //     gesture engine with the backdated press (clicks fire once, hold
    //     gestures fire at their threshold) and swallow further repeats.
    //   - the UP: an orphan keyup is always harmless to pass, and only a
    //     passed event reaches the raw-input sink. A click (no repeat seen)
    //     fires the mapped action (remote) or replays the original key
    //     (physical) on release.
    // Per-event truth, no guessing, no leaked keys.
    sealed class PendingPress { public long DownTick; public bool Probing; }              // hook thread only
    internal sealed class PendingEvent { public ushort Vk; public ushort RealVk; public long HookTick; public long DownTick; public bool Probe; public bool Probing; }
    static readonly Dictionary<uint, PendingPress> pending = new Dictionary<uint, PendingPress>();  // hook thread only
    static readonly Dictionary<uint, int> probeVerdict = new Dictionary<uint, int>();     // worker writes, hook reads
    static readonly Dictionary<uint, bool> armedPresses = new Dictionary<uint, bool>();   // worker only: engine holds the real press
    static readonly List<PendingEvent> pendingEvents = new List<PendingEvent>();          // hook writes, worker drains
    static readonly object pendingGate = new object();
    const ushort PROBE_VK = 0x87;                         // VK_F24: unbound, ignored by apps
    const uint LLKHF_EXTENDED = 0x01;

    static IntPtr HookCb(int nCode, IntPtr wParam, IntPtr lParam) {
        try {
            if (nCode >= 0) {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if ((k.flags & LLKHF_INJECTED) == 0)                       // never touch injected input
                    HookTrace(k, wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104);
            }
            if (nCode >= 0 && linked) {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if ((k.flags & LLKHF_INJECTED) == 0) {          // never touch injected input
                    bool down = wParam == (IntPtr)0x0100 || wParam == (IntPtr)0x0104;
                    uint vk = NormalizeVoiceKey(k.vkCode);      // driver voice key (F20) == F5
                    bool f5Case = vk == VK_F5 && blockF5;        // legacy blocker semantics
                    bool mapCase = false;
                    if (mappingEnabled) lock (mapGate) mapCase = engine.HasBinding(vk);
                    if (mapCase && !f5Case && pending.Count > 0) {
                        PendingPress pp;
                        pending.TryGetValue(vk, out pp);
                        if (pp != null) {
                            if (down) {
                                if (!pp.Probing) {
                                    pp.Probing = true;           // first auto-repeat becomes the device probe,
                                    KBDLLHOOKSTRUCT probe = k;   // rewritten so it can never ghost a real key
                                    probe.vkCode = PROBE_VK;
                                    probe.scanCode = KeySender.ScanOf(PROBE_VK);
                                    probe.flags &= ~LLKHF_EXTENDED;
                                    Marshal.StructureToPtr(probe, lParam, false);
                                    lock (pendingGate) pendingEvents.Add(new PendingEvent {
                                        Vk = PROBE_VK, RealVk = (ushort)vk, HookTick = NowMs(), DownTick = pp.DownTick, Probe = true
                                    });
                                } else {
                                    int v; bool known;
                                    lock (pendingGate) known = probeVerdict.TryGetValue(vk, out v);
                                    if (!known) return (IntPtr)1;             // verdict not in yet: swallow
                                    if (v == 1) {                             // remote: swallow, keep the art lit while held
                                        NoteRemoteKey(vk, true);
                                        return (IntPtr)1;
                                    }
                                }
                                return CallNextHookEx(hhk, nCode, wParam, lParam); // physical: repeats flow as real keys
                            }
                            // UP: pass in every case - the app saw the probe
                            // repeats (or nothing at all) and must see the release
                            bool probing = pp.Probing;
                            pending.Remove(vk);
                            lock (pendingGate) pendingEvents.Add(new PendingEvent {
                                Vk = (ushort)vk, RealVk = (ushort)vk, HookTick = NowMs(), Probe = false, Probing = probing
                            });
                            return CallNextHookEx(hhk, nCode, wParam, lParam);
                        }
                    }
                    if (f5Case || mapCase) {
                        bool remote = true;
                        if (mapCase) remote = RawSink.LooksFromRemote();
                        if (remote) {
                            Interlocked.Increment(ref SwallowedCount);
                            if (mapCase) {
                                RawSink.NoteRemoteActivity();    // swallowed keys produce no WM_INPUT - latch the burst
                                NoteRemoteKey(vk, down);         // swallowed = the sink will never see it: light here, dark on up
                                mapQueue.TryAdd(new GestureEngine.RawEvent { Vk = (ushort)vk, Down = down, TickMs = NowMs() });
                            }
                            return (IntPtr)1;                    // swallowed
                        }
                        if (mapCase && down) {
                            // ambiguous DOWN: swallow silently; auto-repeats will
                            // probe the device, the UP settles the rest
                            pending[vk] = new PendingPress { DownTick = NowMs() };
                            int td = Environment.TickCount;
                            if (td - lastPassLogTick > 2000 || td < lastPassLogTick) {
                                lastPassLogTick = td;
                                Log.Info("[INPUT] 0x" + vk.ToString("X2") + " 按下 无近期证据 → 延迟归因（重复键探测/松开判定） 证据=" + RawSink.LastProbe);
                            }
                            return (IntPtr)1;
                        }
                        if (f5Case) {                            // physical F5 while linked: still swallowed (old behavior)
                            Interlocked.Increment(ref SwallowedCount);
                            return (IntPtr)1;
                        }
                        if (mapCase && !down) {                  // evidence flipped mid-press: still let the engine finish its state
                            mapQueue.TryAdd(new GestureEngine.RawEvent { Vk = (ushort)vk, Down = false, TickMs = NowMs() });
                        }
                        int t = Environment.TickCount;           // rare stray event: throttled note
                        if (t - lastPassLogTick > 2000 || t < lastPassLogTick) {
                            lastPassLogTick = t;
                            Log.Info("[INPUT] 0x" + vk.ToString("X2") + (down ? " 按下" : " 松开") +
                                     " 已映射但最近物理键盘更活跃 → 放行" +
                                     (mapCase ? " 证据=" + RawSink.LastProbe : ""));
                        }
                    }
                }
            }
        } catch { }
        return CallNextHookEx(hhk, nCode, wParam, lParam);
    }

    /// Map-worker side: resolve probes against the raw-input ring (per-event
    /// truth), then settle releases. A remote probe verdict arms the gesture
    /// engine with the backdated press so hold gestures fire at their threshold
    /// (and clicks fire once, ~immediately) - the release then only completes
    /// the engine state, so a configured click can never leak on top of a hold.
    /// Physical click releases (no probe seen) replay the original key.
    static void ResolvePendingEvents() {
        List<PendingEvent> remoteClicks = null;
        List<ushort> physicalTaps = null;
        List<PendingEvent> engineCompletions = null;
        List<PendingEvent> armings = null;
        bool releaseProbeVk = false;
        long now = NowMs();
        lock (pendingGate) {
            for (int i = 0; i < pendingEvents.Count; ) {
                PendingEvent p = pendingEvents[i];
                if (p.Probe) {
                    int v = RawSink.FindRecent(p.Vk, true, p.HookTick - 30, now);
                    if (v < 0) v = RawSink.FindRecent(p.RealVk, true, p.HookTick - 30, now);   // raw kept the original vk
                    if (v < 0 && now - p.HookTick < 500) break;  // probes are ordered: wait for its WM_INPUT
                    if (v < 0) v = 0;                            // sink blind -> physical, consistent with the global default
                    probeVerdict[p.RealVk] = v;                  // publish first: the hook stops passing repeats right away
                    releaseProbeVk = true;                       // retire the harmless F24 press the app received
                    Log.Info("[INPUT] 重复键探测 0x" + p.RealVk.ToString("X2") + " -> " +
                             (v == 1 ? "遥控器（补装引擎，后续重复键转吞）" : "物理键盘（重复键持续放行）"));
                    if (v == 1) {
                        NoteRemoteKey(p.RealVk, true);           // remote mid-hold: light the held key
                        armedPresses[p.RealVk] = true;
                        if (armings == null) armings = new List<PendingEvent>();
                        armings.Add(p);
                    }
                    pendingEvents.RemoveAt(i);
                    continue;
                }
                int verdict;
                bool known;
                bool armed;
                if (p.Probing) {
                    known = probeVerdict.TryGetValue(p.Vk, out verdict);
                    if (!known) break;                           // its probe is still unresolved (head of line)
                    armed = armedPresses.TryGetValue(p.Vk, out armed) && armed;
                    armedPresses.Remove(p.Vk);
                    probeVerdict.Remove(p.Vk);
                    pendingEvents.RemoveAt(i);
                    if (armed) {
                        // engine already holds the real press: just complete it
                        // (fires hold-through release; a click was already handled)
                        if (engineCompletions == null) engineCompletions = new List<PendingEvent>();
                        engineCompletions.Add(p);
                    }                                            // physical: repeats + up were real, nothing to do
                    continue;
                }
                verdict = RawSink.FindRecent(p.Vk, false, p.HookTick - 30, now);
                if (verdict < 0 && now - p.HookTick < 500) break;
                pendingEvents.RemoveAt(i);
                if (verdict == 1) {                              // click: synthesize the press the hook swallowed
                    if (remoteClicks == null) remoteClicks = new List<PendingEvent>();
                    remoteClicks.Add(p);
                } else {                                         // physical click: give back the original key (injected)
                    if (physicalTaps == null) physicalTaps = new List<ushort>();
                    physicalTaps.Add(p.Vk);
                }
            }
        }
        // injections and engine work happen OUTSIDE pendingGate: the hook thread
        // waits on that lock, and SendInput inside it would stall all input
        if (releaseProbeVk) KeySender.Release(new ushort[] { PROBE_VK });   // injected -> hook ignores
        var act = new List<GestureEngine.OutAction>();
        if (armings != null) {
            for (int i = 0; i < armings.Count; i++) {
                // remote mid-hold: arm the engine with the REAL press, backdated
                // so the hold threshold lands on real time
                lock (mapGate) {
                    engine.Feed(armings[i].RealVk, true, armings[i].DownTick, act);
                    engine.Tick(NowMs(), act);
                }
                Flush(act);
            }
        }
        if (engineCompletions != null) {
            for (int i = 0; i < engineCompletions.Count; i++) {
                lock (mapGate) engine.Feed(engineCompletions[i].Vk, false, NowMs(), act);
                Flush(act);
            }
        }
        if (remoteClicks != null) {
            for (int i = 0; i < remoteClicks.Count; i++) {
                ushort vk = remoteClicks[i].Vk;
                long tick = NowMs();
                NoteRemoteKey(vk, true);                         // the DOWN was swallowed silently: light now
                mapQueue.TryAdd(new GestureEngine.RawEvent { Vk = vk, Down = true, TickMs = tick });
                mapQueue.TryAdd(new GestureEngine.RawEvent { Vk = vk, Down = false, TickMs = tick + 1 });
                Log.Info("[INPUT] 延迟归因 0x" + vk.ToString("X2") + " -> 遥控器（补发映射动作）");
            }
        }
        if (physicalTaps != null) {
            for (int i = 0; i < physicalTaps.Count; i++) {
                KeySender.Tap(new ushort[] { physicalTaps[i] }); // injected -> hook ignores, hDevice=0 votes for neither side
                Log.Info("[INPUT] 延迟归因 0x" + physicalTaps[i].ToString("X2") + " -> 物理键盘（还原原键）");
            }
        }
    }

    static long NowMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

    /// Unit-test hook: simulate a key event and return the verdict (1 = swallowed).
    internal static IntPtr TestDispatch(uint vk, bool down, bool fromRemote) {
        var k = new KBDLLHOOKSTRUCT { vkCode = vk };
        IntPtr mem = Marshal.AllocHGlobal(Marshal.SizeOf(k));
        try {
            Marshal.StructureToPtr(k, mem, false);
            return HookCb(0, (IntPtr)(down ? 0x0100 : 0x0101), mem);
        } finally { Marshal.FreeHGlobal(mem); }
    }
    internal static IntPtr TestDispatch(uint vk, bool down) { return TestDispatch(vk, down, true); }

    public static void Start() {
        if (pump != null) return;
        pump = new Thread((ThreadStart)delegate {
            pumpTid = GetCurrentThreadId();
            hhk = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            Log.Info("[INPUT] router armed (blockF5 " + (blockF5 ? "on" : "off") +
                     ", mapping " + (mappingEnabled ? "on" : "off") + " while linked)" +
                     " hook=0x" + hhk.ToString("X") +
                     (hhk == IntPtr.Zero ? " FAILED err=" + Marshal.GetLastWin32Error() : ""));
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) {
                if (m.message == WM_APP_REHOOK && hhk == IntPtr.Zero) {
                    // safety net for a hook that vanished for any reason (system
                    // policy, watchdog repost); normally never taken
                    hhk = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                    Log.Info("[INPUT] hook reinstalled 0x" + hhk.ToString("X") +
                             (hhk == IntPtr.Zero ? " FAILED err=" + Marshal.GetLastWin32Error() : ""));
                }
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
            Log.Error("[INPUT] pump exited - keyboard hook lost (GetMessage returned <= 0)");
        }) { IsBackground = true, Name = "inputrouter" };
        pump.Start();
        RawSink.Start();
        if (mapWorker == null) {
            mapWorker = new Thread((ThreadStart)delegate {
                var pending = new List<GestureEngine.OutAction>();
                while (true) {
                    GestureEngine.RawEvent ev;
                    while (mapQueue.TryTake(out ev, 20)) {
                        try {
                            if (ev.ReleaseAll) {            // link drop / table swap: release held combos
                                var rel = new List<GestureEngine.OutAction>();
                                lock (mapGate) engine.Reset(rel);
                                Flush(rel);
                                continue;
                            }
                            lock (mapGate) engine.Feed(ev.Vk, ev.Down, ev.TickMs, pending);
                            Flush(pending);
                        } catch (Exception ex) { Log.Error("[INPUT] worker: " + ex.Message); }
                    }
                    try {
                        lock (mapGate) engine.Tick(NowMs(), pending);
                        Flush(pending);
                    } catch (Exception ex) { Log.Error("[INPUT] tick: " + ex.Message); }
                    try { ResolvePendingEvents(); } catch (Exception ex) { Log.Error("[INPUT] pending: " + ex.Message); }
                    // watchdog: if the hook vanished for any reason while the
                    // remote is linked, ask the pump to reinstall (self-heal ~1s)
                    if (linked && hhk == IntPtr.Zero &&
                        Math.Abs(Environment.TickCount - lastHookMissingTick) > 1000) {
                        lastHookMissingTick = Environment.TickCount;
                        Log.Warn("[INPUT] watchdog: hook missing while linked -> repost rehook");
                        PostThreadMessage(pumpTid, WM_APP_REHOOK, IntPtr.Zero, IntPtr.Zero);
                    }
                }
            }) { IsBackground = true, Name = "keymap" };
            mapWorker.Start();
        }
    }

    public static void Stop() {
        if (hhk != IntPtr.Zero) { UnhookWindowsHookEx(hhk); hhk = IntPtr.Zero; }
        RawSink.Stop();
    }

    /// Unit-test hook: publish a repeat-probe verdict as the worker would.
    internal static void TestSetProbeVerdict(uint vk, int verdict) {
        lock (pendingGate) probeVerdict[(ushort)vk] = verdict;
    }

    public static void SetBlockF5(bool on) { blockF5 = on; }
    public static void SetLinked(bool on) {
        linked = on;
        if (!on) ReleaseEngineHolds();                      // remote gone mid-press: release its combos
    }
    public static void SetMappingEnabled(bool on) { mappingEnabled = on; }

    /// Swap in a new mapping table (config edit / preset load). Safe at any time.
    public static void SetKeyMap(KeyMapConfig map) { SetKeyMap(map, null); }

    public static void SetKeyMap(KeyMapConfig map, string macPrefix) {
        if (map == null) {
            mappingEnabled = false;
            vkToId = new Dictionary<ushort, string>();     // no table -> nothing lights
            ReleaseEngineHolds();                          // old table may still hold combos down
            return;
        }
        List<KeyMapEntry> bound = new List<KeyMapEntry>();
        var table = new Dictionary<ushort, string>();      // ALL keys, mapped or not:
        foreach (KeyMapEntry e in map.keys) {              // an unmapped key still passes
            if (e == null) continue;                       // the hook and must light the art
            if (e.Mapped) bound.Add(e);
            ushort vk = e.VkCode;
            if (vk != 0 && !table.ContainsKey(vk)) table[vk] = e.id;
        }
        var releases = new List<GestureEngine.OutAction>();
        lock (mapGate) {
            engine.Reset(releases);                        // stuck hold-through combos from the old table
            engine = new GestureEngine(bound.ToArray());
            mappingEnabled = map.enabled && bound.Count > 0;
            RawSink.SetMatchers(map.matchVidPid, macPrefix);
        }
        vkToId = table;
        Flush(releases);                                   // SendInput outside mapGate
        Log.Info("[INPUT] keymap: " + bound.Count + " mapped key(s), " + (mappingEnabled ? "enabled" : "disabled"));
    }

    /// Drop deferred-press bookkeeping and queue a sentinel so the map worker
    /// releases every combo the gesture engine currently holds (the matching
    /// UP will never arrive once the remote is gone or the table is swapped).
    /// The hook-owned "pending" dict is left alone - it self-heals on the next
    /// UP of the same key.
    static void ReleaseEngineHolds() {
        lock (pendingGate) { pendingEvents.Clear(); probeVerdict.Clear(); armedPresses.Clear(); }
        mapQueue.TryAdd(new GestureEngine.RawEvent { ReleaseAll = true });
    }

    static void Flush(List<GestureEngine.OutAction> actions) {
        for (int i = 0; i < actions.Count; i++) Execute(actions[i]);
        actions.Clear();
    }

    static void Execute(GestureEngine.OutAction a) {
        try {
            KeyMapAction act = a.Action;
            switch (act.Kind) {
                case MapActionKind.Combo: {
                    ushort[] combo;
                    if (!KeyMapNames.TryParseCombo(act.keys, out combo)) return;
                    combo = KeyMapNames.NormalizeForInject(combo);   // Win-class modifiers first (WeType order quirk)
                    if (act.tap) KeySender.Tap(combo);
                    else if (a.Phase == GestureEngine.Phase.Down) KeySender.Press(combo);
                    else KeySender.Release(combo);
                    break;
                }
                case MapActionKind.TaskView:
                    KeySender.Tap(new ushort[] { 0x5B, 0x09 });      // Win+Tab
                    break;
                case MapActionKind.Launch: {
                    string path, args;
                    SplitCommand(act.command, out path, out args);
                    if (path.Length > 0)
                        Process.Start(new ProcessStartInfo { FileName = path, Arguments = args, UseShellExecute = true });
                    break;
                }
                case MapActionKind.Cmd:
                    Process.Start(new ProcessStartInfo {
                        FileName = "cmd.exe", Arguments = "/c " + act.command,
                        CreateNoWindow = true, UseShellExecute = false
                    });
                    break;
            }
            string desc = KeyMapNames.Describe(act);
            Log.Info("[INPUT] " + a.EntryName + " -> " + desc + (a.Phase == GestureEngine.Phase.Up ? " (release)" : ""));
            FireAction(a.EntryId, desc);
        } catch (Exception ex) {
            Log.Error("[INPUT] exec " + a.EntryName + ": " + ex.Message);
        }
    }

    internal static void SplitCommand(string command, out string path, out string args) {
        path = ""; args = "";
        if (string.IsNullOrWhiteSpace(command)) return;
        command = command.Trim();
        if (command[0] == '"') {
            int end = command.IndexOf('"', 1);
            if (end > 0) {
                path = command.Substring(1, end - 1);
                args = end + 1 < command.Length ? command.Substring(end + 1).Trim() : "";
                return;
            }
        }
        int sp = command.IndexOf(' ');
        if (sp < 0) { path = command; return; }
        path = command.Substring(0, sp);
        args = command.Substring(sp + 1).Trim();
    }

    // ---- injection-time hook policy -----------------------------------------
    // The hook is NEVER unhooked around injections (see the class header of
    // HotkeyInjector / the real-device log of 2026-09-07: unhook+rehook cycles
    // intermittently lost or zombified the hook). HookCb filters injected keys
    // via LLKHF_INJECTED, so our own SendInput can never re-enter it. The
    // watchdog below only guards against the hook vanishing for other reasons.
    static int lastHookMissingTick;       // mapWorker only: watchdog throttle
}

// ---- gesture engine (pure logic, unit-testable) ---------------------------
sealed class GestureEngine {
    public enum Phase { Down, Up, Once }

    public struct RawEvent { public ushort Vk; public bool Down; public long TickMs; public bool ReleaseAll; }

    public struct OutAction { public string EntryId; public string EntryName; public KeyMapAction Action; public Phase Phase; }

    sealed class KeyState {
        public long DownSince; public bool Armed; public bool HoldFired; public KeyMapEntry Entry;
    }

    readonly Dictionary<ushort, KeyMapEntry> byVk = new Dictionary<ushort, KeyMapEntry>();
    readonly Dictionary<ushort, KeyState> states = new Dictionary<ushort, KeyState>();

    public GestureEngine(IList<KeyMapEntry> entries) {
        if (entries == null) return;
        foreach (KeyMapEntry e in entries) {
            ushort vk = e.VkCode;
            if (vk != 0 && !byVk.ContainsKey(vk)) byVk[vk] = e;
        }
    }

    public int Count { get { return byVk.Count; } }

    public bool HasBinding(uint vk) { return byVk.ContainsKey((ushort)vk); }

    static bool HoldConfigured(KeyMapEntry e) { return e.hold != null && e.hold.HasPayload; }

    public void Feed(ushort vk, bool down, long now, List<OutAction> out_) {
        KeyMapEntry e;
        if (!byVk.TryGetValue(vk, out e)) return;
        if (down) {
            KeyState st;
            if (!states.TryGetValue(vk, out st)) { st = new KeyState(); states[vk] = st; }
            if (st.DownSince > 0) return;                        // repeat / stuck: ignore
            st.Entry = e; st.DownSince = now; st.HoldFired = false;
            if (HoldConfigured(e)) { st.Armed = true; }          // wait for threshold or early release
            else {
                st.Armed = false;
                EmitClick(e, Phase.Down, out_);                  // no hold gesture: act immediately
            }
        } else {
            KeyState st;
            if (!states.TryGetValue(vk, out st) || st.DownSince == 0) return;
            st.DownSince = 0;
            if (st.HoldFired) {
                if (e.hold != null && e.hold.Kind == MapActionKind.Combo && !e.hold.tap)
                    out_.Add(Make(e, e.hold, Phase.Up));          // release hold-through combo
            } else if (st.Armed) {
                EmitClick(e, Phase.Once, out_);                  // released before hold threshold
            } else {
                if (e.click != null && e.click.Kind == MapActionKind.Combo && !e.click.tap)
                    out_.Add(Make(e, e.click, Phase.Up));         // release hold-through combo
            }
            st.Armed = false;
        }
    }

    void EmitClick(KeyMapEntry e, Phase phase, List<OutAction> out_) {
        if (e.click == null || !e.click.HasPayload) return;
        if (phase == Phase.Once && e.click.Kind == MapActionKind.Combo && !e.click.tap) {
            // hold-through combo released quickly: press + release back-to-back
            out_.Add(Make(e, e.click, Phase.Down));
            out_.Add(Make(e, e.click, Phase.Up));
        } else {
            out_.Add(Make(e, e.click, phase));
        }
    }

    public void Tick(long now, List<OutAction> out_) {
        foreach (var kv in states) {
            KeyState st = kv.Value;
            if (st.DownSince == 0 || st.HoldFired || !st.Armed) continue;
            KeyMapEntry e = st.Entry;
            uint ms = e.hold != null && e.hold.ms > 0 ? e.hold.ms : 600;
            if (now - st.DownSince >= ms) {
                st.HoldFired = true;
                if (e.hold != null && e.hold.HasPayload) {
                    if (e.hold.Kind == MapActionKind.Combo && !e.hold.tap)
                        out_.Add(Make(e, e.hold, Phase.Down));
                    else
                        out_.Add(Make(e, e.hold, Phase.Once));
                }
            }
        }
    }

    /// Release everything the engine currently holds WITHOUT firing pending
    /// actions (link drop / mapping-table swap: the real UP is never coming,
    /// and a half-press must not trigger an action on the way out). Only
    /// combos whose Down phase already went out get their Up.
    public void Reset(List<OutAction> out_) {
        foreach (var kv in states) {
            KeyState st = kv.Value;
            if (st.DownSince == 0) continue;
            KeyMapEntry e = st.Entry;
            if (st.HoldFired) {
                if (e.hold != null && e.hold.Kind == MapActionKind.Combo && !e.hold.tap)
                    out_.Add(Make(e, e.hold, Phase.Up));
            } else if (!st.Armed) {
                if (e.click != null && e.click.Kind == MapActionKind.Combo && !e.click.tap)
                    out_.Add(Make(e, e.click, Phase.Up));
            }
            st.DownSince = 0; st.Armed = false; st.HoldFired = false;
        }
    }

    static OutAction Make(KeyMapEntry e, KeyMapAction a, Phase p) {
        OutAction o;
        o.EntryId = e.id; o.EntryName = e.name; o.Action = a; o.Phase = p;
        return o;
    }
}

// ---- combo sender (SendInput, hook suspended during injection) -------------
static class KeySender {
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    const int INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_SCANCODE = 0x0008;

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }

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
        INPUT i = new INPUT();
        i.type = INPUT_KEYBOARD;
        i.keyboard = new KEYBDINPUT { vk = vk, scan = (ushort)MapVirtualKey(vk, 0), flags = flags };
        return i;
    }

    public static void Press(ushort[] keys) { SendAll(keys, true); }
    public static void Release(ushort[] keys) { SendAll(keys, false); }
    internal static ushort ScanOf(ushort vk) { return (ushort)MapVirtualKey(vk, 0); }
    public static void Tap(ushort[] keys) {
        var inputs = new List<INPUT>();
        for (int i = 0; i < keys.Length; i++) inputs.Add(Make(keys[i], true));
        for (int i = keys.Length - 1; i >= 0; i--) inputs.Add(Make(keys[i], false));
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
    }

    static void SendAll(ushort[] keys, bool down) {
        var inputs = new INPUT[keys.Length];
        int idx = 0;
        for (int i = 0; i < keys.Length; i++) inputs[idx++] = down ? Make(keys[i], true) : Make(keys[keys.Length - 1 - i], false);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }
}

// ---- raw input attribution sink --------------------------------------------
static class RawSink {
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern short RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, System.Text.StringBuilder pData, ref uint pcbSize);
    [DllImport("user32.dll")] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint count, uint size);

#pragma warning disable 649   // filled by GetRawInputDeviceList via marshaling
    struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }
#pragma warning restore 649

    /// Startup visibility: list every raw-input keyboard and mark which one
    /// attribution picks as the remote (goes to MiVoiceMic.log).
    static void LogRawKeyboards() {
        try {
            uint count = 0, size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            if (GetRawInputDeviceList(null, ref count, size) == unchecked((uint)-1) || count == 0) return;
            var list = new RAWINPUTDEVICELIST[count];
            if (GetRawInputDeviceList(list, ref count, size) == unchecked((uint)-1)) return;
            int keyboards = 0; string matched = null;
            lock (ringGate) {
                foreach (var d in list) {
                    if (d.dwType != 1) continue;                  // RIM_TYPEKEYBOARD
                    keyboards++;
                    var sb = new System.Text.StringBuilder(512);
                    uint sz = (uint)sb.Capacity;
                    uint r = GetRawInputDeviceInfo(d.hDevice, RIDI_DEVICENAME, sb, ref sz);
                    if (r == unchecked((uint)-1) || r == 0) continue;
                    string name = sb.ToString();
                    bool remote = DeviceIsRemote(d.hDevice);
                    Log.Info("[INPUT] raw 键盘: " + name + (remote ? "  <= 遥控器" : ""));
                    if (remote) matched = name;
                }
            }
            Log.Info("[INPUT] raw 键盘共 " + keyboards + " 台; 遥控器归因: " +
                     (matched != null ? "已匹配" : "未发现（检查 keymap.matchVidPid / deviceMacPrefix）"));
        } catch (Exception ex) { Log.Error("[INPUT] rawlist: " + ex.Message); }
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProc lpfnWndProc; public int cbClsExtra; public int cbWndExtra; public IntPtr hInstance; public IntPtr hIcon; public IntPtr hCursor; public IntPtr hBackground; public string lpszMenuName; public string lpszClassName; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWKEYBOARD { public ushort MakeCode; public ushort Flags; public ushort Reserved; public ushort VKey; public uint Message; public ulong ExtraInformation; }

    [StructLayout(LayoutKind.Explicit)]
    struct RAWINPUT {
        [FieldOffset(0)] public RAWINPUTHEADER header;
        [FieldOffset(24)] public RAWKEYBOARD keyboard;
    }

    const uint WM_INPUT = 0x00FF;
    const uint RIDEV_INPUTSINK = 0x00000100;
    const uint RID_INPUT = 0x10000003;         // NOT 0x100000 - winuser.h RID_INPUT; the wrong value made GetRawInputData always fail with -1
    const uint RIDI_DEVICENAME = 0x20000007;   // NOT 0x20000003 - the wrong value made every query fail

    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int px, py; }

    const uint WM_QUIT = 0x0012;
    static uint sinkTid;                                   // rawsink thread id (for Stop)

    static Thread thread;
    static IntPtr hwnd = IntPtr.Zero;
    static readonly WndProc proc = SinkProc;
    // raw config (re-parsed on Start) + derived substrings matched against the
    // lowercase raw-input device path
    static List<string> vidPid = new List<string> { "2717:32B8" };
    static string macPrefix = "";
    static List<string> matchers = BuildMatchers(vidPid, macPrefix);

    // correlation ring (diagnostics) + device-evidence clocks (decision).
    // WM_INPUT for a key is posted only AFTER the LL hook chain returns, so the
    // hook can never correlate the CURRENT key - it decides from which device
    // spoke most recently (injected events, hDevice=0, vote for neither side).
    struct Rec { public ushort Vk; public bool Down; public long Tick; public bool Remote; }
    const int RING = 64;
    static readonly Rec[] ring = new Rec[RING];
    static int ringHead;                                    // next write slot
    static readonly object ringGate = new object();
    static readonly Dictionary<IntPtr, bool> deviceCache = new Dictionary<IntPtr, bool>();
    static long lastRemoteTick;                             // last WM_INPUT from the remote
    static long lastForeignTick;                            // last WM_INPUT from any real non-remote keyboard
    const long EVIDENCE_FRESH_MS = 2000;                    // older evidence stops voting

    static long NowMs() { return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond; }

    /// <param name="vidPidList">Config entries like "2717:32B8" (hex, "0x" ok).</param>
    /// <param name="macPrefix">Optional BLE MAC prefix like "C0:5D:39" - the raw
    /// input path of a HID-over-GATT device embeds its MAC, so this is a
    /// model-independent extra matcher.</param>
    public static void SetMatchers(List<string> vidPidList, string mac) {
        if (vidPidList != null && vidPidList.Count > 0) vidPid = vidPidList;
        macPrefix = mac ?? "";
        List<string> m = BuildMatchers(vidPid, macPrefix);
        lock (ringGate) { matchers = m; deviceCache.Clear(); }
    }

    /// USB keyboards enumerate as HID\VID_2717&amp;PID_32B8\... but HID-over-GATT
    /// (BLE) keyboards as HID\{1812guid}_DEV_VID&amp;012717_PID&amp;32b8_REV&amp;00a4_&lt;mac&gt; -
    /// VID is stored as (vendorIdSource&lt;&lt;16)|vid, source 01=Bluetooth SIG /
    /// 02=USB-IF, so both spellings must be matched (the USB-style substring
    /// alone never matches a BLE remote - that silently disabled keymap
    /// attribution on the real device).
    static List<string> BuildMatchers(List<string> vidPidList, string macPrefix) {
        var m = new List<string>();
        if (vidPidList != null)
            foreach (string s in vidPidList) {
                if (string.IsNullOrWhiteSpace(s)) continue;
                string[] parts = s.Split(':');
                uint vid, pid;
                if (parts.Length == 2 && TryHex(parts[0].Trim(), out vid) && TryHex(parts[1].Trim(), out pid)) {
                    string v = vid.ToString("x4"), p = pid.ToString("x4");
                    m.Add("vid_" + v + "&pid_" + p);          // USB HID
                    m.Add("vid&0" + v + "_pid&" + p);         // BLE HOGP, SIG vendor source
                    m.Add("vid&2" + v + "_pid&" + p);         // BLE HOGP, USB vendor source
                }
            }
        if (!string.IsNullOrWhiteSpace(macPrefix)) {
            var hex = new System.Text.StringBuilder();
            foreach (char c in macPrefix)
                if (Uri.IsHexDigit(c)) hex.Append(char.ToLowerInvariant(c));
            if (hex.Length >= 6) m.Add(hex.ToString(0, 6));   // "C0:5D:39" -> "c05d39"
        }
        if (m.Count == 0) return BuildMatchers(new List<string> { "2717:32B8" }, "");
        return m;
    }

    static bool TryHex(string s, out uint v) {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
    }

    public static void Start() {
        lock (ringGate) matchers = BuildMatchers(vidPid, macPrefix);   // re-derive (config may have landed before Start)
        if (thread != null) return;
        thread = new Thread((ThreadStart)delegate {
            sinkTid = GetCurrentThreadId();
            var wc = new WNDCLASS();
            wc.lpfnWndProc = proc;
            wc.hInstance = GetModuleHandle(null);
            wc.lpszClassName = "MivmRawSink";
            RegisterClassW(ref wc);
            // MUST be a normal top-level window, NOT a HWND_MESSAGE message-only
            // window: the raw input system never posts WM_INPUT to message-only
            // windows (classic Win32 trap - the ring silently stays empty and
            // every mapped key then looks like it came from a physical keyboard).
            // Hidden (style 0) + RIDEV_INPUTSINK = delivers without focus; the
            // tool-window/no-activate ex styles keep it out of taskbar & Alt-Tab.
            const uint WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            hwnd = CreateWindowEx(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                "MivmRawSink", "", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { Log.Error("[INPUT] raw sink window failed: " + Marshal.GetLastWin32Error()); return; }
            var rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = 1; rid[0].usUsage = 6;       // generic desktop / keyboard
            rid[0].dwFlags = RIDEV_INPUTSINK;
            rid[0].hwndTarget = hwnd;
            if (!RegisterRawInputDevices(rid, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                Log.Error("[INPUT] RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());
            else
                Log.Info("[INPUT] raw sink hwnd=0x" + hwnd.ToString("X") + " registered (top-level, INPUTSINK)");
            LogRawKeyboards();
            MSG m;
            while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessage(ref m); }
            DestroyWindow(hwnd);      // our own window: DestroyWindow must run on its creating thread
            hwnd = IntPtr.Zero;
        }) { IsBackground = true, Name = "rawsink" };
        thread.Start();
    }

    public static void Stop() {
        if (sinkTid != 0) PostThreadMessage(sinkTid, WM_QUIT, IntPtr.Zero, IntPtr.Zero);   // lets the thread unregister/destroy cleanly
    }

    static IntPtr SinkProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam) {
        if (msg == WM_INPUT) {
            // single call with a fixed buffer - the pData=NULL size probe of
            // GetRawInputData fails outright (same disease as RIDI_DEVICENAME's
            // NULL probe, fixed 2026-09-03); keyboard RAWINPUT is 48 bytes
            IntPtr buf = Marshal.AllocHGlobal(256);
            try {
                uint hdr = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
                uint size = 256;
                uint got = GetRawInputData(lParam, RID_INPUT, buf, ref size, hdr);
                if (got != unchecked((uint)-1) && got >= hdr) {
                    var raw = (RAWINPUT)Marshal.PtrToStructure(buf, typeof(RAWINPUT));
                    if (raw.header.dwType == 1) {              // RIM_TYPEKEYBOARD
                        bool down = raw.keyboard.Message == 0x0100 || raw.keyboard.Message == 0x0104;
                        // normalize the driver voice key (F20) to F5 so the ring
                        // correlates with the hook's normalized vk (probe path)
                        ushort vk = (ushort)InputRouter.NormalizeVoiceKey(raw.keyboard.VKey);
                        bool remote = DeviceIsRemote(raw.header.hDevice);
                        long tick = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
                        lock (ringGate) {
                            ring[ringHead].Vk = vk;
                            ring[ringHead].Down = down;
                            ring[ringHead].Tick = tick;
                            ring[ringHead].Remote = remote;
                            ringHead = (ringHead + 1) % RING;
                            if (remote) lastRemoteTick = tick;
                            else if (raw.header.hDevice != IntPtr.Zero) lastForeignTick = tick;
                        }
                        if (remote) InputRouter.NoteRemoteKey(vk, down);   // passed remote key: light on down, dark on up (per-event truth)
                        int t = Environment.TickCount;         // diagnosis: remote keys only - otherwise every physical keystroke floods the log
                        if (remote && (t - lastEvtLog > 400 || t < lastEvtLog)) {
                            lastEvtLog = t;
                            Log.Info("[INPUT] wm_input vk=0x" + vk.ToString("X2") +
                                     (down ? " down" : " up") + " dev=0x" + raw.header.hDevice.ToString("X") +
                                     " remote=" + (remote ? 1 : 0));
                        }
                    }
                } else {
                    int t = Environment.TickCount;
                    if (t - lastEvtLog > 400 || t < lastEvtLog) {
                        lastEvtLog = t;
                        Log.Info("[INPUT] wm_input parse FAIL got=" + got + " size=" + size);
                    }
                }
            } finally { Marshal.FreeHGlobal(buf); }
        }
        return DefWindowProcW(h, msg, wParam, lParam);
    }
    static int lastEvtLog;                                     // rawsink thread only

    static bool DeviceIsRemote(IntPtr hDevice) {
        if (hDevice == IntPtr.Zero) return false;             // SendInput injections
        lock (ringGate) {
            bool cached;
            if (deviceCache.TryGetValue(hDevice, out cached)) return cached;
            bool remote = false;
            string path = "";
            int r = -1;
            try {
                // no NULL size-probe here: RIDI_DEVICENAME with pData=NULL fails
                // outright - a big-enough buffer must be passed in one call
                var sb = new System.Text.StringBuilder(512);
                uint sz = (uint)sb.Capacity;
                r = unchecked((int)GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, sb, ref sz));
                if (r != -1 && r > 0) {
                    path = sb.ToString().ToLowerInvariant();
                    foreach (string m in matchers)
                        if (path.IndexOf(m) >= 0) { remote = true; break; }
                }
            } catch { }
            deviceCache[hDevice] = remote;
            // first time this handle is seen after (re)connection: log the verdict -
            // a BLE remote gets a new handle each reconnect, so this line tracks it
            Log.Info("[INPUT] attrib dev=0x" + hDevice.ToString("X") + " r=" + r +
                     " remote=" + (remote ? 1 : 0) + " path=" + path);
            return remote;
        }
    }

    /// Diagnostics for the last decision (hook thread only).
    public static string LastProbe = "-";

    /// Decide from device evidence whether a mapped key belongs to the remote.
    /// Called from the hook, so it must never wait: WM_INPUT for the current
    /// key arrives only after the hook chain returns (observed on Win11 RDP),
    /// which is why in-hook polling of the current event can never succeed.
    ///   physical keyboard spoke more recently                  -> physical
    ///   remote spoke more recently (latched, see NoteRemoteActivity) -> remote
    ///   no fresh evidence at all                               -> NOT remote
    ///     = deferred attribution (PendingUp): the DOWN is swallowed so a
    ///     remote first press can never ghost, and the UP's own WM_INPUT names
    ///     the true device. The old default ("mapping is on and the link is
    ///     up, so it's almost certainly the remote") remapped every isolated
    ///     physical arrow press during quiet periods and could never
    ///     self-correct; the pass-through alternative ghosted the remote.
    public static bool LooksFromRemote() {
        long now = NowMs();
        lock (ringGate) {
            bool remoteFresh = lastRemoteTick > 0 && now - lastRemoteTick < EVIDENCE_FRESH_MS;
            bool foreignFresh = lastForeignTick > 0 && now - lastForeignTick < EVIDENCE_FRESH_MS;
            if (foreignFresh && (!remoteFresh || lastForeignTick > lastRemoteTick)) {
                LastProbe = "物理键盘最后按键 " + (now - lastForeignTick) + "ms 前";
                return false;
            }
            if (remoteFresh) {
                LastProbe = "遥控器最后按键 " + (now - lastRemoteTick) + "ms 前";
                return true;
            }
            LastProbe = "无近期按键证据 → 延迟归因";
            return false;
        }
    }

    /// The hook just swallowed a mapped key on a "remote" verdict. Swallowed
    /// keys never reach the raw-input sink, so without this latch the remote's
    /// evidence clock would starve mid-burst and the very next press would
    /// fall back to pass-through. A real keystroke from the physical keyboard
    /// (it always passes) still overrides: fresh foreign evidence wins.
    public static void NoteRemoteActivity() {
        lock (ringGate) { lastRemoteTick = NowMs(); }
    }

    /// Test helper: seed the evidence clocks (ages in ms, -1 = never seen).
    internal static void SeedEvidence(long remoteAgeMs, long foreignAgeMs) {
        long now = NowMs();
        lock (ringGate) {
            lastRemoteTick = remoteAgeMs >= 0 ? now - remoteAgeMs : 0;
            lastForeignTick = foreignAgeMs >= 0 ? now - foreignAgeMs : 0;
        }
    }

    internal static long TicksNow() { return NowMs(); }

    /// Test helper: push one event into the correlation ring (age in ms).
    internal static void SeedRing(ushort vk, bool down, bool remote, long ageMs) {
        long tick = NowMs() - ageMs;
        lock (ringGate) {
            ring[ringHead].Vk = vk;
            ring[ringHead].Down = down;
            ring[ringHead].Tick = tick;
            ring[ringHead].Remote = remote;
            ringHead = (ringHead + 1) % RING;
        }
    }

    /// Newest ring event matching {vk, down} with Tick in [minTick, maxTick].
    /// Returns 1 remote / 0 physical / -1 not found. Used by the deferred
    /// attribution path: the passed orphan UP's own WM_INPUT is per-event truth
    /// about which device the press came from.
    public static int FindRecent(ushort vk, bool down, long minTick, long maxTick) {
        lock (ringGate) {
            int best = -1;
            long bestTick = long.MinValue;
            for (int i = 0; i < RING; i++) {
                Rec r = ring[i];
                if (r.Vk == vk && r.Down == down && r.Tick >= minTick && r.Tick <= maxTick && r.Tick > bestTick) {
                    bestTick = r.Tick;
                    best = r.Remote ? 1 : 0;
                }
            }
            return best;
        }
    }
}
