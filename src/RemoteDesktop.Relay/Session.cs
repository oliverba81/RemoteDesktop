using System.Net.Sockets;
using RemoteDesktop.Shared.Transport;

namespace RemoteDesktop.Relay;

/// <summary>
/// A registered session on the relay. The relay only matches host and viewer by
/// id and then pipes bytes blindly — it never sees the E2E-encrypted content.
/// All state transitions happen under <see cref="Lock"/>.
/// </summary>
internal sealed class Session
{
    public required string Id { get; init; }
    public required byte[] OwnerTag { get; init; }

    public required TcpClient HostClient { get; set; }
    public required Stream HostStream { get; set; }
    public required FramedConnection HostConn { get; set; }

    public TcpClient? ViewerClient { get; set; }
    public Stream? ViewerStream { get; set; }
    public FramedConnection? ViewerConn { get; set; }

    /// <summary>True once a viewer has claimed the single viewer slot.</summary>
    public bool ViewerAttached { get; set; }

    /// <summary>True once bytes are being piped (handshake + session in progress).</summary>
    public bool Piping { get; set; }

    /// <summary>Cancels the host "park" read loop the moment a viewer pairs, so the
    /// blind pipe becomes the sole reader of the host stream (no concurrent reads).</summary>
    public readonly CancellationTokenSource ParkCts = new();

    /// <summary>Completes when the host park loop has stopped touching the stream.</summary>
    public readonly TaskCompletionSource ParkExited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public readonly object Lock = new();

    public void TeardownHost()
    {
        try { HostConn.Dispose(); } catch { /* ignore */ }
        try { HostClient.Close(); } catch { /* ignore */ }
    }

    public void TeardownViewer()
    {
        try { ViewerConn?.Dispose(); } catch { /* ignore */ }
        try { ViewerClient?.Close(); } catch { /* ignore */ }
        ViewerClient = null;
        ViewerStream = null;
        ViewerConn = null;
        ViewerAttached = false;
    }
}
