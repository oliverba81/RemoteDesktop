using System.Security.Cryptography;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transfer;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

public class TransferTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rd-xfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void FileTransfer_RoundTrips_ThroughChunks()
    {
        string srcDir = TempDir();
        string dstDir = TempDir();
        try
        {
            // Source file spanning several chunks.
            string srcPath = Path.Combine(srcDir, "payload.bin");
            byte[] content = RandomNumberGenerator.GetBytes(64 * 1024 * 2 + 123);
            File.WriteAllBytes(srcPath, content);

            FileOfferPayload offer = FileStreamer.CreateOffer("t1", srcPath);
            byte[] sha = FileStreamer.ComputeSha256(srcPath);

            using var receiver = new FileReceiver(dstDir, offer.FileName, offer.Size);
            foreach (FileChunkPayload chunk in FileStreamer.ReadChunks("t1", srcPath))
                receiver.Write(chunk.Offset, chunk.Data);

            string finalPath = receiver.Complete(sha);
            Assert.True(File.Exists(finalPath));
            Assert.Equal(content, File.ReadAllBytes(finalPath));
        }
        finally
        {
            Directory.Delete(srcDir, true);
            Directory.Delete(dstDir, true);
        }
    }

    [Fact]
    public void FileReceiver_HashMismatch_Throws_AndLeavesNoFile()
    {
        string dstDir = TempDir();
        try
        {
            using var receiver = new FileReceiver(dstDir, "file.bin", 4);
            receiver.Write(0, new byte[] { 1, 2, 3, 4 });
            Assert.Throws<ProtocolException>(() => receiver.Complete(new byte[32])); // wrong hash
            Assert.Empty(Directory.GetFiles(dstDir)); // .part cleaned up, no final file
        }
        finally { Directory.Delete(dstDir, true); }
    }

    [Fact]
    public void FileReceiver_OutOfOrderChunk_Throws()
    {
        string dstDir = TempDir();
        try
        {
            using var receiver = new FileReceiver(dstDir, "file.bin", 10);
            Assert.Throws<ProtocolException>(() => receiver.Write(5, new byte[] { 1 }));
        }
        finally { Directory.Delete(dstDir, true); }
    }

    [Fact]
    public void FileReceiver_SanitizesMaliciousName()
    {
        string dstDir = TempDir();
        try
        {
            byte[] data = { 9, 9 };
            byte[] sha = SHA256.HashData(data);
            using var receiver = new FileReceiver(dstDir, "../../evil.bin", data.Length);
            receiver.Write(0, data);
            string finalPath = receiver.Complete(sha);
            // Must stay inside the download dir.
            Assert.Equal(dstDir, Path.GetDirectoryName(finalPath));
            Assert.Equal("evil.bin", Path.GetFileName(finalPath));
        }
        finally { Directory.Delete(dstDir, true); }
    }

    [Fact]
    public void ClipboardLoopGuard_SuppressesEcho_Once()
    {
        var guard = new ClipboardLoopGuard();
        guard.SuppressNext("hello");
        Assert.False(guard.ShouldSend("hello")); // the echo of what we just set
        Assert.True(guard.ShouldSend("hello"));  // a genuine re-copy is sent
    }

    [Fact]
    public void ClipboardLoopGuard_UnrelatedValue_IsSent()
    {
        var guard = new ClipboardLoopGuard();
        guard.SuppressNext("received");
        Assert.True(guard.ShouldSend("something the user copied"));
    }
}
