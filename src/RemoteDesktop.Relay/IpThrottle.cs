using System.Collections.Concurrent;

namespace RemoteDesktop.Relay;

/// <summary>
/// Per-source-IP failure and query accounting. Lockouts are keyed on the
/// attacker's IP, never on the session — so a third party cannot lock a
/// legitimate owner out of their own (fixed, unattended) session by feeding wrong
/// passwords. Uses a sliding window.
/// </summary>
public sealed class IpThrottle
{
    private readonly RelayOptions _opts;
    private readonly ConcurrentDictionary<string, Counter> _joinFailures = new();
    private readonly ConcurrentDictionary<string, Counter> _queries = new();

    public IpThrottle(RelayOptions opts) => _opts = opts;

    private sealed class Counter
    {
        public int Count;
        public DateTime WindowStart = DateTime.UtcNow;
        public readonly object Lock = new();
    }

    /// <summary>True if this IP has exceeded the join-failure threshold this window.</summary>
    public bool IsJoinBlocked(string ip)
    {
        if (!_joinFailures.TryGetValue(ip, out var c)) return false;
        lock (c.Lock)
        {
            Roll(c, _opts.JoinFailureWindow);
            return c.Count >= _opts.JoinFailureThreshold;
        }
    }

    public void RecordJoinFailure(string ip)
    {
        var c = _joinFailures.GetOrAdd(ip, _ => new Counter());
        lock (c.Lock)
        {
            Roll(c, _opts.JoinFailureWindow);
            c.Count++;
        }
    }

    /// <summary>Charges <paramref name="distinctIds"/> against the IP's query budget;
    /// returns false if the budget is exhausted.</summary>
    public bool TryChargeQuery(string ip, int distinctIds)
    {
        var c = _queries.GetOrAdd(ip, _ => new Counter());
        lock (c.Lock)
        {
            Roll(c, _opts.QueryWindow);
            if (c.Count + distinctIds > _opts.QueryIdsPerWindow) return false;
            c.Count += distinctIds;
            return true;
        }
    }

    private static void Roll(Counter c, TimeSpan window)
    {
        DateTime now = DateTime.UtcNow;
        if (now - c.WindowStart > window)
        {
            c.WindowStart = now;
            c.Count = 0;
        }
    }
}
