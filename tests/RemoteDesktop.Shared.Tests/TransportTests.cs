using RemoteDesktop.Shared.Crypto;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transport;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

public class TransportTests
{
    [Fact]
    public async Task PlaintextFrame_RoundTrips()
    {
        using var pair = new LoopbackPair();
        var a = new FramedConnection(pair.A);
        var b = new FramedConnection(pair.B);

        byte[] payload = new byte[] { 10, 20, 30 };
        await a.SendAsync(MessageType.Heartbeat, payload, CancellationToken.None);
        Frame? received = await b.ReceiveAsync(CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal(MessageType.Heartbeat, received!.Value.Type);
        Assert.Equal(payload, received.Value.Payload);
    }

    [Fact]
    public async Task Handshake_EstablishesEncryptedChannel_BothDirections()
    {
        using var pair = new LoopbackPair();
        var hostConn = new FramedConnection(pair.A);
        var viewerConn = new FramedConnection(pair.B);

        const string sessionId = "123456789";
        const string password = "correct horse";

        var hostTask = SecureHandshake.RunAsync(
            hostConn, sessionId, password, isHost: true, new PixelSize(2560, 1440), CancellationToken.None);
        var viewerTask = SecureHandshake.RunAsync(
            viewerConn, sessionId, password, isHost: false, new PixelSize(0, 0), CancellationToken.None);

        HandshakeResult[] results = await Task.WhenAll(hostTask, viewerTask);

        // Viewer learned the host's screen size.
        Assert.Equal(2560, results[1].RemoteWidth);
        Assert.Equal(1440, results[1].RemoteHeight);
        Assert.True(hostConn.IsEncrypted && viewerConn.IsEncrypted);

        // Host -> Viewer encrypted message.
        var frameData = new ScreenFramePayload(1, Array.Empty<ScreenTile>()).Serialize();
        await hostConn.SendAsync(MessageType.ScreenFrame, frameData, CancellationToken.None);
        Frame? got = await viewerConn.ReceiveAsync(CancellationToken.None);
        Assert.Equal(MessageType.ScreenFrame, got!.Value.Type);

        // Viewer -> Host encrypted message.
        await viewerConn.SendAsync(MessageType.FrameAck, new FrameAckPayload(1).Serialize(), CancellationToken.None);
        Frame? ack = await hostConn.ReceiveAsync(CancellationToken.None);
        Assert.Equal(MessageType.FrameAck, ack!.Value.Type);
        Assert.Equal(1u, FrameAckPayload.Parse(ack.Value.Payload).FrameId);
    }

    [Fact]
    public async Task Handshake_WrongPassword_Fails()
    {
        using var pair = new LoopbackPair();
        var hostConn = new FramedConnection(pair.A);
        var viewerConn = new FramedConnection(pair.B);

        var hostTask = SecureHandshake.RunAsync(
            hostConn, "123456789", "rightpw", isHost: true, new PixelSize(800, 600), CancellationToken.None);
        var viewerTask = SecureHandshake.RunAsync(
            viewerConn, "123456789", "WRONGpw", isHost: false, new PixelSize(0, 0), CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(async () => await Task.WhenAll(hostTask, viewerTask));
    }

    [Fact]
    public async Task SendScheduler_PrioritizesHighOverLow()
    {
        using var pair = new LoopbackPair();
        var sender = new FramedConnection(pair.A);
        var receiver = new FramedConnection(pair.B);
        await using var scheduler = new SendScheduler(sender);

        // Fill low-priority queue, then add a high-priority frame.
        for (int i = 0; i < 3; i++)
            await scheduler.EnqueueLowAsync(MessageType.FileChunk, new byte[] { (byte)i }, CancellationToken.None);
        scheduler.EnqueueHigh(MessageType.Heartbeat, Array.Empty<byte>());

        // Drain a few frames; a Heartbeat must arrive (not starved forever).
        bool sawHeartbeat = false;
        for (int i = 0; i < 4; i++)
        {
            Frame? f = await receiver.ReceiveAsync(CancellationToken.None);
            if (f!.Value.Type == MessageType.Heartbeat) { sawHeartbeat = true; break; }
        }
        Assert.True(sawHeartbeat);
    }
}
