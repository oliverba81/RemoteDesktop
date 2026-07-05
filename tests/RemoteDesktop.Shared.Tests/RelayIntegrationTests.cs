using System.Net.Sockets;
using RemoteDesktop.Relay;
using RemoteDesktop.Shared.Crypto;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transport;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

public class RelayIntegrationTests
{
    private static RelayOptions TestOptions() => new()
    {
        Port = 0, // ephemeral
        JoinFailureThreshold = 1000,
        QueryIdsPerWindow = 100_000,
    };

    private static async Task<RelayServer> StartRelayAsync(CancellationTokenSource cts)
    {
        var server = new RelayServer(TestOptions());
        _ = server.RunAsync(cts.Token);
        // Wait for the listener to bind.
        for (int i = 0; i < 100 && server.BoundPort == 0; i++)
            await Task.Delay(10);
        Assert.True(server.BoundPort > 0, "relay did not bind");
        return server;
    }

    private static async Task<FramedConnection> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(System.Net.IPAddress.Loopback, port);
        client.NoDelay = true;
        return new FramedConnection(client.GetStream());
    }

    [Fact]
    public async Task FullSession_ThroughRelay_EstablishesEncryptedChannel()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        RelayServer relay = await StartRelayAsync(cts);
        int port = relay.BoundPort;

        const string sessionId = "555111222";
        const string password = "swordfish";
        byte[] ownerTag = new byte[] { 1, 2, 3, 4 };

        async Task<HandshakeResult> RunHost()
        {
            var conn = await ConnectAsync(port);
            await conn.SendAsync(MessageType.RegisterSession,
                new RegisterSessionPayload(ProtocolConstants.Version, sessionId, ownerTag).Serialize(), cts.Token);
            // Wait for pairing signal.
            Frame? ready = await conn.ReceiveAsync(cts.Token);
            Assert.Equal(MessageType.SessionReady, ready!.Value.Type);
            HandshakeResult r = await SecureHandshake.RunAsync(
                conn, sessionId, password, isHost: true, new PixelSize(1920, 1080), cts.Token);

            // Send one encrypted screen frame, expect an ack.
            await conn.SendAsync(MessageType.ScreenFrame,
                new ScreenFramePayload(7, Array.Empty<ScreenTile>()).Serialize(), cts.Token);
            Frame? ack = await conn.ReceiveAsync(cts.Token);
            Assert.Equal(MessageType.FrameAck, ack!.Value.Type);
            return r;
        }

        async Task RunViewer()
        {
            var conn = await ConnectAsync(port);
            await conn.SendAsync(MessageType.JoinSession,
                new JoinSessionPayload(ProtocolConstants.Version, sessionId).Serialize(), cts.Token);
            Frame? ready = await conn.ReceiveAsync(cts.Token);
            Assert.Equal(MessageType.SessionReady, ready!.Value.Type);
            HandshakeResult r = await SecureHandshake.RunAsync(
                conn, sessionId, password, isHost: false, new PixelSize(0, 0), cts.Token);
            Assert.Equal(1920, r.RemoteWidth);

            Frame? frame = await conn.ReceiveAsync(cts.Token);
            Assert.Equal(MessageType.ScreenFrame, frame!.Value.Type);
            await conn.SendAsync(MessageType.FrameAck, new FrameAckPayload(7).Serialize(), cts.Token);
        }

        Task host = RunHost();
        await Task.Delay(100); // let the host register first
        Task viewer = RunViewer();
        await Task.WhenAll(host, viewer);

        cts.Cancel();
    }

    [Fact]
    public async Task Join_UnknownSession_ReturnsError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        RelayServer relay = await StartRelayAsync(cts);

        var conn = await ConnectAsync(relay.BoundPort);
        await conn.SendAsync(MessageType.JoinSession,
            new JoinSessionPayload(ProtocolConstants.Version, "000000000").Serialize(), cts.Token);
        Frame? resp = await conn.ReceiveAsync(cts.Token);
        Assert.Equal(MessageType.SessionError, resp!.Value.Type);
        Assert.Equal(SessionErrorCode.SessionNotFound, SessionErrorPayload.Parse(resp.Value.Payload).Code);
        cts.Cancel();
    }

    [Fact]
    public async Task QueryStatus_ReportsRegisteredSessionOnline()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        RelayServer relay = await StartRelayAsync(cts);
        int port = relay.BoundPort;

        // Register a host and leave it parked.
        var host = await ConnectAsync(port);
        await host.SendAsync(MessageType.RegisterSession,
            new RegisterSessionPayload(ProtocolConstants.Version, "999888777", new byte[] { 9 }).Serialize(), cts.Token);
        await Task.Delay(100);

        var query = await ConnectAsync(port);
        var entries = new[]
        {
            new QueryStatusEntry("999888777", Array.Empty<byte>()),
            new QueryStatusEntry("111111111", Array.Empty<byte>()),
        };
        await query.SendAsync(MessageType.QueryStatus, new QueryStatusPayload(entries).Serialize(), cts.Token);
        Frame? resp = await query.ReceiveAsync(cts.Token);
        var results = StatusResponsePayload.Parse(resp!.Value.Payload).Results;

        Assert.True(results.First(r => r.SessionId == "999888777").Online);
        Assert.False(results.First(r => r.SessionId == "111111111").Online);
        cts.Cancel();
    }
}
