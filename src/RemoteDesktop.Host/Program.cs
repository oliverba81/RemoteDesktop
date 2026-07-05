using RemoteDesktop.Host.Native;

namespace RemoteDesktop.Host;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Per-Monitor-V2 before any window exists, belt-and-suspenders with the manifest.
        try { NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* older Windows: manifest fallback applies */ }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new HostForm());
    }
}
