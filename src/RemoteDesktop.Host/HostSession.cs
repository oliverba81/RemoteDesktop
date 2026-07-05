using System.Diagnostics;
using System.Net.Sockets;
using RemoteDesktop.Host.Capture;
using RemoteDesktop.Host.Input;
using RemoteDesktop.Shared.Crypto;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transfer;
using RemoteDesktop.Shared.Transport;

namespace RemoteDesktop.Host;

/// <summary>
/// Drives one host lifecycle: connect to the relay, register, wait for a viewer,
/// run the authenticated handshake, then stream the screen and apply remote input.
/// Reconnects automatically while unpaired.
/// </summary>
public sealed class HostSession : IAsyncDisposable
{
    private readonly string _relayHost;
    private readonly int _relayPort;
    private readonly string _sessionId;
    private readonly string _password;
    private readonly byte[] _ownerTag;
    private readonly InputInjector _injector = new();
    private readonly SemaphoreSlim _ackSlots = new(2, 2);
    private volatile QualityLevel _quality = QualityLevel.Medium;
    private SendScheduler? _scheduler;
    private FileTransferService? _files;

    public HostSession(string relayHost, int relayPort, string sessionId, string password, byte[] ownerTag)
    {
        _relayHost = relayHost;
        _relayPort = relayPort;
        _sessionId = sessionId;
        _password = password;
        _ownerTag = ownerTag;
    }

    public event Action<string>? Status;
    public event Action? ViewerConnected;
    public event Action? ViewerDisconnected;

    /// <summary>Raised when the peer sends clipboard text to set locally.</summary>
    public event Action<string>? RemoteClipboardText;

    /// <summary>Queues local clipboard text for the peer (no-op when unpaired).</summary>
    public void SendClipboardText(string text) =>
        _scheduler?.EnqueueHigh(MessageType.ClipboardText, new ClipboardTextPayload(text).Serialize());

    /// <summary>Sends a local file to the connected viewer.</summary>
    public Task SendFileAsync(string path, CancellationToken ct) =>
        _files?.SendFileAsync(path, ct) ?? Task.CompletedTask;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ServeOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Status?.Invoke($"Reconnecting… ({ex.Message})");
            }

            // Backoff before re-registering.
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ServeOnceAsync(CancellationToken ct)
    {
        using var capturer = new GdiScreenCapturer();

        Status?.Invoke("Connecting to relay…");
        using var client = new TcpClient();
        await client.ConnectAsync(_relayHost, _relayPort, ct).ConfigureAwait(false);
        client.NoDelay = true;
        var conn = new FramedConnection(client.GetStream());

        await conn.SendAsync(MessageType.RegisterSession,
            new RegisterSessionPayload(ProtocolConstants.Version, _sessionId, _ownerTag).Serialize(), ct)
            .ConfigureAwait(false);
        Status?.Invoke("Waiting for a viewer to connect…");

        Frame? ready = await conn.ReceiveAsync(ct).ConfigureAwait(false);
        if (ready is null) throw new IOException("Relay closed before pairing.");
        if (ready.Value.Type == MessageType.SessionError)
        {
            var err = SessionErrorPayload.Parse(ready.Value.Payload);
            throw new IOException($"Relay rejected session: {err.Message}");
        }
        if (ready.Value.Type != MessageType.SessionReady)
            throw new ProtocolException($"Unexpected {ready.Value.Type} while waiting to pair.");

        await SecureHandshake.RunAsync(conn, _sessionId, _password, isHost: true, capturer.Size, ct)
            .ConfigureAwait(false);

        Status?.Invoke("Viewer connected.");
        ViewerConnected?.Invoke();

        await using var scheduler = new SendScheduler(conn);
        _scheduler = scheduler;
        // The host auto-accepts incoming files into its Downloads folder.
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        _files = new FileTransferService(scheduler, downloads, _ => Task.FromResult(true));
        _files.Completed += (_, path) => Status?.Invoke($"File received: {Path.GetFileName(path)}");
        _files.Failed += (_, reason) => Status?.Invoke($"File transfer failed: {reason}");

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task capture = Task.Run(() => CaptureLoopAsync(capturer, scheduler, sessionCts.Token), sessionCts.Token);
        Task heartbeat = HeartbeatLoopAsync(scheduler, sessionCts.Token);

        try
        {
            await ReceiveLoopAsync(conn, sessionCts.Token).ConfigureAwait(false);
        }
        finally
        {
            sessionCts.Cancel();
            _scheduler = null;
            _files = null;
            _injector.ReleaseAllModifiers();
            try { await capture.ConfigureAwait(false); } catch { /* ignore */ }
            try { await heartbeat.ConfigureAwait(false); } catch { /* ignore */ }
            ViewerDisconnected?.Invoke();
            Status?.Invoke("Viewer disconnected.");
        }
    }

    private async Task CaptureLoopAsync(GdiScreenCapturer capturer, SendScheduler scheduler, CancellationToken ct)
    {
        var sw = new Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            await _ackSlots.WaitAsync(ct).ConfigureAwait(false);
            sw.Restart();

            ScreenFramePayload? frame = capturer.CaptureChanged(GdiScreenCapturer.QualityToJpeg(_quality));
            if (frame is null)
            {
                _ackSlots.Release(); // nothing to send, keep the slot
                scheduler.EnqueueHigh(MessageType.CursorUpdate, capturer.GetCursor().Serialize());
                await DelayToFrameBudget(sw, ct).ConfigureAwait(false);
                continue;
            }

            scheduler.EnqueueScreen(MessageType.ScreenFrame, frame.Serialize());
            scheduler.EnqueueHigh(MessageType.CursorUpdate, capturer.GetCursor().Serialize());
            await DelayToFrameBudget(sw, ct).ConfigureAwait(false);
        }
    }

    private static async Task DelayToFrameBudget(Stopwatch sw, CancellationToken ct)
    {
        // Adaptive: never faster than ~15 fps; if capture+encode already took longer,
        // don't sleep (framerate falls out naturally under load).
        const int targetMs = 66;
        int elapsed = (int)sw.ElapsedMilliseconds;
        if (elapsed < targetMs)
            await Task.Delay(targetMs - elapsed, ct).ConfigureAwait(false);
    }

    private static async Task HeartbeatLoopAsync(SendScheduler scheduler, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                scheduler.EnqueueHigh(MessageType.Heartbeat, Array.Empty<byte>());
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    private async Task ReceiveLoopAsync(FramedConnection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Frame? f = await conn.ReceiveAsync(ct).ConfigureAwait(false);
            if (f is null) break; // peer gone
            switch (f.Value.Type)
            {
                case MessageType.InputMouse:
                    _injector.Mouse(InputMousePayload.Parse(f.Value.Payload));
                    break;
                case MessageType.InputKeyboard:
                    _injector.Keyboard(InputKeyboardPayload.Parse(f.Value.Payload));
                    break;
                case MessageType.InputReset:
                    _injector.ReleaseAllModifiers();
                    break;
                case MessageType.FrameAck:
                    if (_ackSlots.CurrentCount < 2) _ackSlots.Release();
                    break;
                case MessageType.QualityChange:
                    _quality = QualityChangePayload.Parse(f.Value.Payload).Level;
                    break;
                case MessageType.ClipboardText:
                    RemoteClipboardText?.Invoke(ClipboardTextPayload.Parse(f.Value.Payload).Text);
                    break;
                case MessageType.FileOffer:
                case MessageType.FileAccept:
                case MessageType.FileReject:
                case MessageType.FileChunk:
                case MessageType.FileDone:
                case MessageType.FileError:
                    if (_files is not null) await _files.HandleFrameAsync(f.Value).ConfigureAwait(false);
                    break;
                case MessageType.Heartbeat:
                    break;
                case MessageType.Bye:
                    return;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _ackSlots.Dispose();
        return ValueTask.CompletedTask;
    }
}
