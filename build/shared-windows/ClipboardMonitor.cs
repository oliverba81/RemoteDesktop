using System.Runtime.InteropServices;
using RemoteDesktop.Shared.Transfer;

namespace RemoteDesktop.Windows;

/// <summary>
/// Watches the local clipboard for text changes (event-driven via
/// WM_CLIPBOARDUPDATE) and writes received text back. Shared by Host and Viewer as
/// a linked source file so WinForms stays out of the cross-platform Shared library.
///
/// Loop prevention uses <see cref="ClipboardLoopGuard"/> (a one-shot suppress
/// token), not a timer. Content that password managers mark as sensitive
/// (ExcludeClipboardContentFromMonitorProcessing / CanIncludeInClipboardHistory=0)
/// is never sent, and text we write carries those markers so it does not land in
/// the peer's Win+V history.
/// </summary>
public sealed class ClipboardMonitor : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    private const string HistoryFormat = "CanIncludeInClipboardHistory";
    private const int MaxTextBytes = 1 * 1024 * 1024;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    private readonly ClipboardLoopGuard _guard = new();
    private bool _enabled = true;

    public ClipboardMonitor()
    {
        CreateHandle(new CreateParams());
        AddClipboardFormatListener(Handle);
    }

    /// <summary>Raised (on the UI thread) when the user copies text that should sync.</summary>
    public event Action<string>? TextCopied;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE && _enabled)
            OnClipboardUpdate();
        base.WndProc(ref m);
    }

    private void OnClipboardUpdate()
    {
        // Never sync content a password manager flagged as sensitive.
        if (Clipboard.ContainsData(ExcludeFormat)) return;

        if (!TryGetText(out string text) || text.Length == 0) return;
        if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxTextBytes) return;
        if (!_guard.ShouldSend(text)) return;
        TextCopied?.Invoke(text);
    }

    /// <summary>Writes text received from the peer, arming loop suppression and
    /// marking it excluded from clipboard history.</summary>
    public void SetRemoteText(string text)
    {
        _guard.SuppressNext(text);
        var data = new DataObject();
        data.SetText(text);
        using var excludeStream = new MemoryStream(new byte[] { 0 });
        data.SetData(ExcludeFormat, false, excludeStream);
        data.SetData(HistoryFormat, false, new MemoryStream(new byte[] { 0 }));
        TrySetData(data);
    }

    private static bool TryGetText(out string text)
    {
        for (int i = 0; i < 8; i++)
        {
            try
            {
                text = Clipboard.GetText();
                return true;
            }
            catch (Exception ex) when (ex is ExternalException or COMException)
            {
                Thread.Sleep(60);
            }
        }
        text = "";
        return false;
    }

    private static void TrySetData(DataObject data)
    {
        for (int i = 0; i < 8; i++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true, retryTimes: 5, retryDelay: 80);
                return;
            }
            catch (Exception ex) when (ex is ExternalException or COMException)
            {
                Thread.Sleep(60);
            }
        }
    }

    public void Dispose()
    {
        try { RemoveClipboardFormatListener(Handle); } catch { /* ignore */ }
        DestroyHandle();
    }
}
