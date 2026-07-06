using System.Runtime.InteropServices;

namespace RemoteDesktop.App;

/// <summary>
/// Enforces a single running instance per Windows session. The first instance
/// holds a named mutex and listens for a broadcast "show" message; a second launch
/// finds the mutex taken, tells the running instance to surface its window, and
/// exits. Session-scoped (Local\) so fast-user-switching gives each user their own
/// instance.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\RemoteDesktop.SingleInstance.v1";
    private const string MessageName = "RemoteDesktop.SingleInstance.Show.v1";

    private static Mutex? _mutex;

    /// <summary>Registered window message both instances agree on (same name → same id).</summary>
    public static uint ShowMessage { get; } = RegisterWindowMessage(MessageName);

    /// <summary>
    /// True if this is the first/only instance (mutex acquired). False if another
    /// instance already runs — in that case it has been asked to come to the front.
    /// </summary>
    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return true;

        // Another instance owns it: ask every top-level window to surface. Only the
        // running instance's listener reacts to our registered message id.
        PostMessage((nint)HWND_BROADCAST, ShowMessage, nint.Zero, nint.Zero);
        return false;
    }

    /// <summary>Brings the app's current window back to the foreground (used when a
    /// second launch pings us, e.g. the host running in the tray).</summary>
    public static void SurfaceApp()
    {
        foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
        {
            void Bring()
            {
                if (!form.Visible) form.Show();
                if (form.WindowState == FormWindowState.Minimized)
                    form.WindowState = FormWindowState.Normal;
                form.Activate();
                form.BringToFront();
            }
            if (form.InvokeRequired) form.BeginInvoke(Bring);
            else Bring();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    private const int HWND_BROADCAST = 0xFFFF;
}

/// <summary>
/// A hidden top-level window that lives for the whole process and, on the shared
/// "show" broadcast, brings the app's window forward. Top-level (not message-only)
/// so it receives HWND_BROADCAST.
/// </summary>
internal sealed class SingleInstanceListener : NativeWindow, IDisposable
{
    public SingleInstanceListener() => CreateHandle(new CreateParams());

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == SingleInstance.ShowMessage)
            SingleInstance.SurfaceApp();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();
}
