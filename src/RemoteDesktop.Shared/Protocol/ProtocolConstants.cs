namespace RemoteDesktop.Shared.Protocol;

public static class ProtocolConstants
{
    /// <summary>
    /// Wire protocol version. Bumped on any breaking frame-format change. The relay
    /// and both peers exchange it in Register/Join/Hello and refuse a mismatched
    /// major version, because the relay (.spk on the NAS) and the clients are
    /// updated independently and will drift.
    /// </summary>
    public const ushort Version = 1;

    /// <summary>Absolute hard cap on a single frame body. Real frames are far smaller.</summary>
    public const int MaxFrameBody = 16 * 1024 * 1024;

    // Per-type sane limits, checked before allocating, so a corrupt length field
    // near MaxFrameBody cannot force a 16 MB allocation for every frame.
    public const int MaxScreenFrameBody = 4 * 1024 * 1024;
    public const int MaxControlBody = 64 * 1024;

    /// <summary>File transfer chunk size.</summary>
    public const int FileChunkSize = 64 * 1024;

    /// <summary>Clipboard text is capped; larger content is skipped, not truncated.</summary>
    public const int MaxClipboardBytes = 1 * 1024 * 1024;

    /// <summary>GCM authentication tag length.</summary>
    public const int GcmTagSize = 16;

    /// <summary>Nonce = 4-byte per-direction random prefix || 8-byte counter.</summary>
    public const int NoncePrefixSize = 4;
    public const int NonceCounterSize = 8;
    public const int NonceSize = NoncePrefixSize + NonceCounterSize;
}
