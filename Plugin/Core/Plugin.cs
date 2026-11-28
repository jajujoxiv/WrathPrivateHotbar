using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Slot = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using Module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using Kind = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace WrathPrivateHotbar;

public sealed unsafe partial class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IFramework framework;
    private readonly IGameInteropProvider api;
    private readonly IClientState client;
    private readonly IPlayerState player;
    private readonly ICondition condition;
    private readonly IGameGui gui;
    private readonly IAddonLifecycle lifecycle;
    private readonly IDataManager data;
    private readonly ITextureProvider textures;
    private readonly IPluginLog log;
    private readonly Preferences p;
    private readonly string path;
    private readonly WrathLink link;
    private readonly GameBars bars;
    private readonly BarInput input = new();
    private readonly Hotkey[] keys = new Hotkey[24];
    private int previousHeldMask, previousNativeMask;
    private bool sampledNative;
    private readonly record struct PressAttempt(Address Source, uint Registered, uint Resolved, uint Job, long Tick, RepeatChord Chord);
    private PressAttempt repeatAttempt;
    private PressAttempt lastAttempt;
    private readonly record struct CastPress(Address Source, uint Registered, uint Id, ulong Target, long Began, long End, bool ReceiptDue, ulong PreviewTarget);
    private CastPress castPress, previousCast;
    private readonly ConcurrentQueue<PressAttempt> privateAttempts = new();
    private readonly long[] lastActionPulse = new long[12];
    private readonly SlotView[] view = new SlotView[12];
    private NativeRouter? router;
    private Hook<UsedAction>? usedHook;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte UsedAction(ActionManager* m, ActionType type, uint action, ulong target, Vector3* location, uint extra, byte flags);
    private readonly record struct ActionUse(uint Id, Address Position, uint Stored, long Tick, uint Job, int SelectedBar, ushort Sequence, ulong Target, ulong PreviewTarget);
    private readonly record struct HeldFeedback(Address Source, uint Registered, uint LastAccepted = 0,
        ulong Target = 0, long AcceptedAt = 0, uint PreviewAfter = 0, uint PreviewId = 0, uint Trying = 0, uint Consumed = 0, bool SawLock = false);
    private readonly SequencePreview sequences = new();
    private readonly NativePressArt nativeArt;
    private readonly PressSurface pressSurface;
    private readonly HotbarNativeEvents nativeEvents;
    private long nextProbe, mouseBeat;
    private int contactMask, releasedSlots, freshAttempts, handledAttempts;
    private string previewStatus = "入力待ち", lastLead = "未観測";
    private readonly record struct Authority(Address Source, uint Registered, uint Running, ulong Target);
    private Authority authoritativeDuring;
    private HeldFeedback heldFeedback;
    private readonly string[] nativeLabels = new string[12];
    private readonly HudTypography[] typography = new HudTypography[12];
    private readonly IFontHandle? labelFont, timerFont, valueFont;
    private uint lastReceiptAction;
    private ushort lastReceiptSequence;
    private long lastReceiptTime;
    [ThreadStatic] private static bool observingUse;
    private readonly ConcurrentQueue<ActionUse> uses = new();
    private readonly Dictionary<uint, uint> actionIcons = new();
    private readonly Func<uint, uint> iconForAction;
    private readonly Action<uint, uint> rememberNative;
    private readonly Dictionary<uint, (int Count, uint Icon, string Name)> observed = new();
    private readonly HashSet<uint> installedActions = new();
    private readonly Dictionary<uint, long> absentSince = new();
    private readonly Dictionary<uint, HashSet<uint>> seenVariants = new();
    private uint job;
    private int clicked = -1;
    private uint clickedId;
    private Kind clickedKind;
    private int clickedBar;
    private long scanAt, linkAt, retryAt, auditScanAt;
    private readonly RepeatClock repeatClock = new();
    private bool ready, interactive, settings, textEntry, externalRepeat;
    private string lastSwiftcast = "未観測";
    private string status = "ログイン待ち", lastNative = "未観測", saveError = "";
    private readonly record struct SlotView(Kind Kind, uint Stored, uint Display, uint Icon, float Remaining, float Total, uint Cost, SlotCostType Resource, string? CostText, int Charges, int Quantity, bool Highlight);
    private static readonly string[] Dialogs = ["Talk", "SelectString", "SelectIconString", "SelectYesno", "SelectOk", "CutSceneSelectString"];

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IFramework framework,
        IGameInteropProvider interop, IClientState client, IPlayerState player, ICondition condition,
        IGameGui gui, IDataManager data, ITextureProvider textures, IPluginLog log, IAddonLifecycle lifecycle,
        ITextureReadbackProvider readback)
    {
        pi = pluginInterface;
        commands = commandManager;
        this.framework = framework;
        api = interop;
        this.client = client;
        this.player = player;
        this.condition = condition;
        this.gui = gui;
        this.data = data;
        iconForAction = GetActionIcon;
        rememberNative = RememberNative;
        this.textures = textures;
        this.log = log;
        labelFont = LoadFont(GameFontFamilyAndSize.MiedingerMid36);
        timerFont = LoadFont(GameFontFamilyAndSize.TrumpGothic34);
        valueFont = LoadFont(GameFontFamilyAndSize.Axis36);
        path = Path.Combine(pi.GetPluginConfigDirectory(), "preferences-2.0.0.json");
        try
        {
            p = Preferences.Load(path);
        }
        catch (Exception e) { p = new(); saveError = "設定の読み込みに失敗しました: " + e.Message; }
        if (p.Migrated) Save();
        link = new(pi);
        nativeArt = new(gui, textures, readback);
        pressSurface = new(textures);
        bars = new(gui, link.Standard, (root, action) => seenVariants.TryGetValue(root, out var known) && known.Contains(action), nativeArt, MayPresent);
        nativeEvents = new(api, gui, p, log, BeforeNativePress, bars.HeardOrdinaryClick);
        this.lifecycle = lifecycle;
        lifecycle.RegisterListener(AddonEvent.PreDraw, GameBars.Names, BeforeBarDraw);
        lifecycle.RegisterListener(AddonEvent.PostDraw, GameBars.Names, AfterBarDraw);
        commands.AddHandler("/wprivate", new CommandInfo(Command) { HelpMessage = "config / show / edit / lock / unlock" });
        pi.ActivePluginsChanged += PluginsChanged;
        framework.Update += Update;
        pi.UiBuilder.Draw += Draw;
        pi.UiBuilder.OpenConfigUi += Open;
        pi.UiBuilder.OpenMainUi += Open;
    }

    private void Command(string cmd, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "show":
                p.Show = true;
                Save();
                break;
            case "edit":
            case "unlock":
                p.Locked = false;
                p.Show = true;
                Save();
                break;
            case "lock":
                p.Locked = true;
                Save();
                break;
            case "":
                settings = !settings;
                break;
            default:
                settings = true;
                break;
        }
    }

    private bool MayPresent(int slot) => (contactMask & (1 << slot)) == 0 ||
        pressedSlot == slot && pressedBar == p.Bar || input.StillDown(slot, DesktopInput.PhysicalDown);

    private void AfterBarDraw(AddonEvent _, AddonArgs args)
    {
        int index = Array.IndexOf(GameBars.Names, args.AddonName);
        if (!p.Pulse || !ready || !interactive || !client.IsLoggedIn || args.PreventOriginalRequested)
        {
            bars.DiscardDraw(index);
            return;
        }
        bars.AfterDraw(index, args.GetAddonPointer<AddonActionBarBase>(), Module.Instance(), p.Bar,
            Environment.TickCount64, p.Sound, nativeEvents.PlayPresentedClick);
    }

    private void BeforeNativePress(AddonActionBarBase* hud, int index, int slot, long now)
    {
        if (!ready || !interactive) return;
        if (hud->RaptureHotbarId + 1 != p.Bar)
        {
            nativeArt.BeforePulse(-1, index, new(hud->RaptureHotbarId + 1, slot), now);
            return;
        }
        if (!p.Show || (releasedSlots & (1 << slot)) != 0 || view[slot].Stored == 0) return;
        nativeArt.BeforeSelectedPulse(hud, index, slot, now);
        MarkPress(slot, now, PressSignal.Native);
    }

    private void BeforeBarDraw(AddonEvent _, AddonArgs args)
    {
        if (!interactive || !ready || !client.IsLoggedIn) return;
        var hud = args.GetAddonPointer<AddonActionBarBase>();
        if (hud == null) return;
        if (hud->RaptureHotbarId + 1 == p.Bar)
        {
            if (p.Show && p.HideOriginal) args.PreventOriginal();
            return;
        }
        var module = Module.Instance();
        GameBars.PrepareNormalHud(hud, module, p.Bar, link, iconForAction, rememberNative);
        if (p.Pulse && !args.PreventOriginalRequested)
            bars.BeforeDraw(Array.IndexOf(GameBars.Names,args.AddonName), hud, module, p.Bar, Environment.TickCount64);
    }

    private uint GetActionIcon(uint action)
    {
        if (action == 0) return 0;
        if (actionIcons.TryGetValue(action, out uint icon)) return icon;
        try { icon = data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(action)?.Icon ?? 0u; }
        catch (Exception ex)
        {
            if (lastViewError != ex.GetType())
            {
                lastViewError = ex.GetType();
                log.Debug(ex, "Hotbar action icon");
            }
            return 0;
        }
        if (icon != 0) actionIcons[action] = icon;
        return icon;
    }

    private void PluginsChanged(IActivePluginsChangedEventArgs change)
    {
        if (change.AffectedInternalNames.Any(n => n.Equals("WrathCombo", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("ReAction", StringComparison.OrdinalIgnoreCase)))
            link.Invalidate();
    }
    private void Open() => settings = true;
    private void Save()
    {
        try
        {
            p.Save(path);
            saveError = "";
        }
        catch (Exception ex) { saveError = ex.Message; }
    }

    private byte OnUsed(ActionManager* m, ActionType type, uint id, ulong target, Vector3* location, uint extra, byte flags)
    {
        if (observingUse) return usedHook!.Original(m, type, id, target, location, extra, flags);
        observingUse = true;
        try
        {
            byte result = usedHook!.Original(m, type, id, target, location, extra, flags);
            if (result == 0 || type != ActionType.Action || id == 0 || !ready)
                return result;
            long now = Environment.TickCount64;
            var source = OnBar.Current;
            uint registered = OnBar.Stored;
            var origin = OnBar.Origin;
            uint combo = OnBar.ComboResult;
            bool queued = false;
            if (!source.Valid && router != null)
            {
                source = router.TakeQueued(id, target, now, out registered, out origin, out combo);
                queued = source.Valid;
            }
            // A held-slot match below is visual attribution only. Report evidence before that guess.
            if (id == 7561)
            {
                bool matches = registered == id || combo == id;
                string from = !source.Valid ? "枠外・呼出元不明" : !matches ? "枠処理中・迅速魔の呼出元不明" : origin switch
                {
                    PressOrigin.PrivateClick => "疑似HBクリック",
                    PressOrigin.BuiltInRepeat => "WPH内蔵連打",
                    _ => "ゲーム／他プラグインの枠操作"
                };
                string route = source.Valid ? $"　{source}　登録 {registered} → {id}" : "";
                string replace = combo == id ? "（選択HBのコンボ置換）" : "";
                bool shift = DesktopInput.PhysicalDown(0x10), minus = DesktopInput.PhysicalDown(0xBD);
                Volatile.Write(ref lastSwiftcast, $"{DateTime.Now:HH:mm:ss}　{from}{route}{replace}{(queued ? "　先行入力" : "")}　受付時 Shift:{(shift ? "押" : "離")} / -:{(minus ? "押" : "離")}");
            }
            if (uses.Count >= 256) return result;
            // A direct repeater may bypass the game slot. Bind its accepted action while
            // it still matches the held slot, before the next update refreshes the display.
            int held = input.Held;
            if (!source.Valid && held >= 0 && view[held].Kind == Kind.Action && view[held].Display == id)
            {
                source = new(p.Bar, held);
                registered = view[held].Stored;
            }
            if (source.Valid) router?.RetireQueued(source, registered, id, now);
            uses.Enqueue(new(id, source, registered, now, job, p.Bar, m == null ? (ushort)0 : m->LastUsedActionSequence, target, TargetForPreview(target)));
            return result;
        }
        finally { observingUse = false; }
    }
    private void ResetInteraction()
    {
        input.Suspend();
        sequences.Reset();
        previewStatus = "入力待ち";
        lastLead = "未観測";
        nextProbe = mouseBeat = 0;
        router?.ForgetQueued();
        heldFeedback = default;
        lastReceiptSequence = 0;
        lastReceiptTime = 0;
        lastReceiptAction = 0;
        clicked = pressedSlot = -1;
        repeatClock.Reset();
        contactMask = releasedSlots = freshAttempts = 0;
        Array.Clear(lastActionPulse);
        bars.Reset();
        ResetPrivatePresses();
    }
    private void Leave()
    {
        nativeEvents.Dispose();
        if (!ready && router == null && usedHook == null)
        {
            return;
        }
        ready = false;
        retryAt = linkAt = 0;
        ResetInteraction();
        usedHook?.Dispose();
        usedHook = null;
        router?.Dispose();
        router = null;
        bars.ApplyHidden(p.Bar, false);
        uses.Clear();
        observed.Clear();
        installedActions.Clear();
        absentSince.Clear();
        seenVariants.Clear();
        Array.Clear(view);
        Array.Clear(keys);
        Array.Clear(nativeLabels);
        job = 0;
    }
    private void Update(IFramework _)
    {
        long now = Environment.TickCount64;
        try
        {
            if (!client.IsLoggedIn)
            {
                interactive = false;
                Volatile.Write(ref lastSwiftcast, "未観測");
                Leave();
                link.Release();
                status = "ログイン待ち";
                return;
            }
            // HUD visibility and ownership do not depend on Wrath or the input hooks.
            bool changingArea = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51];
            bool visible = !changingArea && !IsHidden();
            if (interactive && !visible)
                ResetInteraction();
            interactive = visible;
            if (now >= linkAt || link.Invalidated)
            {
                if (link.Invalidated)
                {
                    ResetInteraction();
                    uses.Clear();
                }
                link.Refresh();
                externalRepeat = pi.InstalledPlugins.Any(x => x.IsLoaded && x.InternalName.Equals("ReAction", StringComparison.OrdinalIgnoreCase));
                linkAt = now + 750;
            }
            else
                link.Maintain();

            var m = player.IsLoaded && !changingArea ? Module.Instance() : null;
            if (m == null || !m->ModuleReady)
            {
                Leave();
                status = "FF14のホットバーを読み込み中";
                return;
            }
            bars.ApplyHidden(p.Bar, p.Show && p.HideOriginal && interactive);
            ready = true;
            uint newJob = player.ClassJob.RowId;
            if (newJob != job)
            {
                job = newJob;
                ResetInteraction();
                uses.Clear();
                observed.Clear();
                installedActions.Clear();
                absentSince.Clear();
                seenVariants.Clear();
                Array.Clear(view);
                scanAt = auditScanAt = 0;
            }
            if (now >= retryAt && (router == null || usedHook == null))
            {
                retryAt = now + 4000;
                try
                {
                    router ??= new NativeRouter(api, p, link, iconForAction, OnPrivatePress);
                }
                catch (Exception ex) { log.Debug(ex, "Hotbar routing initialization"); }
                if (usedHook == null)
                {
                    try
                    {
                        var observer = api.HookFromAddress<UsedAction>((nint)ActionManager.MemberFunctionPointers.UseActionLocation, OnUsed);
                        usedHook = observer;
                        observer.Enable();
                    }
                    catch (Exception ex) { usedHook?.Dispose(); usedHook = null; log.Debug(ex, "Action observer initialization"); }
                }
            }
            router?.UpdateIndex(m, now);
            router?.CheckQueue(now);
            if (now >= scanAt)
            {
                scanAt = now + 65;
                var hud = bars.SelectedHud(p.Bar);
                GameBars.ReadKeyLabels(m, p.Bar, hud, nativeLabels);
                RefreshView(m);
                ReadTypography(hud, m);
            }
            if (router != null && now >= auditScanAt)
            {
                auditScanAt = now + 250;
                RefreshAssignments(m);
            }
            bool inputAllowed = interactive && DesktopInput.Foreground() && !textEntry;
            var stage = AtkStage.Instance();
            inputAllowed &= stage != null && stage->AtkInputManager != null && !stage->AtkInputManager->IsTextInputActive;
            nativeEvents.Update(now, inputAllowed);
            if (!inputAllowed)
            {
                bars.Reset();
                Drain(m, false);
                sequences.Break();
                nextProbe = mouseBeat = 0;
                input.Suspend();
                heldFeedback = default;
                contactMask = releasedSlots = freshAttempts = 0;
                repeatClock.Reset();
                clicked = pressedSlot = -1;
                ResetPrivatePresses();
                status = link.Status;
                return;
            }
            Span<int> pages = stackalloc int[10];
            Span<int> due = stackalloc int[10];
            int nativePressed = 0;
            for (int b = 0; b < 10; b++)
            {
                pages[b] = bars.Page(b + 1, out int pulsing);
                if (pages[b] == p.Bar) nativePressed |= pulsing;
            }
            int interval = p.PulseMin == p.PulseMax ? p.PulseMin : Random.Shared.Next(p.PulseMin, p.PulseMax + 1);
            bool keysAvailable = input.Sample(p.Bar, pages, due, keys, interval, now, out var pulse, DesktopInput.PhysicalDown);
            int edges = input.HeldMask & ~previousHeldMask;
            int nativeEdges = sampledNative ? nativePressed & ~previousNativeMask : 0;
            int mouse = pressedSlot >= 0 && p.Show && pressedBar == p.Bar && !dragging &&
                pressedId == view[pressedSlot].Stored && pressedKind == view[pressedSlot].Kind ? pressedSlot : -1;
            int visualHeld = input.Held >= 0 ? input.Held : mouse;
            int contact = input.HeldMask | (mouse >= 0 ? 1 << mouse : 0);
            int started = contact & ~contactMask;
            int ended = contactMask & ~contact;
            if (heldFeedback.Source.Valid && ((started | ended) & (1 << heldFeedback.Source.Slot)) != 0)
                heldFeedback = heldFeedback with { Trying = 0, Consumed = 0, SawLock = false };
            releasedSlots = (releasedSlots | ended) & ~contact;
            contactMask = contact;
            freshAttempts = handledAttempts = 0;
            if (ended != 0)
            {
                bars.CancelWaiting(ended);
                previewStatus = "キーを離したため連打表示を停止";
                nextProbe = 0;
            }
            if (mouse < 0) mouseBeat = 0;
            else if (input.Held < 0 && now >= mouseBeat)
            {
                pulse = mouse;
                mouseBeat = now + interval;
            }
            for (int i = 0; i < 12; i++)
            {
                if (view[i].Stored == 0) continue;
                if ((nativeEdges & ~releasedSlots & (1 << i)) != 0) MarkPress(i, now, PressSignal.NativeEdge);
                else if ((edges & (1 << i)) != 0) MarkPress(i, now, PressSignal.Contact);
                else if (pulse == i) MarkPress(i, now, PressSignal.HeldBeat);
            }
            previousHeldMask = input.HeldMask;
            previousNativeMask = nativePressed;
            sampledNative = true;
            status = router == null ? "FF14の枠を表示中・コンボ実行の準備待ち" : link.Status;
            if (!keysAvailable)
                status += " ／ キー割り当て待ち";
            // The cast and queue can identify a destination. Only current contact authorizes repeated presses.
            if (!p.Pulse) heldFeedback = default;
            else if (visualHeld >= 0) Own(m, visualHeld);
            if (p.Pulse)
            {
                for (int b = 0; b < 10; b++)
                {
                    if (pages[b] == p.Bar || pages[b] == 0 || due[b] < 0)
                        continue;
                    if (heldFeedback.Source.Valid && heldFeedback.Source == new Address(p.Bar, input.Held) && input.SharesSelectedChord(b, due[b]))
                        continue;
                    var address = new Address(pages[b], due[b]);
                    var standard = m->GetSlotById((uint)(address.Bar - 1), (uint)address.Slot);
                    if (standard != null && standard->CommandId != 0)
                    {
                        bars.PulseDirect(b + 1, address, standard->CommandId, now);
                        if (visualHeld < 0)
                        {
                            heldFeedback = default;
                            pulse = -1;
                        }
                    }
                }
            }
            if (clicked >= 0)
            {
                int index = clicked;
                clicked = -1;
                var slot = m->GetSlotById((uint)(p.Bar - 1), (uint)index);
                if (p.Show && clickedBar == p.Bar && slot != null && slot->CommandId == clickedId && slot->CommandType == clickedKind)
                {
                    ExecuteSelected(m, index, PressOrigin.PrivateClick);
                }
            }
            now = Environment.TickCount64;
            int attempt = ApplyPrivatePresses(m, now);
            bool blocked = !p.Turbo || externalRepeat || router == null || !link.Ready ||
                (!p.RepeatOutOfCombat && !condition[ConditionFlag.InCombat]);
            if (blocked || repeatAttempt.Source.Bar != p.Bar || !input.Continues(repeatAttempt.Chord))
                repeatAttempt = default;
            int held = repeatAttempt.Source.Valid ? repeatAttempt.Source.Slot : -1;
            if (held >= 0)
            {
                var repeat = m->GetSlotById((uint)(p.Bar - 1), (uint)held);
                if (repeat == null || repeat->CommandId != repeatAttempt.Registered || repeat->CommandType != Kind.Action)
                {
                    repeatAttempt = default;
                    held = -1;
                }
            }
            if (repeatClock.Due(held, p, blocked, condition[ConditionFlag.InCombat], now))
            {
                ExecuteSelected(m, held, PressOrigin.BuiltInRepeat);
                int repeated = ApplyPrivatePresses(m, Environment.TickCount64);
                if (repeated >= 0) attempt = repeated;
            }
            ObserveCast(m, now);
            Drain(m, true);
            now = Environment.TickCount64;
            if (p.Pulse && visualHeld >= 0 && heldFeedback.Source == new Address(p.Bar, visualHeld) &&
                (pulse >= 0 || now >= nextProbe) && now - lastActionPulse[visualHeld] >= 45)
            {
                nextProbe = now + 32;
                PulseNext(m, visualHeld, now, pulse >= 0);
            }
            else if (p.Pulse && visualHeld < 0 && attempt >= 0 && (handledAttempts & (1 << attempt)) == 0 && now - lastActionPulse[attempt] >= 45)
            {
                // An external dispatch with no readable held key is one observed press, never a running clock.
                if (Own(m, attempt))
                    PulseNext(m, attempt, now, true, lastAttempt.Resolved);
            }
            if (p.Show) nativeArt.Update(bars, p.Bar, contactMask, now);
        }
        catch (Exception ex)
        {
            status = $"処理を待機中（{ex.GetType().Name}）";
            input.Suspend();
            repeatClock.Reset();
            clicked = -1;
            log.Debug(ex, "Hotbar update");
        }
    }
    private void ExecuteSelected(Module* module, int slot, PressOrigin origin)
    {
        var previous = OnBar.Origin;
        OnBar.Origin = origin;
        try { module->ExecuteSlotById((uint)(p.Bar - 1), (uint)slot); }
        finally { OnBar.Origin = previous; }
    }

    private uint SelectedAction(int i, Slot* slot) => SelectedAction(i, slot, out _);
    private uint SelectedAction(int i, Slot* slot, out bool known)
    {
        known = true;
        if (slot->CommandType != Kind.Action)
            return slot->CommandId;
        uint display = router != null ? router.Display(new(p.Bar, i), slot->CommandId, out known) : link.Standard(slot->CommandId);
        if (display == slot->CommandId && slot->ApparentSlotType == Kind.Action && slot->ApparentActionId != 0 &&
            !NativeRouter.RegularAppearance(slot))
            return slot->ApparentActionId;
        return display;
    }
    private void RememberNative(uint registered, uint variant)
    {
        if (!seenVariants.TryGetValue(registered, out var forms))
            seenVariants.Add(registered, forms = []);
        forms.Add(variant);
    }

    private void RefreshAssignments(Module* m)
    {
        installedActions.Clear();
        for (int b = 1; b <= 10; b++)
            if (b != p.Bar)
                for (int i = 0; i < 12; i++)
                {
                    var s = m->GetSlotById((uint)(b - 1), (uint)i);
                    if (s == null || s->CommandId == 0)
                        continue;
                    if (GameBars.Sprint(s->CommandType, s->CommandId))
                        installedActions.Add(3);
                    if (s->CommandType != Kind.Action)
                        continue;
                    installedActions.Add(s->CommandId);
                    uint native = router!.Standard(s->CommandId);
                    if (native != 0 && native != s->CommandId)
                        RememberNative(s->CommandId, native);
                    uint enchanted = GameBars.EnchantedForm(s->CommandId);
                    if (enchanted != 0) installedActions.Add(enchanted);
                    if (seenVariants.TryGetValue(s->CommandId, out var history))
                        installedActions.UnionWith(history);
                }
        long now = Environment.TickCount64;
        foreach (var id in observed.Keys)
            if (installedActions.Contains(id))
                absentSince.Remove(id);
            else
                absentSince.TryAdd(id, now);
    }
    private void RefreshView(Module* m)
    {
        var am = ActionManager.Instance();
        for (int i = 0; i < 12; i++)
        {
            var s = m->GetSlotById((uint)(p.Bar - 1), (uint)i);
            if (s == null || s->CommandId != view[i].Stored || s->CommandType != view[i].Kind)
            {
                nativeArt.ResetSlot(i);
            }
            RefreshSlot(i, s, am);
        }
    }

    private void RefreshSlot(int i, Slot* s, ActionManager* am, uint display = 0)
    {
        view[i] = default;
        if (s == null || s->CommandId == 0)
            return;
        var kind = s->CommandType;
        view[i] = new(kind, s->CommandId, s->CommandId, s->IconId, 0, 0, 0, default, null, -1, -1, false);
        if (am == null)
            return;
        try
        {
            view[i] = ReadSlot(s, am, kind, display != 0 ? display : SelectedAction(i, s));
        }
        catch (Exception ex)
        {
            // The cached ID/icon above still make the slot visible and clickable.
            if (lastViewError != ex.GetType())
            {
                lastViewError = ex.GetType();
                log.Debug(ex, "Optional hotbar icon details");
            }
        }
    }

    private Type? lastViewError;
    private SlotView ReadSlot(Slot* s, ActionManager* am, Kind kind, uint id)
    {
        uint icon = 0, cost = 0;
        SlotCostType resource = default;
        string? costText = null;
        int qty = -1, charges = -1;
        float left = 0, total = 0;
        bool hi = false;
        if (kind == Kind.Action)
        {
            var row = data.GetExcelSheet<Lumina.Excel.Sheets.Action>().GetRowOrDefault(id);
            icon = row?.Icon ?? 0u;
            if (icon != 0)
            {
                cost = s->GetCostValueForSlot(kind, id);
                // Prefer the live slot's type only when the client priced this very action.
                // MP (including all-MP spells) can also be identified from the displayed Action row.
                bool nativeCost = s->ApparentSlotType == kind && s->ApparentActionId == id && s->CostDisplayMode is 1 or 2;
                if (nativeCost)
                    resource = (SlotCostType)s->CostType;
                else if (row?.PrimaryCostType is 3 or 4)
                    resource = SlotCostType.Magic;
                else if (cost > 0 && Slot.MemberFunctionPointers.GetCostTypeForSlot != null)
                    resource = s->GetCostTypeForSlot(kind, id);
                if (nativeCost && s->CostDisplayMode == 2)
                    costText = s->CostTextString;
                else if (row?.PrimaryCostType == 4 && Slot.MemberFunctionPointers.GetCostTextForSlot != null)
                    costText = s->GetCostTextForSlot(kind, id).ToString();
                hi = am->IsActionHighlighted(ActionType.Action, id);
                if (ActionManager.GetMaxCharges(id, 0) > 1)
                    charges = (int)am->GetCurrentCharges(id);
            }
        }
        else if (kind == Kind.Item)
        {
            uint item = s->CommandId % 1_000_000;
            icon = data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(item)?.Icon ?? 0u;
            var inv = InventoryManager.Instance();
            if (inv != null)
                qty = inv->GetInventoryItemCount(item, s->CommandId >= 1_000_000, false, false);
        }
        if (icon == 0)
        {
            int native = s->GetIconIdForSlot(s->ApparentSlotType, s->ApparentActionId);
            icon = native > 0 ? (uint)native : s->IconId;
        }
        if (p.ShowCooldown && (kind == Kind.Action || kind == Kind.Item || GameBars.Sprint(kind, id)))
        {
            var type = kind == Kind.Item ? ActionType.Item : ActionType.Action;
            uint recast = kind == Kind.Item ? id % 1_000_000 : GameBars.Sprint(kind, id) ? 3 : id;
            HotbarCooldownInfo info = default;
            am->GetHotbarCooldownInfo(&info, type, recast);
            if (info.CooldownActive && float.IsFinite(info.PrimaryTotalTime) && float.IsFinite(info.PrimaryElapsedTime))
            {
                total = info.PrimaryTotalTime;
                left = Math.Clamp(total - info.PrimaryElapsedTime, 0, Math.Max(0, total));
            }
        }
        return new(kind, s->CommandId, id, icon, left, total, cost, resource, costText, charges, qty, hi);
    }
    private bool IsHidden()
    {
        if (client.TerritoryType == 1359 || pi.UiBuilder.CutsceneActive || gui.GameUiHidden ||
            condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] ||
            condition[ConditionFlag.OccupiedInCutSceneEvent])
            return true;
        if (!condition[ConditionFlag.OccupiedInEvent] && !condition[ConditionFlag.OccupiedInQuestEvent])
            return false;
        foreach (var name in Dialogs)
        {
            var ui = gui.GetAddonByName<AtkUnitBase>(name);
            if (ui != null && ui->IsVisible && ui->RootNode != null && ui->RootNode->IsVisible())
                return true;
        }
        return false;
    }
    public void Dispose()
    {
        pi.ActivePluginsChanged -= PluginsChanged;
        framework.Update -= Update;
        pi.UiBuilder.Draw -= Draw;
        pi.UiBuilder.OpenConfigUi -= Open;
        pi.UiBuilder.OpenMainUi -= Open;
        lifecycle.UnregisterListener(AddonEvent.PostDraw, GameBars.Names, AfterBarDraw);
        lifecycle.UnregisterListener(AddonEvent.PreDraw, GameBars.Names, BeforeBarDraw);
        commands.RemoveHandler("/wprivate");
        pressSurface.Dispose();
        nativeArt.Dispose();
        labelFont?.Dispose();
        timerFont?.Dispose();
        valueFont?.Dispose();
        interactive = false;
        Leave();
        link.Release();
    }
}
