using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace WrathPrivateHotbar;

internal readonly record struct Hotkey(int Key, int Modifiers)
{
    internal bool Matches(int mask) => Key > 0 && Key < 256 && Modifiers == mask;
    internal string Label
    {
        get
        {
            if (Key == 0)
                return "";
            string name = Key switch
            {
                >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)Key).ToString(),
                0x80 => "NumLock", 0x81 => "Scroll",
                >= 0x70 and <= 0x87 => $"F{Key - 0x6F}",
                >= 0x60 and <= 0x69 => $"Num{Key - 0x60}",
                0xBD => "-",
                0xDE => "^",
                0xBB => "+",
                0x05 => "M4",
                0x06 => "M5",
                0x20 => "Sp",
                0xBA => ";", 0xBC => ",", 0xBE => ".", 0xBF => "/",
                0xDB => "[", 0xDD => "]", 0xDC => "\\",
                0x08 => "Back", 0x09 => "Tab", 0x0D => "Enter", 0x1B => "Esc",
                0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
                _ => Enum.GetName((SeVirtualKey)Key) ?? ""
            };
            if (name.Length == 0) return "";
            return ((Modifiers & 2) != 0 ? "C+" : "") + ((Modifiers & 1) != 0 ? "S+" : "") + ((Modifiers & 4) != 0 ? "A+" : "") + name;
        }
    }
}

internal sealed class PulseInput
{
    private readonly long[] pressed = new long[12];
    private readonly bool[] physical = new bool[24], eligible = new bool[24];
    private readonly Hotkey[] priorBindings = new Hotkey[24];
    private bool suppressOldPress, hasSample;
    private int previous = -1;
    private long next;
    internal int Held { get; private set; } = -1;
    internal int HeldMask { get; private set; }
    internal bool NewHold
    {
        get; private set;
    }
    internal long StartedAt => Held < 0 ? 0 : pressed[Held];
    internal void Reset()
    {
        Array.Clear(pressed);
        Array.Clear(physical);
        Array.Clear(eligible);
        previous = Held = -1;
        HeldMask = 0;
        next = 0;
        NewHold = false;
        Array.Clear(priorBindings);
        suppressOldPress = hasSample = false;
    }

    internal void Suspend()
    {
        Reset();
        suppressOldPress = true;
    }
    // Each of the 12 slots has two bindings. A key that began as Ctrl+1 cannot turn into
    // a fresh plain 1 just because Ctrl was released. Another matching held chord can resume.
    internal int Step(ReadOnlySpan<Hotkey> keys, ReadOnlySpan<bool> down, int modifiers, long now, int interval, ReadOnlySpan<byte> keyModifiers = default)
    {
        int chosen = -1;
        HeldMask = 0;
        for (int i = 0; i < 12; i++)
        {
            bool live = false;
            for (int k = 2 * i; k < 2 * i + 2; k++)
            {
                var b = keys[k];
                bool held = b.Key > 0 && b.Key < down.Length && down[b.Key];
                bool chord = held && b.Matches(keyModifiers.IsEmpty ? modifiers : keyModifiers[b.Key]);
                bool rebound = hasSample && priorBindings[k] != b;
                priorBindings[k] = b;
                if (!chord || rebound || suppressOldPress)
                    eligible[k] = false;
                else if (!physical[k])
                {
                    eligible[k] = true;
                    pressed[i] = now;
                }
                physical[k] = held;
                live |= eligible[k];
            }
            if (live) HeldMask |= 1 << i;
            if (live && (chosen < 0 || pressed[i] > pressed[chosen]))
                chosen = i;
        }
        suppressOldPress = false;
        hasSample = true;
        Held = chosen;
        NewHold = chosen >= 0 && chosen != previous;
        previous = chosen;
        if (chosen < 0)
        {
            next = 0;
            return -1;
        }
        if (!NewHold && now < next)
            return -1;
        next = now + Math.Max(1, interval);
        return chosen;
    }

}

internal readonly record struct RepeatChord(int Binding, Hotkey Key, int Page, long Stroke);

// Bindings belong to the on-screen bar control. The control can show another page.
internal sealed class BarInput
{
    private readonly PulseInput[] trackers = Enumerable.Range(0, 10).Select(_ => new PulseInput()).ToArray();
    private readonly Hotkey[] bindings = new Hotkey[240];
    private readonly bool[] down = new bool[256];
    private readonly bool[] desktop = new bool[256], previousDesktop = new bool[240], eligible = new bool[240];
    private readonly long[] strokes = new long[240];
    private bool suspendDesktop;
    private readonly bool[] desktopStroke = new bool[256];
    private readonly int[] previousPages = new int[10];
    private readonly byte[] keyModifiers = new byte[256];
    private int owner = -1;
    private bool pagesKnown;
    internal int Held => owner < 0 ? -1 : trackers[owner].Held;
    internal long StartedAt => owner < 0 ? 0 : trackers[owner].StartedAt;
    internal int HeldMask { get; private set; }

    internal void Suspend()
    {
        foreach (var tracker in trackers)
            tracker.Suspend();
        owner = -1;
        HeldMask = 0;
        pagesKnown = false;
        suspendDesktop = true;
        Array.Clear(eligible);
    }

    internal unsafe bool Sample(int selected, ReadOnlySpan<int> pages, Span<int> due, Span<Hotkey> labels,
        int interval, long now, out int selectedPulse, Func<int, bool>? physicalDown = null)
    {
        selectedPulse = -1;
        owner = -1;
        HeldMask = 0;
        due.Fill(-1);
        labels.Clear();
        var input = UIInputData.Instance();
        int first = (int)InputId.HOTBAR_1_1;
        if (input == null || input->Keybinds == null || input->NumKeybinds < first + 120)
        {
            Suspend();
            return false;
        }
        Array.Clear(down);
        Array.Clear(desktop);
        Span<bool> sampled = stackalloc bool[256];
        sampled.Clear();
        int gameMods = (int)input->CurrentKeyModifier & 7;
        // ReAction and other input handlers can consume the game's key state.
        // Only query the three modifiers and assigned keys while FF14 accepts hotbar input.
        int physicalMods = physicalDown == null ? 0 : DesktopModifiers(physicalDown);
        int labelBar = -1;
        for (int b = 0; b < 10; b++)
        {
            var row = bindings.AsSpan(b * 24, 24);
            Keybind* record = null;
            for (int k = 0; k < 24; k++)
            {
                if ((k & 1) == 0)
                {
                    int id = first + b * 12 + k / 2;
                    record = (nint)InputData.MemberFunctionPointers.GetKeybind != 0
                        ? input->GetKeybind((InputId)id) : input->Keybinds + id;
                }
                var binding = record == null ? default : record->KeySettings[k % 2];
                int key = (byte)binding.Key;
                var old = row[k];
                row[k] = new(key, (int)binding.KeyModifier);
                if (key != 0 && !sampled[key])
                {
                    sampled[key] = true;
                    desktop[key] = physicalDown?.Invoke(key) == true;
                    bool gameDown = input->IsKeyDown(binding.Key) || input->IsKeyPressed(binding.Key);
                    // After Windows observed this stroke, a game-side pressed flag can outlive
                    // key-up. Wait for both sources to clear before accepting game-only input again.
                    if (desktop[key]) desktopStroke[key] = true;
                    else if (!gameDown) desktopStroke[key] = false;
                    down[key] = desktop[key] || (gameDown && !desktopStroke[key]);
                    // The main key and all its modifiers must come from the same input source.
                    keyModifiers[key] = (byte)(desktop[key] ? physicalMods : gameMods);
                }
                // Actual repeat can only continue the same complete desktop chord on the same page.
                // A modifier released and reapplied during one main-key stroke cannot rearm it.
                int col = b * 24 + k;
                bool pressed = desktop[key], fresh = pressed && !previousDesktop[col];
                if (fresh) strokes[col]++;
                if (!pressed || suspendDesktop || (pagesKnown && (old != row[k] || previousPages[b] != pages[b])) ||
                    !row[k].Matches(physicalMods)) eligible[col] = false;
                else if (fresh) eligible[col] = true;
                previousDesktop[col] = pressed;
            }
            int page = pages[b];
            if (pagesKnown && previousPages[b] != page)
                trackers[b].Suspend();
            previousPages[b] = page;
            due[b] = trackers[b].Step(row, down, gameMods, now, interval, keyModifiers);
            if (page != selected)
                continue;
            HeldMask |= trackers[b].HeldMask;
            if (labelBar < 0 || b + 1 == selected)
                labelBar = b;
            if (trackers[b].Held >= 0 && (owner < 0 || trackers[b].StartedAt > trackers[owner].StartedAt ||
                trackers[b].StartedAt == trackers[owner].StartedAt && b + 1 == selected))
                owner = b;
        }
        pagesKnown = true;
        suspendDesktop = false;
        if (labelBar >= 0)
            bindings.AsSpan(labelBar * 24, 24).CopyTo(labels);
        if (owner >= 0)
            selectedPulse = due[owner];
        return true;
    }
    private static int DesktopModifiers(Func<int, bool> read) =>
        (read(0x10) ? 1 : 0) | (read(0x11) ? 2 : 0) | (read(0x12) ? 4 : 0);

    internal bool StillDown(int slot, Func<int, bool> physicalDown) =>
        owner >= 0 && Held == slot && BoundKeyDown(owner, slot, physicalDown);

    internal bool PrivateKeyDown(int slot, Func<int, bool> physicalDown)
    {
        int bit = 1 << slot;
        if (owner < 0 || (HeldMask & bit) == 0) return false;
        for (int b = 0; b < trackers.Length; b++)
            if (previousPages[b] == previousPages[owner] && (trackers[b].HeldMask & bit) != 0 &&
                BoundKeyDown(b, slot, physicalDown)) return true;
        return false;
    }

    private unsafe bool BoundKeyDown(int bar, int slot, Func<int, bool> physicalDown)
    {
        var game = UIInputData.Instance();
        for (int k = bar * 24 + slot * 2; k < bar * 24 + slot * 2 + 2; k++)
        {
            var binding = bindings[k];
            int key = binding.Key;
            if (key == 0 || !down[key] || !binding.Matches(keyModifiers[key])) continue;
            if (desktopStroke[key])
            {
                if (physicalDown(key) && binding.Matches(DesktopModifiers(physicalDown))) return true;
            }
            else if (game != null && binding.Matches((int)game->CurrentKeyModifier & 7) &&
                (game->IsKeyDown((SeVirtualKey)key) || game->IsKeyPressed((SeVirtualKey)key))) return true;
        }
        return false;
    }

    // Called at the first real game slot dispatch. No combination of game and OS states
    // can authorize a second execution. Track both key assignments and paged HUDs.
    internal RepeatChord AtDispatch(Address source, Func<int, bool> read)
    {
        if (!pagesKnown || !source.Valid) return default;
        int mods = DesktopModifiers(read);
        for (int n = 0; n < 10; n++)
        {
            int b = (source.Bar - 1 + n) % 10;
            if (previousPages[b] != source.Bar) continue;
            for (int k = b * 24 + source.Slot * 2; k < b * 24 + source.Slot * 2 + 2; k++)
            {
                var key = bindings[k];
                if (key.Matches(mods) && read(key.Key) && (!previousDesktop[k] || eligible[k]))
                    return new(k, key, source.Bar, strokes[k] + (previousDesktop[k] ? 0 : 1));
            }
        }
        return default;
    }

    internal bool Continues(RepeatChord chord) => chord.Page != 0 && pagesKnown &&
        previousPages[chord.Binding / 24] == chord.Page && bindings[chord.Binding] == chord.Key &&
        eligible[chord.Binding] && strokes[chord.Binding] == chord.Stroke;

    // FF14 can assign an identical chord to a selected and an ordinary slot. Once a
    // selected use owns that chord, its repeat has just one destination.
    internal bool SharesSelectedChord(int physicalBar, int slot)
    {
        if (owner < 0 || Held < 0) return false;
        var ordinary = bindings.AsSpan(physicalBar * 24 + slot * 2, 2);
        var selected = bindings.AsSpan(owner * 24 + Held * 2, 2);
        foreach (var key in ordinary)
            if (key.Matches(keyModifiers[key.Key]) && down[key.Key] && (selected[0] == key || selected[1] == key))
                return true;
        return false;
    }

}

internal sealed class RepeatClock
{
    private int owner = -1;
    private long next;
    internal void Reset()
    {
        owner = -1;
        next = 0;
    }
    internal bool Due(int held, Preferences options, bool external, bool inCombat, long now)
    {
        if (held < 0 || !options.Turbo || external || (!options.RepeatOutOfCombat && !inCombat))
        {
            Reset();
            return false;
        }
        if (owner != held)
        {
            owner = held;
            next = now + options.RepeatMs;
            return false;
        }
        if (now < next)
            return false;
        next = now + options.RepeatMs;
        return true;
    }
}
