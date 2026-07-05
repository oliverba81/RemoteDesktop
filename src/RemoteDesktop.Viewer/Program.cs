using RemoteDesktop.Viewer.Native;

namespace RemoteDesktop.Viewer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try { NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* manifest fallback */ }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ConnectForm());
    }
}
