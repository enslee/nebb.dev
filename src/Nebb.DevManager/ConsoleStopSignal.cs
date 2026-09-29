using System.Runtime.InteropServices;

namespace Nebb.DevManager;

internal static class ConsoleStopSignal
{
    private static readonly object Sync = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint signal, uint processGroupId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    public static bool TrySendBreak(int rootPid)
    {
        lock (Sync)
        {
            // Do not detach a test runner or terminal from its own console.
            if (GetConsoleWindow() != IntPtr.Zero || !AttachConsole((uint)rootPid)) return false;
            try { return GenerateConsoleCtrlEvent(1, (uint)rootPid); }
            finally { FreeConsole(); }
        }
    }
}
