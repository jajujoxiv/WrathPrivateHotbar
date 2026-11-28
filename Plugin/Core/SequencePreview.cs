namespace WrathPrivateHotbar;

// Only accepted, attributable uses train this table. A prediction is never fed back into it.
internal sealed class SequencePreview
{
    internal readonly record struct Source(Address Slot, uint Registered, ulong Target);
    private readonly record struct Prefix(Source Source, uint Previous, uint Current);
    private readonly record struct Outcome(uint Next, byte Count);
    private readonly Dictionary<Prefix, Outcome> outcomes = new();
    private Source source;
    private uint previous, current;
    private long lastUse;
    private const int MaxGap = 8000;

    internal void Accept(Source nextSource, uint action, long now)
    {
        if (Usable(nextSource) && source == nextSource && now - lastUse is >= 0 and <= MaxGap)
        {
            if (previous != 0)
            {
                var prefix = new Prefix(source, previous, current);
                if (outcomes.TryGetValue(prefix, out var seen))
                    outcomes[prefix] = seen.Next == action ? new(action, (byte)Math.Min(3, seen.Count + 1)) : default;
                else if (outcomes.Count < 512)
                    outcomes.Add(prefix, new(action, 1));
            }
            previous = current;
        }
        else previous = 0;
        source = nextSource;
        current = action;
        lastUse = now;
    }

    internal uint Predict(Source live, uint casting, bool accepted, long now)
    {
        if (!Usable(live) || source != live || now - lastUse is < 0 or > MaxGap) return 0;
        uint before = accepted && current == casting ? previous : current;
        return before != 0 && outcomes.TryGetValue(new(live, before, casting), out var seen) &&
            seen.Count >= 3 && seen.Next != casting ? seen.Next : 0;
    }

    internal static bool Usable(Source s) => s.Slot.Valid && s.Registered != 0 && s.Target != 0 && s.Target != 0xE0000000;
    internal void Break() { source = default; previous = current = 0; }
    internal void Reset() { Break(); outcomes.Clear(); }
}
