using System.Buffers;
using System.Net.Sockets;

namespace RemoteDesktop.Relay;

/// <summary>
/// Pumps bytes between the two paired sockets without parsing them. Bounded reads
/// preserve TCP backpressure (no unbounded buffering → no OOM DoS), and an idle
/// timeout on the last transferred byte in either direction frees a session whose
/// peer vanished without a FIN (half-open TCP).
/// </summary>
internal static class BlindPipe
{
    public static async Task RunAsync(
        TcpClient a, Stream aStream, TcpClient b, Stream bStream, TimeSpan idleTimeout, CancellationToken ct)
    {
        EnableKeepAlive(a);
        EnableKeepAlive(b);

        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastActivity = new StrongBox(DateTime.UtcNow);

        Task pumpAtoB = PumpAsync(aStream, bStream, lastActivity, idleCts.Token);
        Task pumpBtoA = PumpAsync(bStream, aStream, lastActivity, idleCts.Token);
        Task watchdog = WatchdogAsync(lastActivity, idleTimeout, idleCts);

        await Task.WhenAny(pumpAtoB, pumpBtoA).ConfigureAwait(false);
        idleCts.Cancel(); // one side ended → tear the other down
        try { await Task.WhenAll(pumpAtoB, pumpBtoA, watchdog).ConfigureAwait(false); }
        catch { /* expected on cancellation / disconnect */ }
    }

    private static async Task PumpAsync(Stream from, Stream to, StrongBox lastActivity, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break; // clean EOF
                lastActivity.Value = DateTime.UtcNow;
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { /* torn down */ }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static async Task WatchdogAsync(StrongBox lastActivity, TimeSpan idleTimeout, CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                if (DateTime.UtcNow - lastActivity.Value > idleTimeout)
                {
                    cts.Cancel();
                    break;
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
    }

    private static void EnableKeepAlive(TcpClient client)
    {
        try { client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); }
        catch { /* best effort */ }
    }

    private sealed class StrongBox
    {
        public DateTime Value;
        public StrongBox(DateTime v) => Value = v;
    }
}
