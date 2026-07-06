using RemoteDesktop.Host;
using RemoteDesktop.Viewer;
using HostNative = RemoteDesktop.Host.Native.NativeMethods;

namespace RemoteDesktop.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Per-Monitor-V2 before any window exists, belt-and-suspenders with the manifest.
        try { HostNative.SetProcessDpiAwarenessContext(HostNative.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch { /* older Windows: manifest fallback applies */ }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Direct-launch flags bypass the chooser, for shortcuts and autostart.
        if (args.Any(a => a.Equals("--host", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new HostForm());
            return;
        }
        if (args.Any(a => a.Equals("--viewer", StringComparison.OrdinalIgnoreCase)))
        {
            Application.Run(new ConnectForm());
            return;
        }

        // Chooser loop: closing the Host/Viewer window returns to the chooser, so
        // one machine can switch roles without restarting the app.
        while (true)
        {
            LauncherChoice choice;
            using (var launcher = new LauncherForm())
            {
                Application.Run(launcher);
                choice = launcher.Choice;
            }

            switch (choice)
            {
                case LauncherChoice.Host:
                    Application.Run(new HostForm());
                    break;
                case LauncherChoice.Viewer:
                    Application.Run(new ConnectForm());
                    break;
                default:
                    return;
            }
        }
    }
}
