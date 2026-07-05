using System.Runtime.InteropServices;

namespace RemoteDesktop.Viewer.Native;

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(nint value);

    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
}
