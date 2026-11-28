using System.Threading;

namespace WrathPrivateHotbar;

internal readonly record struct Address(int Bar, int Slot)
{
    public bool Valid => Bar is >= 1 and <= 10 && Slot is >= 0 and < 12;
    public override string ToString() => $"HB{Bar}-{Slot + 1}";
}

internal enum PressOrigin { GameSlot, PrivateClick, BuiltInRepeat }

internal static class OnBar
{
    // Each native callback is synchronous. A nested lookup must restore the preceding slot.
    [ThreadStatic] private static Address current;
    [ThreadStatic] private static uint root;
    [ThreadStatic] private static bool insideResolver;
    [ThreadStatic] private static PressOrigin origin;
    internal static PressOrigin Origin { get => origin; set => origin = value; }
    [ThreadStatic] internal static uint ComboResult;
    public readonly struct Scope : IDisposable
    {
        private readonly Address prior;
        private readonly uint priorId, priorResult;
        internal Scope(Address next, uint stored)
        {
            prior = current;
            priorId = root;
            priorResult = ComboResult;
            ComboResult = 0;
            current = next;
            root = stored;
        }
        public void Dispose()
        {
            current = prior;
            root = priorId;
            ComboResult = priorResult;
        }
    }
    internal static Scope Enter(Address address, uint stored) => new(address, stored);
    internal static Address Current => current;
    internal static uint Stored => root;
    internal static bool TryResolve(int selected, uint id, Func<uint, uint>? wrath, out uint result)
    {
        result = 0;
        if (wrath == null || insideResolver || !current.Valid || current.Bar != selected || id == 0 || id != root)
            return false;
        insideResolver = true;
        try
        {
            result = wrath(id);
            return result != 0;
        }
        finally { insideResolver = false; }
    }

    // A remote flash identifies a confirmed action or the current repeat candidate. The combo
    // opener is not a substitute for a missing target. Native level variants match.
    internal static int Rank(int selected, int priorityMask, int bar, int index, bool visible,
        uint requested, uint registered, uint native)
    {
        if (!visible || bar == selected || bar is < 1 or > 10 || index is < 0 or >= 12 ||
            requested == 0 || registered == 0 || requested != native)
            return int.MaxValue;
        int priority = priorityMask == 0 || (priorityMask & (1 << (bar - 1))) != 0 ? 0 : 1;
        int barOrder = priorityMask == 0 ? 0 : bar * 100;
        return priority * 10000 + barOrder + (registered == requested ? 0 : 32) + index;
    }
}

// Context exists only on the thread and for the duration of an actual slot/pulse call.
internal readonly struct ButtonAudioScope : IDisposable
{
    [ThreadStatic] internal static Address Source;
    [ThreadStatic] internal static bool Mirrored;
    [ThreadStatic] internal static int WidgetSound;
    private readonly Address previous;
    private readonly bool previousMirror;
    private readonly int previousSound;
    internal ButtonAudioScope(Address source, bool mirrored = false, int widgetSound = 0)
    {
        previous = Source; previousMirror = Mirrored; previousSound = WidgetSound;
        Source = source; Mirrored = mirrored; WidgetSound = widgetSound;
    }
    public void Dispose() { Source = previous; Mirrored = previousMirror; WidgetSound = previousSound; }
}
