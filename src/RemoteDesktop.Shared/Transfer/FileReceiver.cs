using System.Security.Cryptography;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Util;

namespace RemoteDesktop.Shared.Transfer;

/// <summary>
/// Receives an incoming file: writes chunks sequentially to a <c>.part</c> file,
/// hashes them, verifies the sender's SHA-256, then atomically renames to a
/// collision-free final name. The <c>.part</c> also enables resume. Pure I/O, so
/// it is unit-testable on Linux.
/// </summary>
public sealed class FileReceiver : IDisposable
{
    private readonly string _directory;
    private readonly string _finalName;
    private readonly string _partPath;
    private readonly FileStream _stream;
    private readonly IncrementalHash _hash;
    private bool _completed;

    public FileReceiver(string downloadDirectory, string proposedName, long expectedSize)
    {
        Directory.CreateDirectory(downloadDirectory);
        _directory = downloadDirectory;
        _finalName = SafeFileName.Sanitize(proposedName);
        ExpectedSize = expectedSize;
        _partPath = Path.Combine(_directory, _finalName + ".part");
        _stream = new FileStream(_partPath, FileMode.Create, FileAccess.Write, FileShare.None);
        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }

    public long ExpectedSize { get; }
    public long Received { get; private set; }

    /// <summary>Writes a chunk. v1 requires in-order delivery (offset == Received).</summary>
    public void Write(long offset, ReadOnlySpan<byte> data)
    {
        if (offset != Received)
            throw new ProtocolException($"Out-of-order chunk: expected offset {Received}, got {offset}.");
        if (Received + data.Length > ExpectedSize)
            throw new ProtocolException("Chunk exceeds declared file size.");
        _stream.Write(data);
        _hash.AppendData(data);
        Received += data.Length;
    }

    /// <summary>Verifies the hash and finalizes; returns the final path.</summary>
    public string Complete(ReadOnlySpan<byte> expectedSha256)
    {
        _stream.Flush();
        _stream.Dispose();
        byte[] actual = _hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedSha256))
        {
            TryDeletePart();
            throw new ProtocolException("File hash mismatch — transfer corrupted.");
        }
        string finalPath = SafeFileName.ResolveCollision(_directory, _finalName);
        File.Move(_partPath, finalPath);
        _completed = true;
        return finalPath;
    }

    private void TryDeletePart()
    {
        try { if (File.Exists(_partPath)) File.Delete(_partPath); } catch { /* ignore */ }
    }

    public void Dispose()
    {
        if (_completed) return;
        try { _stream.Dispose(); } catch { /* ignore */ }
        TryDeletePart();
    }
}
