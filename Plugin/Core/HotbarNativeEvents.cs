using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Sound;
using GlobalSound = FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.Delegates.PlaySoundEffect;
using HudSound = FFXIVClientStructs.FFXIV.Client.UI.AddonActionBarBase.Delegates.PlaySoundEffect;
using NativePulse = FFXIVClientStructs.FFXIV.Client.UI.AddonActionBarBase.Delegates.PulseActionBarSlot;

namespace WrathPrivateHotbar;

internal sealed unsafe class HotbarNativeEvents(IGameInteropProvider api, IGameGui gui, Preferences options,
    IPluginLog log, HotbarNativeEvents.SelectedPulse onSelected, Action<Address,long> heardOrdinary) : IDisposable
{
    internal delegate void SelectedPulse(AddonActionBarBase* hud, int index, int slot, long now);
    internal const uint ClickId = 12;
    private readonly nint[] addons = new nint[10];
    private readonly Dictionary<nint, IDisposable> hooks = new();
    private Hook<GlobalSound>? global;
    private long retryAt, sourceMuted, mirrorMuted, unassigned;
    private bool running;
    internal string Status => $"元HBで抑止 {System.Threading.Interlocked.Read(ref sourceMuted)}／表示要求で抑止 {System.Threading.Interlocked.Read(ref mirrorMuted)}／枠不明の同音 {System.Threading.Interlocked.Read(ref unassigned)}（音声経路 {(global == null ? "準備中" : "接続")}）";

    internal void Update(long now, bool active)
    {
        running = active;
        if (!active || now < retryAt) return;
        retryAt = now + 2000;
        for (int b = 0; b < addons.Length; b++)
        {
            var hud = gui.GetAddonByName<AddonActionBarBase>(GameBars.Names[b]);
            addons[b] = (nint)hud;
            if (hud == null || hud->VirtualTable == null) continue;
            try { ListenPulse((nint)hud->VirtualTable->PulseActionBarSlot); }
            catch (Exception e) { log.Debug(e, "Hotbar native pulse"); }
            try { ListenHudSound((nint)hud->VirtualTable->PlaySoundEffect); }
            catch (Exception e) { log.Debug(e, "Hotbar widget click"); }
        }
        if (global != null) return;
        try
        {
            nint address = (nint)UIGlobals.MemberFunctionPointers.PlaySoundEffect;
            if (address == 0) return;
            global = api.HookFromAddress<GlobalSound>(address, GlobalClick);
            global.Enable();
        }
        catch (Exception e) { global?.Dispose(); global = null; log.Debug(e, "Hotbar input sound"); }
    }

    private int KnownIndex(AddonActionBarBase* hud)
    {
        if (!running || hud == null) return -1;
        int i = Array.IndexOf(addons, (nint)hud);
        // During addon recreation the old address can be recycled before our next scan.
        return i >= 0 && gui.GetAddonByName<AddonActionBarBase>(GameBars.Names[i]) == hud ? i : -1;
    }

    private void ListenPulse(nint address)
    {
        if (address == 0 || hooks.ContainsKey(address)) return;
        Hook<NativePulse>? hook = null;
        hook = api.HookFromAddress<NativePulse>(address, (hud, slot) =>
        {
            int index = KnownIndex(hud);
            if (index < 0 || (uint)slot >= 12) { hook!.Original(hud, slot); return; }
            var source = new Address(hud->RaptureHotbarId + 1, slot);
            var fields = hud->ActionBarSlotVector.AsSpan();
            var drag = slot < fields.Length ? fields[slot].ComponentDragDrop : null;
            using var scope = new ButtonAudioScope(source, ButtonAudioScope.Mirrored, drag == null ? 0 : drag->SoundEffectId);
            if (!ButtonAudioScope.Mirrored)
            {
                try { onSelected(hud, index, slot, Environment.TickCount64); }
                catch (Exception e) { log.Debug(e, "Reading the native press frame"); }
            }
            hook!.Original(hud, slot);
        });
        Enable(address, hook);
    }

    private void ListenHudSound(nint address)
    {
        if (address == 0 || hooks.ContainsKey(address)) return;
        Hook<HudSound>? hook = null;
        hook = api.HookFromAddress<HudSound>(address, (hud, id) =>
        {
            int b = KnownIndex(hud);
            if (b < 0)
            {
                using var unrelated = new ButtonAudioScope(default);
                hook!.Original(hud, id);
                return;
            }
            int page = hud->RaptureHotbarId + 1;
            var owner = ButtonAudioScope.Source;
            using var scope = new ButtonAudioScope(new(page, owner.Bar == page ? owner.Slot : -1),
                ButtonAudioScope.Mirrored, owner.Bar == page ? ButtonAudioScope.WidgetSound : 0);
            if (!Suppress((uint)id)) hook!.Original(hud, id);
        });
        Enable(address, hook);
    }

    private void Enable<T>(nint address, Hook<T> hook) where T : Delegate
    {
        try { hook.Enable(); hooks.Add(address, hook); }
        catch { hook.Dispose(); throw; }
    }

    private bool Suppress(uint id)
    {
        if (!running || (id != ClickId && (ButtonAudioScope.WidgetSound <= 0 || id != (uint)ButtonAudioScope.WidgetSound))) return false;
        if (ButtonAudioScope.Mirrored) { System.Threading.Interlocked.Increment(ref mirrorMuted); return true; }
        if (options.MuteSourceClick && options.Show && options.HideOriginal && ButtonAudioScope.Source.Bar == options.Bar)
        { System.Threading.Interlocked.Increment(ref sourceMuted); return true; }
        return false;
    }

    private void GlobalClick(uint id, SoundData** a, SoundData** b, byte flag)
    {
        if (Suppress(id)) return;
        if (running && !presenting && (id == ClickId || ButtonAudioScope.WidgetSound > 0 && id == (uint)ButtonAudioScope.WidgetSound))
        {
            var source = ButtonAudioScope.Source;
            if (source.Valid && source.Bar != options.Bar) heardOrdinary(source, Environment.TickCount64);
            else if (source.Bar == 0) System.Threading.Interlocked.Increment(ref unassigned);
        }
        global!.Original(id, a, b, flag);
    }

    [ThreadStatic] private static bool presenting;
    internal void PlayPresentedClick()
    {
        // The one confirmed visible frame owns this click, even inside another routed callback.
        using var scope = new ButtonAudioScope(default);
        bool previous = presenting;
        presenting = true;
        try { UIGlobals.PlaySoundEffect(ClickId); }
        finally { presenting = previous; }
    }

    public void Dispose()
    {
        running = false;
        foreach (var hook in hooks.Values) hook.Dispose();
        hooks.Clear();
        global?.Dispose(); global = null;
        Array.Clear(addons); retryAt = 0;
    }
}
