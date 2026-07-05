namespace RemoteDesktop.Relay;

/// <summary>
/// Relay configuration. Everything is injectable so integration tests can bind an
/// ephemeral port (0) and relax the rate limits — otherwise loopback host+viewer
/// share one IP and the throttle makes the handshake test flaky.
/// </summary>
public sealed class RelayOptions
{
    public int Port { get; init; } = 9945;

    /// <summary>Max concurrently registered sessions (global backstop).</summary>
    public int MaxSessions { get; init; } = 10_000;

    /// <summary>Close a paired session after this much silence in both directions.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Failed joins from one IP within this window before it is throttled.</summary>
    public int JoinFailureThreshold { get; init; } = 5;
    public TimeSpan JoinFailureWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Max distinct session IDs a single IP may probe via QueryStatus per window.</summary>
    public int QueryIdsPerWindow { get; init; } = 200;
    public TimeSpan QueryWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Cap on entries in a single QueryStatus request.</summary>
    public int MaxQueryBatch { get; init; } = 64;

    public static RelayOptions FromEnvironment()
    {
        var opts = new RelayOptions();
        string? portEnv = Environment.GetEnvironmentVariable("RELAY_PORT");
        if (int.TryParse(portEnv, out int port) && port is > 0 and < 65536)
            opts = new RelayOptions { Port = port };
        return opts;
    }
}
