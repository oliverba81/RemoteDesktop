using System.Security.Cryptography;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Transfer;

/// <summary>Reads an outgoing file into chunks and computes its SHA-256. Testable on Linux.</summary>
public static class FileStreamer
{
    public static FileOfferPayload CreateOffer(string transferId, string path)
    {
        var info = new FileInfo(path);
        return new FileOfferPayload(transferId, Path.GetFileName(path), info.Length);
    }

    /// <summary>Yields <see cref="FileChunkPayload"/>s reading from <paramref name="offset"/>
    /// (for resume) to end of file.</summary>
    public static IEnumerable<FileChunkPayload> ReadChunks(
        string transferId, string path, int chunkSize = ProtocolConstants.FileChunkSize, long offset = 0)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[chunkSize];
        long pos = offset;
        int read;
        while ((read = fs.Read(buffer, 0, chunkSize)) > 0)
        {
            yield return new FileChunkPayload(transferId, pos, buffer.AsSpan(0, read).ToArray());
            pos += read;
        }
    }

    public static byte[] ComputeSha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        return sha.ComputeHash(fs);
    }
}
