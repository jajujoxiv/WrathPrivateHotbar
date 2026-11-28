global using Environment = SimulationClock;

// Production uses Environment.TickCount64. These scenarios can replay exact frame times.
internal sealed class SimulationClock : IDisposable
{
    private static long? simulated;
    public static long TickCount64 => simulated ?? System.Environment.TickCount64;
    internal SimulationClock() => simulated = 100_000;
    internal void Advance(int ms) => simulated += ms;
    public void Dispose() => simulated = null;
}
