using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using Slot = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlot;
using Kind = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;
using Output = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarUIIntermediate;

namespace WrathPrivateHotbar;

internal sealed unsafe class NativeRouter : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte ById(Module* module, uint bar, uint slot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte BySlot(Module* module, Slot* slot);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Prepare(Module* module, Slot* slot, Output* output);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint Appearance(Kind* kind, uint* action, ushort* state, Module* module, Slot* slot);

    private readonly Preferences options;
    private readonly WrathLink wrath;
    private readonly Func<uint, uint>? actionIcon;
    private readonly Action<Address, uint, uint>? privatePress;
    private readonly Hook<ById> byId;
    private readonly Hook<BySlot> bySlot;
    private readonly Hook<Prepare> prepare;
    private readonly Hook<Appearance>? appearance;
    [ThreadStatic] private static nint dispatching;
    private sealed record Slots(nint Module, Dictionary<nint, Address> Locations);
    private Slots? slots;
    private readonly record struct QueuedUse(Address Source, uint Registered, uint Expected, uint Queued, ulong Target, long Tick, PressOrigin Origin, uint ComboResult);
    private QueuedUse pending;

    internal void ForgetQueued() => pending = default;

    internal void RetireQueued(Address source, uint registered, uint action, long through)
    {
        if (pending.Source == source && pending.Registered == registered && pending.Tick <= through &&
            (pending.Expected == action || pending.Queued == action)) pending = default;
    }

    internal void CheckQueue(long now)
    {
        if (!pending.Source.Valid) return;
        var manager = ActionManager.Instance();
        if (manager == null || !manager->ActionQueued || manager->QueuedActionType != ActionType.Action ||
            manager->QueuedActionId != pending.Queued || (ulong)manager->QueuedTargetId != pending.Target || now - pending.Tick is < 0 or > 2000)
            pending = default;
    }
    // A queue is usable as a preview only while both its producer and the live game queue agree.
    internal uint QueuedFor(Address source, uint registered, long now)
    {
        CheckQueue(now);
        return pending.Source == source && pending.Registered == registered ? pending.Expected : 0;
    }

    internal Address TakeQueued(uint action, ulong target, long now, out uint registered, out PressOrigin origin, out uint comboResult)
    {
        registered = comboResult = 0;
        origin = PressOrigin.GameSlot;
        var last = pending;
        if (!last.Source.Valid) return default;
        if (now - last.Tick is < 0 or > 2000) { pending = default; return default; }
        if ((action != last.Expected && action != last.Queued) ||
            (last.Target != 0 && last.Target != 0xE0000000 && target != last.Target)) return default;
        pending = default;
        registered = last.Registered;
        origin = last.Origin;
        comboResult = last.ComboResult;
        return last.Source;
    }


    internal string LastError { get; private set; } = "";
    internal NativeRouter(IGameInteropProvider api, Preferences options, WrathLink wrath, Func<uint, uint>? actionIcon = null, Action<Address, uint, uint>? privatePress = null)
    {
        this.options = options;
        this.wrath = wrath;
        this.actionIcon = actionIcon;
        this.privatePress = privatePress;
        try
        {
            byId = api.HookFromAddress<ById>((nint)Module.MemberFunctionPointers.ExecuteSlotById, ExecuteId);
            bySlot = api.HookFromAddress<BySlot>((nint)Module.MemberFunctionPointers.ExecuteSlot, ExecutePointer);
            prepare = api.HookFromAddress<Prepare>((nint)Module.MemberFunctionPointers.PrepareSlotForRender, Render);
            // Some game builds inline the appearance lookup. Render also handles a slot
            // that already contains an apparent ID, without depending on this callback.
            try
            {
                if ((nint)Module.MemberFunctionPointers.GetSlotAppearance != 0)
                {
                    appearance = api.HookFromAddress<Appearance>((nint)Module.MemberFunctionPointers.GetSlotAppearance, ReadAppearance);
                    appearance.Enable();
                }
            }
            catch (Exception ex)
            {
                appearance?.Dispose();
                appearance = null;
                LastError = $"外観の個別通知なし（{ex.GetType().Name}）";
            }
            prepare.Enable();
            bySlot.Enable();
            byId.Enable();
        }
        catch { Dispose(); throw; }
    }

    internal void UpdateIndex(Module* module, long now)
    {
        if (module == null || !module->ModuleReady)
        {
            Volatile.Write(ref slots, null);
            return;
        }
        var current = Volatile.Read(ref slots);
        // Slots are embedded in the module. A changed hotbar page or job changes
        // the contents, not the addresses; a different module gets a fresh map.
        if (current != null && current.Module == (nint)module && current.Locations.Count == 120)
            return;
        var locations = new Dictionary<nint, Address>(120);
        for (uint bar = 0; bar < 10; bar++)
            for (uint i = 0; i < 12; i++)
            {
                var slot = module->GetSlotById(bar, i);
                if (slot != null)
                    locations[(nint)slot] = new((int)bar + 1, (int)i);
            }
        Volatile.Write(ref slots, new Slots((nint)module, locations));
    }

    private Address Identify(Module* module, Slot* slot)
    {
        if (slot == null || module == null || !module->ModuleReady)
            return default;
        var current = Volatile.Read(ref slots);
        if (current == null || current.Module != (nint)module ||
            !current.Locations.TryGetValue((nint)slot, out var address))
            return default;
        return module->GetSlotById((uint)(address.Bar - 1), (uint)address.Slot) == slot ? address : default;
    }

    internal uint Standard(uint registered)
    {
        if (registered == 0 || ActionManager.Instance() == null)
            return registered;
        return wrath.Standard(registered);
    }

    internal uint Display(Address address, uint registered) => Display(address, registered, out _);
    internal uint Display(Address address, uint registered, out bool known)
    {
        known = true;
        if (address.Valid && address.Bar == options.Bar && wrath.Ready)
        {
            using var scope = OnBar.Enter(address, registered);
            try
            {
                if (OnBar.TryResolve(options.Bar, registered, wrath.Lookup, out var selected))
                    return selected;
            }
            catch (Exception ex) { LastError = ex.GetType().Name; }
            known = false;
        }
        return Standard(registered);
    }

    internal static bool RegularAppearance(Slot* slot) => slot != null &&
        slot->CommandType == Kind.Action && slot->CommandId != 0 &&
        slot->ApparentSlotType == Kind.Action &&
        slot->ApparentActionMode is HotbarSlotApparentActionMode.AdjustedAction or HotbarSlotApparentActionMode.None;

    private uint Icon(Slot* slot, uint action)
    {
        uint id = actionIcon?.Invoke(action) ?? 0;
        if (id != 0) return id;
        int native = slot->GetIconIdForSlot(Kind.Action, action);
        return native > 0 ? (uint)native : 0;
    }

    private uint ReadAppearance(Kind* kind, uint* action, ushort* state, Module* module, Slot* slot)
    {
        wrath.EnsureNative();
        uint result = appearance!.Original(kind, action, state, module, slot);
        var address = Identify(module, slot);
        if (!address.Valid || address.Bar == options.Bar || kind == null || *kind != Kind.Action ||
            action == null || !RegularAppearance(slot))
            return result;
        // In some clients the return value and out parameter are used by different callers.
        uint native = Standard(slot->CommandId);
        if (native == 0) return result;
        *action = native;
        return native;
    }

    private void Render(Module* module, Slot* slot, Output* output)
    {
        wrath.EnsureNative();
        var address = Identify(module, slot);
        bool ordinary = address.Valid && address.Bar != options.Bar;
        uint native = 0;
        if (ordinary && RegularAppearance(slot))
        {
            native = Standard(slot->CommandId);
            if (native != 0)
                slot->ApparentActionId = native;
        }

        using (OnBar.Enter(default, 0))
            prepare.Original(module, slot, output);
        if (!address.Valid || output == null || !RegularAppearance(slot))
            return;

        uint wanted = ordinary ? (native != 0 ? native : Standard(slot->CommandId)) : Display(address, slot->CommandId);
        if (wanted == 0) return;
        uint icon = Icon(slot, wanted);
        if (ordinary)
        {
            // Both cache consumers (e.g. tooltips) and the outgoing HUD data get the
            // native result. Registered CommandId and OriginalApparentActionId stay intact.
            slot->ApparentActionId = wanted;
            if (icon != 0) slot->IconId = icon;
        }
        if (output->ActionId == wanted && (icon == 0 || output->IconId == icon)) return;
        output->ActionId = wanted;
        if (icon != 0) output->IconId = icon;
        output->CostValue = slot->GetCostValueForSlot(Kind.Action, wanted);
        output->DrawAnts = slot->IsActionHighlighted(Kind.Action, wanted);
    }

    private byte ExecuteId(Module* module, uint bar, uint index)
    {
        var address = bar < 10 && index < 12 ? new Address((int)bar + 1, (int)index) : default;
        var slot = address.Valid && module != null && module->ModuleReady ? module->GetSlotById(bar, index) : null;
        return Dispatch(module, slot, address, bar, index, true);
    }

    private byte ExecutePointer(Module* module, Slot* slot)
    {
        if (slot != null && dispatching == (nint)slot)
        {
            wrath.EnsureNative();
            return bySlot.Original(module, slot);
        }
        return Dispatch(module, slot, Identify(module, slot), 0, 0, false);
    }

    private byte Dispatch(Module* module, Slot* slot, Address address, uint bar, uint index, bool numbered)
    {
        wrath.EnsureNative();
        using var audio = new ButtonAudioScope(address);
        bool action = slot != null && slot->CommandType == Kind.Action;
        bool sprint = slot != null && GameBars.Sprint(slot->CommandType, slot->CommandId);
        if (slot == null || slot->CommandId == 0 || (!action && !sprint))
            address = default;
        uint registered = address.Valid ? slot->CommandId : 0;
        using var scope = OnBar.Enter(address, registered);
        // Never exchange a command on an ordinary bar. Let FF14 apply its own
        // adjustments, queuing and targeting to the original registered action.
        bool selected = action && address.Valid && address.Bar == options.Bar && wrath.Ready;
        bool known = false;
        uint use = selected ? Display(address, registered, out known) : registered;
        if (known && use != registered) OnBar.ComboResult = use;
        if (address.Valid && address.Bar == options.Bar) privatePress?.Invoke(address, registered, sprint ? 3u : use);
        bool replace = selected && use != 0 && use != registered;
        bool appearanceChanged = replace && slot->ApparentSlotType == Kind.Action;
        uint oldAppearance = appearanceChanged ? slot->ApparentActionId : 0;
        var previous = dispatching;
        dispatching = (nint)slot;
        try
        {
            if (replace) slot->CommandId = use;
            if (appearanceChanged) slot->ApparentActionId = use;
            if (address.Valid && !selected && RegularAppearance(slot))
                slot->ApparentActionId = Standard(registered);
            var manager = ActionManager.Instance();
            uint previousQueue = manager != null && manager->ActionQueued ? manager->QueuedActionId : 0;
            ulong previousTarget = previousQueue == 0 ? 0 : (ulong)manager->QueuedTargetId;
            byte result = numbered ? byId.Original(module, bar, index) : bySlot.Original(module, slot);
            // Queued casts can be accepted on a later frame, after key release.
            // Track only a queue that this real dispatch can be responsible for.
            if (address.Valid && manager != null && manager->ActionQueued && manager->QueuedActionType == ActionType.Action)
            {
                uint queued = manager->QueuedActionId;
                uint expected = sprint ? 3u : selected ? use : Standard(registered);
                ulong target = manager->QueuedTargetId;
                if (queued != 0 && (queued == registered || queued == expected))
                {
                    if (queued != previousQueue || target != previousTarget)
                        pending = new(address, registered, expected, queued, target, Environment.TickCount64, OnBar.Origin, OnBar.ComboResult);
                    else if (pending.Source.Valid && pending.Source != address && result != 0)
                        pending = default;
                }
            }
            return result;
        }
        finally
        {
            if (replace) slot->CommandId = registered;
            if (appearanceChanged) slot->ApparentActionId = oldAppearance;
            dispatching = previous;
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref slots, null);
        byId?.Dispose();
        bySlot?.Dispose();
        prepare?.Dispose();
        appearance?.Dispose();
    }
}
