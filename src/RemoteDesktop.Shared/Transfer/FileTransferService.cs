using System.Collections.Concurrent;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transfer;
using RemoteDesktop.Shared.Transport;

namespace RemoteDesktop.Shared.Transfer;

/// <summary>
/// Drives file transfers over an established session, shared by Host and Viewer.
/// GDI-free: it sends via the <see cref="SendScheduler"/> and uses
/// <see cref="FileStreamer"/>/<see cref="FileReceiver"/>. The UI provides an accept
/// callback and observes progress via events. One active transfer per direction.
/// </summary>
public sealed class FileTransferService
{
    private readonly SendScheduler _scheduler;
    private readonly string _downloadDirectory;
    private readonly Func<FileOfferPayload, Task<bool>> _acceptAsync;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingSends = new();
    private FileReceiver? _incoming;
    private string? _incomingId;
    private long _incomingTotal;

    public FileTransferService(
        SendScheduler scheduler, string downloadDirectory, Func<FileOfferPayload, Task<bool>> acceptAsync)
    {
        _scheduler = scheduler;
        _downloadDirectory = downloadDirectory;
        _acceptAsync = acceptAsync;
    }

    /// <summary>(transferId, fileName, bytesDone, bytesTotal, incoming?)</summary>
    public event Action<string, string, long, long, bool>? Progress;
    public event Action<string, string>? Completed; // transferId, finalPathOrFileName
    public event Action<string, string>? Failed;    // transferId, reason

    /// <summary>Sends a local file to the peer. Blocks (async) until done/rejected.</summary>
    public async Task SendFileAsync(string path, CancellationToken ct)
    {
        string transferId = Guid.NewGuid().ToString("N");
        FileOfferPayload offer = FileStreamer.CreateOffer(transferId, path);

        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSends[transferId] = accepted;
        _scheduler.EnqueueHigh(MessageType.FileOffer, offer.Serialize());

        bool ok;
        using (ct.Register(() => accepted.TrySetCanceled()))
            ok = await accepted.Task.ConfigureAwait(false);
        _pendingSends.TryRemove(transferId, out _);
        if (!ok) { Failed?.Invoke(transferId, "Peer declined."); return; }

        long sent = 0;
        foreach (FileChunkPayload chunk in FileStreamer.ReadChunks(transferId, path))
        {
            await _scheduler.EnqueueLowAsync(MessageType.FileChunk, chunk.Serialize(), ct).ConfigureAwait(false);
            sent += chunk.Data.Length;
            Progress?.Invoke(transferId, offer.FileName, sent, offer.Size, false);
        }

        byte[] sha = FileStreamer.ComputeSha256(path);
        var done = new PayloadWriter();
        done.WriteString(transferId);
        done.WriteBytes(sha);
        _scheduler.EnqueueHigh(MessageType.FileDone, done.ToArray());
        Completed?.Invoke(transferId, offer.FileName);
    }

    /// <summary>Feeds an incoming File* frame. Called from the session receive loop.</summary>
    public async Task HandleFrameAsync(Frame frame)
    {
        switch (frame.Type)
        {
            case MessageType.FileOffer:
                await OnOfferAsync(FileOfferPayload.Parse(frame.Payload)).ConfigureAwait(false);
                break;
            case MessageType.FileAccept:
                Signal(frame.Payload, true);
                break;
            case MessageType.FileReject:
                Signal(frame.Payload, false);
                break;
            case MessageType.FileChunk:
                OnChunk(FileChunkPayload.Parse(frame.Payload));
                break;
            case MessageType.FileDone:
                OnDone(frame.Payload);
                break;
            case MessageType.FileError:
                Abort();
                break;
        }
    }

    private async Task OnOfferAsync(FileOfferPayload offer)
    {
        bool accept;
        try { accept = await _acceptAsync(offer).ConfigureAwait(false); }
        catch { accept = false; }

        // Free-space guard.
        if (accept)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_downloadDirectory))!);
                if (drive.AvailableFreeSpace < offer.Size + 1024 * 1024) accept = false;
            }
            catch { /* if we can't check, proceed */ }
        }

        if (!accept)
        {
            _scheduler.EnqueueHigh(MessageType.FileReject, IdPayload(offer.TransferId));
            return;
        }

        _incoming?.Dispose();
        _incoming = new FileReceiver(_downloadDirectory, offer.FileName, offer.Size);
        _incomingId = offer.TransferId;
        _incomingTotal = offer.Size;
        _scheduler.EnqueueHigh(MessageType.FileAccept, IdPayload(offer.TransferId));
        Progress?.Invoke(offer.TransferId, offer.FileName, 0, offer.Size, true);
    }

    private void OnChunk(FileChunkPayload chunk)
    {
        if (_incoming is null || chunk.TransferId != _incomingId) return;
        try
        {
            _incoming.Write(chunk.Offset, chunk.Data);
            Progress?.Invoke(chunk.TransferId, "", _incoming.Received, _incomingTotal, true);
        }
        catch (Exception ex)
        {
            _scheduler.EnqueueHigh(MessageType.FileError, IdPayload(chunk.TransferId));
            Failed?.Invoke(chunk.TransferId, ex.Message);
            Abort();
        }
    }

    private void OnDone(byte[] payload)
    {
        var r = new PayloadReader(payload);
        string id = r.ReadString();
        byte[] sha = r.ReadBytes();
        if (_incoming is null || id != _incomingId) return;
        try
        {
            string finalPath = _incoming.Complete(sha);
            Completed?.Invoke(id, finalPath);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(id, ex.Message);
        }
        finally
        {
            _incoming = null;
            _incomingId = null;
        }
    }

    private void Abort()
    {
        _incoming?.Dispose();
        _incoming = null;
        _incomingId = null;
    }

    private void Signal(byte[] payload, bool accepted)
    {
        string id = new PayloadReader(payload).ReadString();
        if (_pendingSends.TryGetValue(id, out var tcs)) tcs.TrySetResult(accepted);
    }

    private static byte[] IdPayload(string transferId)
    {
        var w = new PayloadWriter();
        w.WriteString(transferId);
        return w.ToArray();
    }
}
