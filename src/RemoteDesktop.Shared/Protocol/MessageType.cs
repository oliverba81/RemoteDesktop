namespace RemoteDesktop.Shared.Protocol;

/// <summary>
/// Wire message discriminator. One byte, prepended to every payload.
///
/// The transport has three phases:
///  * Control (plaintext, peer &lt;-&gt; relay): Register/Join/Query/SessionReady/SessionError.
///  * Handshake (plaintext, peer &lt;-&gt; peer through the blind relay pipe): PakeMsg*, Hello.
///    These are safe in the clear — the ephemeral ECDH public keys and password-auth
///    confirmation MACs reveal nothing usable to a passive relay.
///  * Secure (AES-256-GCM, peer &lt;-&gt; peer): everything else.
/// </summary>
public enum MessageType : byte
{
    // --- Relay control plane (plaintext) ---
    RegisterSession = 1,
    JoinSession = 2,
    SessionReady = 3,
    SessionError = 4,
    QueryStatus = 5,
    StatusResponse = 6,

    // --- Encrypted profile sync (plaintext control frames; the blob is opaque) ---
    ProfilePut = 7,
    ProfileGet = 8,
    ProfileData = 9,
    ProfileAck = 10,

    // --- End-to-end handshake (plaintext, blind-piped by the relay) ---
    Hello = 16,
    HelloAck = 17,

    // --- Secure channel (AES-256-GCM) ---
    ScreenFrame = 32,
    FrameAck = 33,
    CursorUpdate = 34,
    QualityChange = 35,

    InputMouse = 48,
    InputKeyboard = 49,
    InputReset = 50,

    ClipboardText = 64,

    FileOffer = 80,
    FileAccept = 81,
    FileReject = 82,
    FileChunk = 83,
    FileChunkAck = 84,
    FileDone = 85,
    FileError = 86,

    Heartbeat = 112,
    Bye = 113,
}

/// <summary>
/// Reasons a <see cref="MessageType.SessionError"/> can carry (first payload byte).
/// </summary>
public enum SessionErrorCode : byte
{
    Unknown = 0,
    SessionNotFound = 1,
    SessionBusy = 2,        // a viewer is already attached
    VersionMismatch = 3,
    RateLimited = 4,
    NotOwner = 5,           // re-register attempt without owner proof
    HostGone = 6,
    Internal = 7,
    ProfileAuthFailed = 8,   // wrong passphrase for an existing profile
    ProfileNotFound = 9,     // no profile stored for this id yet
    ProfileTooLarge = 10,    // blob exceeds the relay's per-profile cap
    ProfileDisabled = 11,    // relay has profile sync turned off
}
