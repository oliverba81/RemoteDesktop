using System.Threading.Channels;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Transport;

public enum SendPriority
{
    /// <summary>Heartbeat, input, acks, control — latency critical, never dropped.</summary>
    High = 0,
    /// <summary>Screen frames — may drop the oldest under pressure (a newer frame supersedes it).</summary>
    Screen = 1,
    /// <summary>File chunks / clipboard — bounded with backpressure so they never starve the rest.</summary>
    Low = 2,
}

/// <summary>
/// The single writer for a connection. Every producer thread (capture, input,
/// clipboard, file, heartbeat) enqueues here; nobody writes to the socket
/// directly. This is what prevents interleaved concurrent writes from corrupting
/// the frame stream, and it lets latency-critical frames overtake a large file or
/// screen backlog.
///
/// Priority per drain: High &gt; Screen &gt; Low. The Screen queue drops its oldest
/// entry past capacity; the Low queue applies backpressure (the producer awaits a
/// free slot) so it can never be silently lost.
/// </summary>
public sealed class SendScheduler : IAsyncDisposable
{
    private readonly FramedConnection _conn;
    private readonly object _lock = new();
    private readonly Queue<Frame> _high = new();
    private readonly Queue<Frame> _screen = new();
    private readonly Queue<Frame> _low = new();
    private readonly int _screenCapacity;
    private readonly SemaphoreSlim _lowSlots;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private int _disposed;

    public SendScheduler(FramedConnection conn, int screenCapacity = 2, int lowCapacity = 8)
    {
        _conn = conn;
        _screenCapacity = screenCapacity;
        _lowSlots = new SemaphoreSlim(lowCapacity, lowCapacity);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Fired when the pump loop dies (peer gone / socket error).</summary>
    public event Action<Exception>? Faulted;

    public void EnqueueHigh(MessageType type, byte[] payload)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        Enqueue(_high, new Frame(type, payload), drop: false);
    }

    public void EnqueueScreen(MessageType type, byte[] payload)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        lock (_lock)
        {
            while (_screen.Count >= _screenCapacity)
                _screen.Dequeue(); // supersede the stalest frame
            _screen.Enqueue(new Frame(type, payload));
        }
        _signal.Release();
    }

    /// <summary>Enqueues a low-priority frame, awaiting a free slot (backpressure).</summary>
    public async Task EnqueueLowAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        await _lowSlots.WaitAsync(ct).ConfigureAwait(false);
        lock (_lock) _low.Enqueue(new Frame(type, payload));
        _signal.Release();
    }

    private void Enqueue(Queue<Frame> q, Frame frame, bool drop)
    {
        lock (_lock) q.Enqueue(frame);
        _signal.Release();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);

                Frame frame;
                bool fromLow = false;
                lock (_lock)
                {
                    if (_high.Count > 0) frame = _high.Dequeue();
                    else if (_screen.Count > 0) frame = _screen.Dequeue();
                    else if (_low.Count > 0) { frame = _low.Dequeue(); fromLow = true; }
                    else continue; // spurious wake (e.g. after a screen drop)
                }

                await _conn.SendAsync(frame.Type, frame.Payload, _cts.Token).ConfigureAwait(false);
                if (fromLow) _lowSlots.Release();
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // idempotent
        _cts.Cancel();
        try { await _pump.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
        _signal.Dispose();
        _lowSlots.Dispose();
    }
}
