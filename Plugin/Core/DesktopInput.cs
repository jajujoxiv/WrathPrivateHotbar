using System.Runtime.InteropServices;

namespace WrathPrivateHotbar;

internal static class DesktopInput
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    internal static bool PhysicalDown(int key) => GetAsyncKeyState(key) < 0;
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    internal static bool Foreground()
    {
        var h = GetForegroundWindow();
        if (h == 0)
            return false;
        GetWindowThreadProcessId(h, out var pid);
        return pid == (uint)Environment.ProcessId;
    }
}
