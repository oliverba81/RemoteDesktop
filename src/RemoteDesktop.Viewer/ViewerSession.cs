using System.Net.Sockets;
using RemoteDesktop.Shared.Crypto;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transfer;
using RemoteDesktop.Shared.Transport;

namespace RemoteDesktop.Viewer;

/// <summary>
/// Drives the viewer side: join a session through the relay, run the handshake,
/// then feed incoming screen frames to the <see cref="RemoteScreenControl"/> and
/// forward local input.
/// </summary>
public sealed class ViewerSession : IAsyncDisposable
{
    private readonly string _relayHost;
    private readonly int _relayPort;
    private readonly string _sessionId;
    private readonly string _password;
    private readonly RemoteScreenControl _screen;

    private FramedConnection? _conn;
    private SendScheduler? _scheduler;
    private FileTransferService? _files;

    /// <summary>Where accepted files are written. Defaults to the user's Downloads.</summary>
    public string DownloadDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Asked before accepting an incoming file. Set by the UI.</summary>
    public Func<FileOfferPayload, Task<bool>> AcceptFile { get; set; } = _ => Task.FromResult(false);

    public ViewerSession(string relayHost, int relayPort, string sessionId, string password, RemoteScreenControl screen)
    {
        _relayHost = relayHost;
        _relayPort = relayPort;
        _sessionId = sessionId;
        _password = password;
        _screen = screen;
        _screen.MouseInput += OnLocalMouse;
    }

    public event Action<string>? Status;
    public event Action<PixelSize>? Connected;
    public event Action? Disconnected;
    public event Action<string>? RemoteClipboardText;
    /// <summary>(fileName, bytesDone, bytesTotal, incoming?)</summary>
    public event Action<string, long, long, bool>? FileProgress;

    public async Task RunAsync(CancellationToken ct)
    {
        Status?.Invoke("Connecting…");
        using var client = new TcpClient();
        await client.ConnectAsync(_relayHost, _relayPort, ct).ConfigureAwait(false);
        client.NoDelay = true;
        var conn = new FramedConnection(client.GetStream());
        _conn = conn;

        await conn.SendAsync(MessageType.JoinSession,
            new JoinSessionPayload(ProtocolConstants.Version, _sessionId).Serialize(), ct).ConfigureAwait(false);

        Frame? ready = await conn.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("Relay closed before pairing.");
        if (ready.Value.Type == MessageType.SessionError)
            throw new IOException(SessionErrorPayload.Parse(ready.Value.Payload).Message);
        if (ready.Value.Type != MessageType.SessionReady)
            throw new ProtocolException($"Unexpected {ready.Value.Type} while joining.");

        HandshakeResult hs = await SecureHandshake.RunAsync(
            conn, _sessionId, _password, isHost: false, new PixelSize(0, 0), ct).ConfigureAwait(false);

        var remote = new PixelSize(hs.RemoteWidth, hs.RemoteHeight);
        Connected?.Invoke(remote);
        Status?.Invoke("Connected.");

        var scheduler = new SendScheduler(conn);
        _scheduler = scheduler;

        _files = new FileTransferService(scheduler, DownloadDirectory, AcceptFile);
        _files.Progress += (_, name, done, total, incoming) => FileProgress?.Invoke(name, done, total, incoming);
        _files.Completed += (_, path) => Status?.Invoke($"File saved: {Path.GetFileName(path)}");
        _files.Failed += (_, reason) => Status?.Invoke($"File transfer failed: {reason}");

        Task heartbeat = HeartbeatLoopAsync(scheduler, ct);
        try
        {
            await ReceiveLoopAsync(conn, scheduler, ct).ConfigureAwait(false);
        }
        finally
        {
            SendInputReset();
            _files = null;
            await scheduler.DisposeAsync().ConfigureAwait(false);
            try { await heartbeat.ConfigureAwait(false); } catch { /* ignore */ }
            Disconnected?.Invoke();
            Status?.Invoke("Disconnected.");
        }
    }

    private async Task ReceiveLoopAsync(FramedConnection conn, SendScheduler scheduler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Frame? f = await conn.ReceiveAsync(ct).ConfigureAwait(false);
            if (f is null) break;
            switch (f.Value.Type)
            {
                case MessageType.ScreenFrame:
                    ScreenFramePayload frame = ScreenFramePayload.Parse(f.Value.Payload);
                    _screen.ApplyFrame(frame); // decode/composite off the UI thread
                    scheduler.EnqueueHigh(MessageType.FrameAck, new FrameAckPayload(frame.FrameId).Serialize());
                    break;
                case MessageType.CursorUpdate:
                    _screen.ApplyCursor(CursorUpdatePayload.Parse(f.Value.Payload));
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

    private void OnLocalMouse(InputMousePayload p) =>
        _scheduler?.EnqueueHigh(MessageType.InputMouse, p.Serialize());

    /// <summary>Sends a printable character (Unicode, layout independent).</summary>
    public void SendChar(char c, bool down) =>
        _scheduler?.EnqueueHigh(MessageType.InputKeyboard,
            new InputKeyboardPayload(IsUnicode: true, c, down, IsExtended: false).Serialize());

    /// <summary>Sends a virtual-key event (modifiers, shortcuts, navigation keys).</summary>
    public void SendVirtualKey(ushort vk, bool down, bool extended) =>
        _scheduler?.EnqueueHigh(MessageType.InputKeyboard,
            new InputKeyboardPayload(IsUnicode: false, vk, down, extended).Serialize());

    public void SendInputReset() =>
        _scheduler?.EnqueueHigh(MessageType.InputReset, Array.Empty<byte>());

    public void SetQuality(QualityLevel level) =>
        _scheduler?.EnqueueHigh(MessageType.QualityChange, new QualityChangePayload(level).Serialize());

    public void SendClipboardText(string text) =>
        _scheduler?.EnqueueHigh(MessageType.ClipboardText, new ClipboardTextPayload(text).Serialize());

    public Task SendFileAsync(string path, CancellationToken ct) =>
        _files?.SendFileAsync(path, ct) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        _screen.MouseInput -= OnLocalMouse;
        if (_scheduler is not null) await _scheduler.DisposeAsync().ConfigureAwait(false);
        _conn?.Dispose();
    }
}
