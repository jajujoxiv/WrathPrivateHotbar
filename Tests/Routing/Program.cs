using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using WrathPrivateHotbar;
using M = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using S = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using O = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarUIIntermediate;
using K = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

internal unsafe class FakeClient : IGameInteropProvider, IGameGui
{
    public delegate uint Adjustment(ActionManager* am, uint id);
    public delegate byte ExecId(M* m, uint b, uint i);
    public delegate byte ExecSlot(M* m, S* s);
    public delegate void Prepare(M* m, S* s, O* ui);
    public Adjustment Adjust = null!; public ExecId RunId = null!; public ExecSlot RunSlot = null!; public Prepare Render = null!;
    public AddonActionBarBase* Hud;
    public uint LastAction; public int Dispatches; public bool QueryInside = true, ThrowOnRender, ThrowOnDispatch;
    public uint NativeOpener = 200;
    public uint Standard(ActionManager* am, uint id) => id == 100 ? NativeOpener : id;
    public byte OriginalId(M* m, uint b, uint i) => RunSlot(m, m->GetSlotById(b, i));
    public byte OriginalSlot(M* m, S* s)
    {
        if (ThrowOnDispatch)
            throw new ArithmeticException();
        LastAction = QueryInside ? Adjust(ActionManager.Instance(), s->CommandId) : s->CommandId;
        Dispatches++;
        return 71;
    }
    public void OriginalRender(M* m, S* s, O* outp)
    {
        if (ThrowOnRender)
            throw new ArithmeticException();
        s->NativeIconCache = (uint)s->GetIconIdForSlot(s->ApparentSlotType, s->ApparentActionId);
        outp->ActionId = s->ApparentActionId;
        outp->IconId = (uint)s->GetIconIdForSlot(s->ApparentSlotType, s->ApparentActionId);
        outp->Cost = 19;
        outp->Recast = 12;
    }
    public Hook<T> HookFromAddress<T>(nint addr, T detour) where T : Delegate
    {
        Delegate orig;
        switch ((int)addr)
        {
            case 1:
                Adjust = detour.Method.CreateDelegate<Adjustment>(detour.Target);
                orig = (Adjustment)Standard;
                break;
            case 2:
                RunId = detour.Method.CreateDelegate<ExecId>(detour.Target);
                orig = (ExecId)OriginalId;
                break;
            case 3:
                RunSlot = detour.Method.CreateDelegate<ExecSlot>(detour.Target);
                orig = (ExecSlot)OriginalSlot;
                break;
            case 4:
                Render = detour.Method.CreateDelegate<Prepare>(detour.Target);
                orig = (Prepare)OriginalRender;
                break;
            default:
                throw new Exception();
        }
        return new Hook<T>(orig.Method.CreateDelegate<T>(orig.Target));
    }
    public T* GetAddonByName<T>(string name) where T : unmanaged
    {
        int n = name == "_ActionBar" ? 0 : int.Parse(name.Substring("_ActionBar".Length));
        return (T*)(Hud + n);
    }
}
internal static unsafe class Program
{
    static int checks;
    static void Check(bool yes, string why)
    {
        checks++;
        if (!yes)
            throw new Exception(why);
    }
    static T* Alloc<T>(int n = 1) where T : unmanaged => (T*)NativeMemory.AllocZeroed((nuint)n, (nuint)sizeof(T));
    static void Main()
    {
        var m = M.Live = Alloc<M>();
        m->ModuleReady = true;
        m->Slots = Alloc<S>(120);
        var am = Alloc<ActionManager>();
        ActionManager.Current = am;
        var gui = new FakeClient { Hud = Alloc<AddonActionBarBase>(10) };
        for (int i = 0; i < 10; i++)
        {
            var a = gui.Hud + i;
            a->IsVisible = true;
            a->RootNode = Alloc<Node>();
            a->RootNode->Visible = true; a->Alpha = 255; a->RootNode->Color.RGBA = 0xFFFFFFFF;
            a->SlotCount = 12;
            a->RaptureHotbarId = (i + 4) % 10;
            a->ActionBarSlotVector = new()
            {
                Pointer = Alloc<HudSlot>(12),
                Count = 12
            };
            for (int x = 0; x < 12; x++)
            {
                var button = Alloc<Component>();
                button->AtkResNode = new() { Visible = true, Width = 48, Height = 48, Color = new() { RGBA = 0xffffffff } };
                a->ActionBarSlotVector.Pointer[x].Icon = button;
                var drag = Alloc<DragDrop>();
                drag->OwnerNode = button;
                a->ActionBarSlotVector.Pointer[x].ComponentDragDrop = drag;
            }
        }
        gui.Adjust = gui.Standard;
        ActionManager.GameAdjustment = id => gui.Standard(ActionManager.Instance(), id);
        var p = new Preferences();
        var wrath = new WrathLink { Lookup = id => id == 100 ? 300 : id };
        using var router = new NativeRouter(gui, p, wrath);
        var bars = new GameBars(gui, wrath.Standard);
        for (int s = 0; s < 120; s++)
            m->Slots[s] = new()
            {
                CommandType = K.Action,
                ApparentActionMode = HotbarSlotApparentActionMode.AdjustedAction,
                CommandId = 100,
                ApparentSlotType = K.Action,
                ApparentActionId = 200
            };
        int pairs = 0;
        for (int selected = 1; selected <= 10; selected++)
        {
            p.Bar = selected;
            router.UpdateIndex(m, 1000 + selected);
            for (int bar = 1; bar <= 10; bar++)
            {
                if (selected != bar)
                    pairs++;
                for (int index = 0; index < 12; index++)
                {
                    var slot = m->GetSlotById((uint)(bar - 1), (uint)index);
                    O ui = default;
                    gui.Render(m, slot, &ui);
                    uint expected = bar == selected ? 300u : 200u;
                    Check(ui.ActionId == expected && ui.IconId == expected + 500, "wrong reported icon");
                    Check(ui.Cost == 19 && ui.Recast == 12 && slot->CommandId == 100 && slot->ApparentActionId == 200, "mutated original slot or render fields");
                    int before = gui.Dispatches;
                    gui.QueryInside = true;
                    Check(gui.RunId(m, (uint)(bar - 1), (uint)index) == 71 && gui.LastAction == expected && gui.Dispatches == before + 1, "wrong/duplicate by-ID execution");
                    gui.QueryInside = false;
                    Check(gui.RunSlot(m, slot) == 71 && gui.LastAction == (bar == selected ? 300u : 100u) && gui.Dispatches == before + 2, "no-adjust by-pointer execution");
                    Check(gui.Adjust(am, 100) == 200, "global/other action inherited previous context");
                }
            }
        }
        Console.WriteLine($"display and both execution paths: {pairs} ordered distinct bar pairs, all 12 slots; 10 selected bars");
        // Reproduce the reporter's exact symptom: the same opener in every slot,
        // with the other bars always executing the opener even while Wrath advances.
        gui.NativeOpener = 100;
        for (int i = 0; i < 120; i++)
            m->Slots[i].ApparentActionId = 100;
        foreach (int selected in Enumerable.Range(1, 10))
        {
            p.Bar = selected;
            router.UpdateIndex(m, 1400 + selected);
            foreach (uint stage in new uint[] { 100, 300, 400, 100 })
            {
                wrath.Lookup = id => id == 100 ? stage : id;
                for (int b = 1; b <= 10; b++)
                    for (int i = 0; i < 12; i++)
                    {
                        var s = m->GetSlotById((uint)(b - 1), (uint)i);
                        O ui = default;
                        gui.Render(m, s, &ui);
                        uint wanted = b == selected ? stage : 100;
                        Check(ui.ActionId == wanted && ui.IconId == 500 + wanted, "combo stage leaked into another bar's icon");
                        gui.QueryInside = true;
                        Check(gui.RunId(m, (uint)(b - 1), (uint)i) == 71 && gui.LastAction == wanted, "action differed from displayed icon");
                        Check(s->CommandId == 100 && s->ApparentActionId == 100 && s->NativeIconCache == 600, "stage persisted to stored slot or native render cache");
                    }
            }
        }
        Console.WriteLine("reported unchanged opener: 10 source bars × 10 displayed bars × 12 slots × 4 combo states");
        gui.NativeOpener = 200;
        wrath.Lookup = id => id == 100 ? 300 : id;
        for (int i = 0; i < 120; i++)
            m->Slots[i].ApparentActionId = 200;
        int visualCases = 0;
        for (int selected = 1; selected <= 10; selected++)
            for (int other = 1; other <= 10; other++)
                if (other != selected)
                {
                    p.Bar = selected;
                    p.PreferredBars = 1 << (other - 1);
                    router.UpdateIndex(m, 1500 + selected);
                    for (int index = 0; index < 12; index++)
                    {
                        for (int j = 0; j < 120; j++)
                            m->Slots[j].CommandId = (uint)(1000 + j);
                        m->GetSlotById((uint)(selected - 1), (uint)index)->CommandId = 300;
                        m->GetSlotById((uint)(other - 1), (uint)index)->CommandId = 300;
                        AddonActionBarBase.Pulses.Clear();
                        bars.Reset();
                        bars.Pulse(m, p, K.Action, 100, 300, 2000);
                        Check(AddonActionBarBase.Pulses.SequenceEqual(new[] { (other, index) }), "visible target logical bar and index");
                        bars.Pulse(m, p, K.Action, 100, 300, 2010);
                        Check(AddonActionBarBase.Pulses.Count == 1, "double event replayed the native pulse");
                        bars.Pulse(m, p, K.Action, 100, 300, 2200);
                        Check(AddonActionBarBase.Pulses.Count == 2, "next native pulse failed");
                        visualCases++;
                    }
                }
        Console.WriteLine($"native pulse destinations: {visualCases} cases with shuffled HUD instances");
        p.Bar = 10;
        router.UpdateIndex(m, 4000);
        var nativeSlot = m->GetSlotById(9, 0);
        nativeSlot->CommandId = 100;
        wrath.Lookup = id => { Check(gui.Adjust(am, id) == 200, "recursive adjustment re-entered combo"); return 300; };
        Check(router.Display(new(10, 0), 100) == 300, "selected private lookup");
        using (OnBar.Enter(new(10, 0), 100))
        {
            using (OnBar.Enter(new(2, 0), 100))
            {
                Check(gui.Adjust(am, 100) == 200, "nested other bar");
            }
            Check(gui.Adjust(am, 100) == 200 && OnBar.Current.Bar == 10, "native adjustment inherited scoped replacement");
        }
        wrath.Lookup = id => throw new InvalidOperationException();
        Check(router.Display(new(10, 0), 100) == 200 && gui.Adjust(am, 100) == 200, "resolver failure failover or stale scope");
        wrath.Lookup = id => 300;
        wrath.Ready = false;
        Check(router.Display(new(10, 0), 100) == 200, "unavailable connection");
        wrath.Ready = true;
        nativeSlot->ApparentActionId = 200;
        O failedOutput = default;
        gui.ThrowOnRender = true;
        try
        {
            gui.Render(m, nativeSlot, &failedOutput);
            Check(false, "missing render exception");
        }
        catch (ArithmeticException) { }
        gui.ThrowOnRender = false;
        gui.ThrowOnDispatch = true;
        try
        {
            gui.RunId(m, 9, 0);
            Check(false, "missing dispatch exception");
        }
        catch (ArithmeticException) { }
        gui.ThrowOnDispatch = false;
        Check(nativeSlot->CommandId == 100 && nativeSlot->ApparentActionId == 200 && !OnBar.Current.Valid, "error retained selected ID, appearance or scope");
        p.Bar = 8;
        Check(gui.RunId(m, 7, 0) == 71 && gui.LastAction == 300 && !OnBar.Current.Valid, "newly selected input before index refresh");
        p.Bar = 10;
        router.UpdateIndex(m, 4500);
        for (int b = 0; b < 10; b++)
            for (int i = 0; i < 12; i++)
            {
                int page = bars.Page(b + 1);
                Check(page == gui.Hud[b].RaptureHotbarId + 1, "HUD page misidentified");
                AddonActionBarBase.Pulses.Clear();
                bars.PulseDirect(b + 1, new(page, i), 123, 4500);
                Check(AddonActionBarBase.Pulses.SequenceEqual(new[] { (page, i) }), "direct ordinary press targeted another slot");
            }
        var hiddenOriginal = gui.Hud + 3;
        hiddenOriginal->RootNode->Visible = false;
        bars.ApplyHidden(bars.Page(4), true);
        bars.ApplyHidden(bars.Page(4), false);
        Check(!hiddenOriginal->RootNode->Visible, "user-hidden bar restored incorrectly");
        hiddenOriginal->RootNode->Visible = true;
        bars.ApplyHidden(bars.Page(4), true);
        Check(!hiddenOriginal->RootNode->Visible, "selected visible HUD wasn't hidden");
        bars.ApplyHidden(bars.Page(4), false);
        Check(hiddenOriginal->RootNode->Visible, "plugin-hidden HUD wasn't restored");
        for (int j = 0; j < 120; j++)
            m->Slots[j].CommandId = 101;
        m->GetSlotById(1, 1)->CommandId = 300;
        m->GetSlotById(3, 2)->CommandId = 300;
        p.PreferredBars = 1 << 1;
        var hidden = gui.Hud + (1 - 4 + 10) % 10;
        hidden->RootNode->Visible = false;
        AddonActionBarBase.Pulses.Clear();
        bars.Pulse(m, p, K.Action, 100, 300, 3000);
        Check(AddonActionBarBase.Pulses.SequenceEqual(new[] { (4, 2) }), "hidden preferred bar blocked visible fallback");
        hidden->RootNode->Visible = true;
        m->GetSlotById(1, 1)->CommandId = 100;
        bars.Pulse(m, p, K.Action, 100, 300, 4000);
        Check(AddonActionBarBase.Pulses.Last() == (4, 2), "preferred opener incorrectly beat an exact skill elsewhere");
        m->GetSlotById(3, 2)->CommandId = 101;
        int beforeMissing = AddonActionBarBase.Pulses.Count;
        bars.Pulse(m, p, K.Action, 100, 300, 4200);
        Check(AddonActionBarBase.Pulses.Count == beforeMissing, "unregistered skill lit the opener for a different action");
        // The same page can be shown twice. A direct key should only mark its own instance.
        int oldPage = gui.Hud[0].RaptureHotbarId;
        gui.Hud[0].RaptureHotbarId = gui.Hud[1].RaptureHotbarId;
        int duplicate = bars.Page(2);
        bars.Reset();
        AddonActionBarBase.Pulses.Clear();
        bars.PulseDirect(2, new(duplicate, 1), 321, 9100);
        Check(AddonActionBarBase.Pulses.Count == 1, "duplicate page produced two native pulses");
        gui.Hud[1].RaptureHotbarId = 0;
        Check(AddonActionBarBase.Pulses.Count == 1, "passive page change triggered input");
        gui.Hud[1].RaptureHotbarId = duplicate - 1;
        gui.Hud[0].RaptureHotbarId = oldPage;

        m->GetSlotById(1, 1)->CommandType = K.GeneralAction;
        m->GetSlotById(1, 1)->CommandId = 4;
        bars.Pulse(m, p, K.Action, 3, 3, 3200);
        Check(AddonActionBarBase.Pulses.Last() == (2, 1), "Sprint has 2 native representations");
        var nativeHudSlot = gui.Hud[0].ActionBarSlotVector.Pointer;
        nativeHudSlot->IconFrame = null;
        nativeHudSlot->Icon = null;
        bars.Reset();
        AddonActionBarBase.Pulses.Clear();
        bars.PulseDirect(1, new(bars.Page(1), 0), 123, 9500);
        Check(AddonActionBarBase.Pulses.Count == 1, "native pulse depended on decorative ImGui geometry");
        RepeatTests();
        InputTests();
        Console.WriteLine($"PASS {checks} assertions");
    }
    static void RepeatTests()
    {
        var clock = new RepeatClock();
        var p = new Preferences { RepeatMs = 120 };
        Check(!clock.Due(0, p, false, true, 1000) && !clock.Due(0, p, false, true, 2000), "disabled repeat sent input");
        p.Turbo = true;
        Check(!clock.Due(0, p, false, false, 3000), "repeat outside configured combat");
        Check(!clock.Due(0, p, false, true, 3100) && !clock.Due(0, p, false, true, 3219) && clock.Due(0, p, false, true, 3220), "first press duplicated or hold delay incorrect");
        Check(!clock.Due(0, p, false, true, 3221) && clock.Due(0, p, false, true, 3340), "repeat cadence");
        Check(!clock.Due(0, p, true, true, 3460) && !clock.Due(0, p, true, true, 3590), "ReAction duplicate request");
        Check(!clock.Due(0, p, false, true, 3600) && !clock.Due(-1, p, false, true, 3720), "release after repeat");
        p.RepeatOutOfCombat = true;
        Check(!clock.Due(3, p, false, false, 4000) && clock.Due(3, p, false, false, 4120), "opt-in outside combat");
        Check(!clock.Due(4, p, false, true, 4250) && !clock.Due(4, p, false, true, 4369), "switching slot reused previous timer");
        clock.Reset();
        Check(!clock.Due(4, p, false, true, 4400), "held input fired immediately after reset");
    }
    static void InputTests()
    {
        var game = Alloc<UIInputData>();
        UIInputData.Current = game;
        game->NumKeybinds = 512;
        game->Keybinds = Alloc<Keybind>(512);
        var entries = Alloc<KeySetting>(1024);
        for (int n = 0; n < 512; n++)
            game->Keybinds[n].KeySettings = entries + n * 2;
        var read = new Hotkey[24];
        var due = new int[10];
        var pages = Enumerable.Range(1, 10).ToArray();
        for (int b = 1; b <= 10; b++)
            for (int i = 0; i < 12; i++)
                for (int mod = 0; mod < 8; mod++)
                {
                    var bind = game->Keybinds + 57 + (b - 1) * 12 + i;
                    bind->KeySettings[0] = new()
                    {
                        Key = (SeVirtualKey)0x41,
                        KeyModifier = (byte)mod
                    };
                    bind->KeySettings[1] = new()
                    {
                        Key = (SeVirtualKey)0x42,
                        KeyModifier = (byte)mod
                    };
                    foreach (int k in new[] { 0x41, 0x42 })
                    {
                        var tracker = new BarInput();
                        UIInputData.Down.Clear();
                        game->CurrentKeyModifier = (byte)mod;
                        UIInputData.Down.Add(k);
                        Check(tracker.Sample(b, pages, due, read, 160, 1000, out int pulse) && pulse == i && due.Count(x => x >= 0) == 1, "initial actual key assignment");
                        Check(tracker.Sample(b, pages, due, read, 160, 1100, out pulse) && pulse == -1, "early repeat");
                        Check(tracker.Sample(b, pages, due, read, 160, 1160, out pulse) && pulse == i, "incomplete held-key repeat");
                        UIInputData.Down.Clear();
                        tracker.Sample(b, pages, due, read, 160, 1320, out pulse);
                        Check(pulse == -1 && tracker.Held == -1 && due.All(x => x == -1), "release repeated");
                    }
                    bind->KeySettings[0] = bind->KeySettings[1] = default;
                }
        var direct = new BarInput();
        var paging = game->Keybinds + 57;
        paging->KeySettings[0] = new()
        {
            Key = (SeVirtualKey)0x31
        };
        game->CurrentKeyModifier = 0;
        UIInputData.Down.Add(0x31);
        pages[0] = 5;
        direct.Sample(5, pages, due, read, 100, 2000, out var mapped);
        Check(mapped == 0 && direct.Held == 0, "paged first bar didn't address actual selected logical slot");
        pages[0] = 4;
        direct.Sample(5, pages, due, read, 100, 2100, out mapped);
        Check(mapped == -1 && due[0] == -1 && direct.Held == -1, "page change while held sent false press");
        UIInputData.Down.Clear();
        direct.Sample(5, pages, due, read, 100, 2150, out _);
        UIInputData.Down.Add(0x31);
        direct.Sample(5, pages, due, read, 100, 2200, out mapped);
        Check(mapped == -1 && due[0] == 0 && direct.Held == -1, "ordinary held bar selected via alias");
        direct.Suspend();
        direct.Sample(5, pages, due, read, 100, 2300, out mapped);
        Check(due.All(x => x == -1), "background-held key triggered on resume");
        game->NumKeybinds = 70;
        Check(!direct.Sample(5, pages, due, read, 100, 2400, out mapped) && mapped == -1 && direct.Held == -1, "partial key table");
        game->NumKeybinds = 512;
        game->CurrentKeyModifier = 0;
        UIInputData.Down.Clear();
        paging->KeySettings[0] = new()
        {
            Key = (SeVirtualKey)0x31,
            KeyModifier = 2
        };
        var consumed = new BarInput();
        var physical = new HashSet<int> { 0x31, 0x11 };
        pages[0] = 1;
        consumed.Sample(1, pages, due, read, 100, 2500, out mapped, physical.Contains);
        Check(mapped == 0 && consumed.Held == 0, "physical held key consumed by another handler");
        consumed.Sample(1, pages, due, read, 100, 2610, out mapped, physical.Contains);
        Check(mapped == 0, "no repeated visual for consumed physical key");
        physical.Clear();
        consumed.Sample(1, pages, due, read, 100, 2720, out mapped, physical.Contains);
        Check(mapped == -1 && consumed.Held == -1, "consumed physical key release");
        var t = new PulseInput();
        var down = new bool[256];
        Array.Clear(read);
        read[0] = new(0x31, 0);
        read[2] = new(0x31, 2);
        down[0x31] = true;
        Check(t.Step(read, down, 2, 1000, 100) == 1 && t.Step(read, down, 2, 1100, 100) == 1, "Ctrl-bound selected slot");
        Check(t.Step(read, down, 0, 1200, 100) == -1, "releasing modifier promoted an unrelated plain hotkey");
        down[0x31] = false;
        t.Step(read, down, 0, 1210, 100);
        down[0x31] = true;
        Check(t.Step(read, down, 0, 1220, 100) == 0, "plain re-press");
        read[0] = new(0x41, 0);
        down[0x41] = true;
        Check(t.Step(read, down, 0, 1400, 100) == -1, "remapped held key triggered a new press");
        Console.WriteLine("input: 10 bars × 12 slots × 8 modifier masks × 2 binding columns; hold/release and chord isolation");
    }
}
