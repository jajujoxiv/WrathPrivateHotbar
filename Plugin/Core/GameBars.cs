using System.Text;
using Bounds = FFXIVClientStructs.FFXIV.Common.Math.Bounds;
using Dalamud.Plugin.Services;
using Lumina.Text.ReadOnly;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Module = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule;
using Kind = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace WrathPrivateHotbar;

internal interface IOrdinaryPressArt
{
    void BeforePulse(int source, int addon, Address button, long now);
    bool VisibleOrdinaryPress(int addon, Address button);
    void PrepareRepeat(int addon, Address button, long age);
    bool FinishRepeat(int addon, int slot, bool drawn);
}

internal sealed unsafe class GameBars(IGameGui gui, Func<uint, uint> standard, Func<uint, uint, bool>? learnedForm = null,
    IOrdinaryPressArt? art = null, Func<int, bool>? stillDown = null)
{
    // Addon names identify HUD instances. RaptureHotbarId identifies the logical hotbar
    // that instance currently displays; paging HB1 can produce duplicate references.
    internal static readonly string[] Names = Enumerable.Range(0, 10).Select(i => i == 0 ? "_ActionBar" : $"_ActionBar{i:00}").ToArray();
    internal static bool Visible(AddonActionBarBase* a) => a != null && a->IsVisible && a->RootNode != null && a->RootNode->IsVisible();
    internal static bool Sprint(Kind kind, uint id) => kind == Kind.GeneralAction && id == 4 || kind == Kind.Action && id == 3;
    internal int Page(int bindingBar) => Page(bindingBar, out _);
    internal int Page(int bindingBar, out int pulsing)
    {
        var a = gui.GetAddonByName<AddonActionBarBase>(Names[bindingBar - 1]);
        pulsing = a == null ? 0 : (ushort)a->CurrentPulsingSlots & ((1 << Math.Min(12, (int)a->SlotCount)) - 1);
        if (a == null)
            return bindingBar;
        int page = a->RaptureHotbarId + 1;
        return page is >= 1 and <= 10 ? page : 0;
    }

    internal AddonActionBarBase* SelectedHud(int selected) => SelectedHud(selected, out _);

    internal AddonActionBarBase* SelectedHud(int selected, out int index)
    {
        index = selected - 1;
        var hud = gui.GetAddonByName<AddonActionBarBase>(Names[index]);
        if (hud != null && hud->RaptureHotbarId + 1 == selected) return hud;
        for (index = 0; index < Names.Length; index++)
        {
            var candidate = gui.GetAddonByName<AddonActionBarBase>(Names[index]);
            if (candidate != null && candidate->RaptureHotbarId + 1 == selected) return candidate;
        }
        index = -1;
        return null;
    }

    internal static void ReadKeyLabels(Module* module, int selected, AddonActionBarBase* hud, string[] labels)
    {
        int count = hud == null ? 0 : Math.Min((int)hud->SlotCount, hud->ActionBarSlotVector.AsSpan().Length);
        for (int i = 0; i < labels.Length; i++)
        {
            // The text node belongs to the game's currently displayed page. It also
            // retains the actual F-key / modifier glyphs while its parent is hidden.
            string text = "";
            if (i < count)
            {
                var hint = hud->ActionBarSlotVector.AsSpan()[i].ControlHintTextNode;
                if (hint != null) text = ReadHint(hint->NodeText.AsSpan());
            }
            var slot = module->GetSlotById((uint)(selected - 1), (uint)i);
            if (text.Length == 0 && slot != null) text = ReadHint(slot->PopUpKeybindHint);
            if (text.Length == 0 && slot != null) text = ReadHint(slot->KeybindHint);
            labels[i] = text;
        }
    }

    private static string ReadHint(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end >= 0) bytes = bytes[..end];
        if (bytes.IsEmpty || bytes.Length > 160) return "";
        string text = (bytes.Contains((byte)2)
            ? new ReadOnlySeStringSpan(bytes).ExtractText()
            : Encoding.UTF8.GetString(bytes)).Trim();
        if (text.Length >= 2 && text[0] == '[' && text[^1] == ']') text = text[1..^1].Trim();
        if (text.Length > 32) return "";
        foreach (char c in text)
            if (char.IsControl(c) || c == '�') return "";
        return text;
    }

    private readonly record struct Flash(int Page, long Tick, nint Hud, nint Root, nint Button, uint Registered, int PrivateSlot, uint Action, bool Restart);
    private readonly record struct DrawnBar(nint Addon, nint Root, int Page);
    private readonly DrawnBar[] drawnBars = new DrawnBar[10];
    private readonly int[] pending = new int[10], presented = new int[10];
    private readonly int[] confirmed = new int[10], tails = new int[10], prepared = new int[10], toStart = new int[10];
    internal long Drawn { get; private set; }
    internal long Clicks { get; private set; }
    private readonly Flash[,] recentPulses = new Flash[10, 12];
    private readonly long[,] nativeClicks = new long[10,12];
    internal void HeardOrdinaryClick(Address source, long now)
    {
        if (source.Valid) nativeClicks[source.Bar-1,source.Slot] = now;
    }

    private readonly nint[] hiddenRoots = new nint[10];
    internal long Requests
    {
        get; private set;
    }
    internal long Matched
    {
        get; private set;
    }
    internal string Last { get; private set; } = "入力待ち";

    internal bool Pulse(Module* m, Preferences p, Kind type, uint source, uint requested, long now, int privateSlot = -1, bool newPress = false)
    {
        if (!p.Pulse || m == null || !m->ModuleReady || source == 0 || requested == 0)
            return false;
        Requests++;
        int best = int.MaxValue, targetIndex = -1;
        AddonActionBarBase* target = null;
        Address chosen = default;
        for (int addonIndex = 0; addonIndex < 10; addonIndex++)
        {
            var a = gui.GetAddonByName<AddonActionBarBase>(Names[addonIndex]);
            if (!Visible(a))
                continue;
            int bar = a->RaptureHotbarId + 1;
            if (bar == p.Bar || bar is < 1 or > 10)
                continue;
            var slots = a->ActionBarSlotVector.AsSpan();
            int limit = Math.Min(12, Math.Min((int)a->SlotCount, slots.Length));
            for (int i = 0; i < limit; i++)
            {
                var s = m->GetSlotById((uint)(bar - 1), (uint)i);
                if (s == null || s->CommandId == 0)
                    continue;
                uint stored = s->CommandId, displayed = stored;
                uint want = requested;
                if (Sprint(type, source))
                {
                    if (!Sprint(s->CommandType, stored))
                        continue;
                    want = stored = displayed = 3;
                }
                else if (s->CommandType != type)
                    continue;
                else if (type == Kind.Action)
                {
                    displayed = !NativeRouter.RegularAppearance(s) && s->ApparentSlotType == Kind.Action && s->ApparentActionId != 0
                        ? s->ApparentActionId : standard(stored);
                }
                else if (type != Kind.Action)
                    want = source;
                int score = OnBar.Rank(p.Bar, p.PreferredBars, bar, i, true, want, stored, displayed);
                // Some native upgrades revert immediately after the resource is spent. Match the
                // registered button only by its own ID or a native variant of that exact button.
                if (score == int.MaxValue && type == Kind.Action && NativeRouter.RegularAppearance(s))
                {
                    bool sameButton = want == stored || EnchantedForm(stored) == want || learnedForm?.Invoke(stored, want) == true;
                    if (sameButton)
                        score = 100000 + OnBar.Rank(p.Bar, p.PreferredBars, bar, i, true, want, stored, want);
                }
                if (score >= best || ButtonOnScreen(a, i) == 0)
                    continue;
                best = score;
                target = a;
                targetIndex = addonIndex;
                chosen = new(bar, i);
            }
        }
        if (target == null)
        {
            Last = $"ID {requested} に一致する表示中の枠なし";
            return false;
        }
        return Light(target, targetIndex, chosen, requested, now, m->GetSlotById((uint)(chosen.Bar - 1), (uint)chosen.Slot)->CommandId, privateSlot, best >= 100000, newPress);
    }

    internal static uint EnchantedForm(uint root) => root switch
    {
        7504 => 7527, 7512 => 7528, 7516 => 7529, _ => 0
    };

    internal bool PulseDirect(int bindingBar, Address source, uint action, long now)
    {
        if (!source.Valid || action == 0)
            return false;
        Requests++;
        for (int step = 0; step < 10; step++)
        {
            int index = (bindingBar - 1 + step) % 10;
            var a = gui.GetAddonByName<AddonActionBarBase>(Names[index]);
            if (!Visible(a) || a->RaptureHotbarId + 1 != source.Bar || source.Slot >= a->SlotCount ||
               source.Slot >= a->ActionBarSlotVector.AsSpan().Length)
                continue;
            if (ButtonOnScreen(a, source.Slot) == 0) continue;
            var module = Module.Instance();
            var slot = module == null ? null : module->GetSlotById((uint)(source.Bar - 1), (uint)source.Slot);
            if (slot == null || slot->CommandId == 0) continue;
            return Light(a, index, source, action, now, slot->CommandId);
        }
        Last = $"{source} は非表示";
        return false;
    }

    // Check the ordinary button itself. An addon may draw while an individual slot,
    // a parent group, or the entire HUD is transparent or outside the viewport.
    private static nint ButtonOnScreen(AddonActionBarBase* hud, int slot)
    {
        if (!Visible(hud) || hud->Alpha == 0 || hud->RootNode->Color.RGBA >> 24 == 0) return 0;
        var field = hud->ActionBarSlotVector.AsSpan()[slot];
        var button = field.Icon;
        if ((button == null || button->AtkResNode.Width == 0 || button->AtkResNode.Height == 0) && field.ComponentDragDrop != null)
            button = field.ComponentDragDrop->OwnerNode;
        if (button == null) return 0;
        var node = &button->AtkResNode;
        if (node->Width == 0 || node->Height == 0) return 0;
        int depth = 0;
        for (var parent = node; parent != null; parent = parent->ParentNode)
            if (!parent->IsVisible() || parent->Color.RGBA >> 24 == 0 || ++depth > 64) return 0;
        Bounds box = default;
        node->GetBounds(&box);
        if (box.Width <= 0 || box.Height <= 0 || box.Pos2.X <= 0 || box.Pos2.Y <= 0) return 0;
        var stage = FFXIVClientStructs.FFXIV.Component.GUI.AtkStage.Instance();
        if (stage != null && stage->ScreenSize.Width > 0 && stage->ScreenSize.Height > 0 &&
            (box.Pos1.X >= stage->ScreenSize.Width || box.Pos1.Y >= stage->ScreenSize.Height)) return 0;
        return (nint)button;
    }

    private bool Light(AddonActionBarBase* target, int index, Address chosen, uint action, long now,
        uint registered, int privateSlot = -1, bool nativeForm = false, bool newPress = false)
    {
        int slot = chosen.Slot, bit = 1 << slot;
        nint button = ButtonOnScreen(target, slot);
        if (button == 0) return false;
        var previous = recentPulses[index, slot];
        bool same = previous.Page == chosen.Bar && previous.Hud == (nint)target &&
            previous.Root == (nint)target->RootNode && previous.Button == button && previous.Registered == registered;
        bool active = ((ushort)target->CurrentPulsingSlots & bit) != 0;
        if (drawnBars[index] != new DrawnBar((nint)target, (nint)target->RootNode, chosen.Bar))
            presented[index] = confirmed[index] = 0;
        if (!active) presented[index] &= ~bit;
        if (same && (now - previous.Tick is >= 0 and < 45 ||
            (pending[index] & bit) != 0 && !(newPress && previous.Restart) && now - previous.Tick is >= 0 and <= 180)) return true;
        bool restart = active && newPress && same && privateSlot >= 0 && previous.PrivateSlot == privateSlot &&
            (confirmed[index] & bit) != 0;
        // A native animation already on screen belongs to its original press. Only a new
        // attributable input beat may restart one previously presented for this source.
        if (!restart && (presented[index] & bit) != 0) return true;
        recentPulses[index, slot] = new(chosen.Bar, now, (nint)target, (nint)target->RootNode, button,
            registered, privateSlot, action, restart);
        pending[index] |= bit;
        if (!restart) confirmed[index] &= ~bit;
        if (restart) tails[index] |= bit;
        else tails[index] &= ~bit;
        Matched++;
        Last = $"{chosen}（ID {action}・{(nativeForm ? "純正の登録枠" : "一致")}）";
        if (privateSlot >= 0 && art != null)
            toStart[index] |= bit; // a release or accepted action can cancel this before it is drawn
        else
            StartNative(target, index, chosen, privateSlot, !active, now);
        return true;
    }

    private void StartNative(AddonActionBarBase* hud, int index, Address button, int privateSlot, bool animate, long now)
    {
        art?.BeforePulse(privateSlot, index, button, now);
        if (!animate) return;
        using var audio = new ButtonAudioScope(button, mirrored: true);
        hud->PulseActionBarSlot(button.Slot);
    }

    // Use the actual native icon for a second onset when the game's preceding press
    // is still lit. All per-frame color adjustments are bracketed by this addon's draw.
    internal void BeforeDraw(int index, AddonActionBarBase* hud, Module* module, int selected, long now)
    {
        if ((uint)index >= 10) return;
        FinishFrame(index, false);
        if ((tails[index] | toStart[index]) == 0 || !Visible(hud) || hud->Alpha == 0 || module == null || !module->ModuleReady) return;
        int page = hud->RaptureHotbarId + 1;
        int count = Math.Min(12, Math.Min((int)hud->SlotCount, hud->ActionBarSlotVector.AsSpan().Length));
        for (int i = 0; i < 12; i++)
        {
            int bit = 1 << i;
            if (((tails[index] | toStart[index]) & bit) == 0) continue;
            var f = recentPulses[index, i];
            long age = now - f.Tick;
            var slot = page == f.Page && page != selected ? module->GetSlotById((uint)(page-1), (uint)i) : null;
            bool start = (toStart[index] & bit) != 0;
            toStart[index] &= ~bit;
            if (start && stillDown?.Invoke(f.PrivateSlot) == false || age is < 0 or > 180 || i >= count || slot == null || slot->CommandId != f.Registered ||
                f.Hud != (nint)hud || f.Root != (nint)hud->RootNode || ButtonOnScreen(hud,i) != f.Button)
            {
                pending[index] &= ~bit;
                tails[index] &= ~bit;
                continue;
            }
            if (start)
                StartNative(hud, index, new(page,i), f.PrivateSlot,
                    f.Restart || (hud->CurrentPulsingSlots & bit) == 0, now);
            if ((tails[index] & bit) == 0) continue;
            art?.PrepareRepeat(index, new(page, i), age);
            prepared[index] |= bit;
        }
    }

    private int FinishFrame(int index, bool drawn)
    {
        int mask = prepared[index], risen = 0;
        prepared[index] = 0;
        for (int i = 0; mask != 0; i++, mask >>= 1)
            if ((mask & 1) != 0 && art?.FinishRepeat(index,i,drawn) == true) risen |= 1 << i;
        return risen;
    }

    internal void AfterDraw(int index, AddonActionBarBase* hud, Module* module, int selected, long now, bool sound, Action click)
    {
        if ((uint)index >= 10) return;
        if (!Visible(hud) || hud->Alpha == 0 || hud->RaptureHotbarId + 1 == selected || module == null || !module->ModuleReady)
        {
            DiscardDraw(index);
            return;
        }
        int risen = FinishFrame(index, true);
        int active = (ushort)hud->CurrentPulsingSlots;
        drawnBars[index] = new((nint)hud, (nint)hud->RootNode, hud->RaptureHotbarId + 1);
        presented[index] = 0;
        confirmed[index] &= active;
        if ((pending[index] | active) == 0) return;
        int count = Math.Min(12, Math.Min((int)hud->SlotCount, hud->ActionBarSlotVector.AsSpan().Length));
        for (int i = 0; i < 12; i++)
        {
            int bit = 1 << i;
            bool waiting = (pending[index] & bit) != 0;
            if (!waiting && (active & bit) == 0) continue;
            nint visible = i >= count ? 0 : ButtonOnScreen(hud, i);
            if (visible != 0 && (active & bit) != 0) presented[index] |= bit;
            if (!waiting) continue;
            var p = recentPulses[index, i];
            var slot = module->GetSlotById((uint)(p.Page - 1), (uint)i);
            if (i >= count || p.Hud != (nint)hud || p.Root != (nint)hud->RootNode || p.Page != hud->RaptureHotbarId + 1 ||
                now - p.Tick is < 0 or > 180 || visible != p.Button || slot == null || slot->CommandId != p.Registered)
            {
                pending[index] &= ~bit;
                continue;
            }
            if (p.Restart ? (risen & bit) == 0 :
                (active & bit) == 0 || art != null && !art.VisibleOrdinaryPress(index, new(p.Page, i))) continue;
            confirmed[index] |= bit;
            pending[index] &= ~bit;
            Drawn++;
            long heard = nativeClicks[p.Page-1,i];
            nativeClicks[p.Page-1,i] = 0;
            if (sound && (heard == 0 || heard < p.Tick - 45 || heard > now)) { click(); Clicks++; }
        }
    }

    internal void CancelWaiting(int sources, uint action = 0)
    {
        for (int b = 0; b < 10; b++)
        {
            int mask = pending[b];
            for (int i = 0; mask != 0; i++, mask >>= 1)
            {
                if ((mask & 1) == 0) continue;
                var f = recentPulses[b,i];
                if (f.PrivateSlot < 0 || (sources & (1 << f.PrivateSlot)) == 0 || action != 0 && f.Action != action) continue;
                pending[b] &= ~(1 << i);
                toStart[b] &= ~(1 << i);
                tails[b] &= ~(1 << i);
            }
        }
    }

    internal void DiscardDraw(int index)
    {
        if ((uint)index >= 10) return;
        FinishFrame(index, false);
        pending[index] = presented[index] = confirmed[index] = tails[index] = toStart[index] = 0;
    }

    // Some native layouts reuse their icon widget across frames without running
    // the appearance helper again. Inspect the actual game widget immediately before
    // this addon draws, using the page it currently represents.
    internal static void PrepareNormalHud(AddonActionBarBase* hud, Module* module, int selected,
        WrathLink wrath, Func<uint, uint> iconForAction, Action<uint, uint>? remember = null)
    {
        if (!Visible(hud) || module == null || !module->ModuleReady) return;
        int bar = hud->RaptureHotbarId + 1;
        if (bar == selected || bar is < 1 or > 10) return;
        wrath.EnsureNative();
        var fields = hud->ActionBarSlotVector.AsSpan();
        int count = Math.Min(12, Math.Min((int)hud->SlotCount, fields.Length));
        for (int i = 0; i < count; i++)
        {
            var slot = module->GetSlotById((uint)(bar - 1), (uint)i);
            if (!NativeRouter.RegularAppearance(slot)) continue;
            var drag = fields[i].ComponentDragDrop;
            var widget = drag == null ? null : drag->AtkComponentIcon;
            if (widget == null && fields[i].Icon != null)
                widget = fields[i].Icon->GetAsAtkComponentIcon();
            if (widget == null) continue;
            uint native = wrath.Standard(slot->CommandId);
            if (native != 0 && native != slot->CommandId) remember?.Invoke(slot->CommandId, native);
            uint icon = iconForAction(native);
            if (icon != 0 && widget->IconId != icon) widget->LoadIcon(icon);
        }
    }

    internal void ApplyHidden(int selected, bool hide)
    {
        for (int i = 0; i < 10; i++)
        {
            var a = gui.GetAddonByName<AddonActionBarBase>(Names[i]);
            var root = a == null ? null : a->RootNode;
            if (hiddenRoots[i] != (nint)root)
                hiddenRoots[i] = 0;
            if (root == null)
                continue;
            if (hide && a->RaptureHotbarId + 1 == selected)
            {
                if (a->IsVisible && root->IsVisible())
                {
                    root->ToggleVisibility(false);
                    hiddenRoots[i] = (nint)root;
                }
            }
            else if (hiddenRoots[i] != 0)
            {
                root->ToggleVisibility(true);
                hiddenRoots[i] = 0;
            }
        }
    }
    internal void Reset()
    {
        for (int b = 0; b < 10; b++) DiscardDraw(b);
        Array.Clear(recentPulses);
        Array.Clear(pending);
        Array.Clear(presented);
        Array.Clear(drawnBars);
        Array.Clear(nativeClicks);
    }
}
