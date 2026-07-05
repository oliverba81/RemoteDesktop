using System.Net;
using System.Net.Sockets;

namespace RemoteDesktop.Shared.Tests;

/// <summary>A connected pair of TCP streams over loopback, for exercising the real
/// transport in tests.</summary>
public sealed class LoopbackPair : IDisposable
{
    public NetworkStream A { get; }
    public NetworkStream B { get; }
    private readonly TcpListener _listener;
    private readonly TcpClient _client;
    private readonly TcpClient _server;

    public LoopbackPair()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        _client = new TcpClient();
        var connectTask = _client.ConnectAsync(IPAddress.Loopback, port);
        _server = _listener.AcceptTcpClient();
        connectTask.GetAwaiter().GetResult();

        _client.NoDelay = true;
        _server.NoDelay = true;
        A = _client.GetStream();
        B = _server.GetStream();
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
        _listener.Stop();
    }
}
