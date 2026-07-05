using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Shared.Transfer;

/// <summary>
/// Prevents clipboard sync feedback loops without relying on a timing window.
/// When we set the clipboard from a received value we arm a one-shot suppress
/// token; the matching local clipboard-update is then swallowed exactly once, so
/// the value is not echoed back. A genuinely re-copied identical value (a second
/// update with the same content but no armed token) is still sent.
/// </summary>
public sealed class ClipboardLoopGuard
{
    private string? _suppressHash;

    /// <summary>Arm suppression for the next local update carrying this text
    /// (call right before writing a received value to the clipboard).</summary>
    public void SuppressNext(string text) => _suppressHash = Hash(text);

    /// <summary>Returns true if a locally-observed clipboard value should be sent
    /// to the peer. Consumes the suppress token on a match.</summary>
    public bool ShouldSend(string text)
    {
        string h = Hash(text);
        if (_suppressHash is not null && _suppressHash == h)
        {
            _suppressHash = null;
            return false;
        }
        return true;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
