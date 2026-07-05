using System.Security.Cryptography;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transport;

namespace RemoteDesktop.Shared.Crypto;

/// <summary>Outcome of a successful handshake: the peer's advertised screen size.</summary>
public readonly record struct HandshakeResult(int RemoteWidth, int RemoteHeight);

/// <summary>
/// Runs the peer-to-peer authenticated key exchange over a <see cref="FramedConnection"/>
/// that is already paired through the relay. Both sides send Hello, then exchange
/// HelloAck confirmation MACs; on success the connection is switched to AES-256-GCM.
///
/// A wrong password (or an active MITM without the password) fails the
/// confirmation check and throws — the caller aborts the session.
/// </summary>
public static class SecureHandshake
{
    public static async Task<HandshakeResult> RunAsync(
        FramedConnection conn, string sessionId, string password, bool isHost,
        PixelSize localScreen, CancellationToken ct)
    {
        using var keys = new EphemeralKeyPair();
        byte[] noncePrefix = RandomNumberGenerator.GetBytes(ProtocolConstants.NoncePrefixSize);
        byte[] salt = isHost ? RandomNumberGenerator.GetBytes(16) : Array.Empty<byte>();

        var hello = new HelloPayload(
            ProtocolConstants.Version, isHost, keys.PublicKey, salt, noncePrefix,
            localScreen.Width, localScreen.Height);
        await conn.SendAsync(MessageType.Hello, hello.Serialize(), ct).ConfigureAwait(false);

        Frame? peerFrame = await conn.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new ProtocolException("Connection closed during handshake.");
        if (peerFrame.Value.Type != MessageType.Hello)
            throw new ProtocolException($"Expected Hello, got {peerFrame.Value.Type}.");
        var peerHello = HelloPayload.Parse(peerFrame.Value.Payload);

        if (peerHello.Version != ProtocolConstants.Version)
            throw new ProtocolException($"Protocol version mismatch: local {ProtocolConstants.Version}, peer {peerHello.Version}.");
        if (peerHello.IsHost == isHost)
            throw new ProtocolException("Both peers claim the same role.");

        // The host is authoritative for the salt.
        byte[] sessionSalt = isHost ? salt : peerHello.Salt;
        if (sessionSalt.Length == 0)
            throw new ProtocolException("Missing session salt from host.");

        byte[] hostPub = isHost ? keys.PublicKey : peerHello.EphemeralPublicKey;
        byte[] viewerPub = isHost ? peerHello.EphemeralPublicKey : keys.PublicKey;
        byte[] hostPrefix = isHost ? noncePrefix : peerHello.NoncePrefix;
        byte[] viewerPrefix = isHost ? peerHello.NoncePrefix : noncePrefix;

        byte[] transcript = KeySchedule.BuildTranscript(
            ProtocolConstants.Version, sessionId, hostPub, viewerPub, hostPrefix, viewerPrefix, sessionSalt);

        byte[] shared = keys.DeriveSharedSecret(peerHello.EphemeralPublicKey);
        byte[] passwordKey = KeySchedule.DerivePasswordKey(password, sessionSalt);
        var (s2cKey, c2sKey) = KeySchedule.DeriveDirectionKeys(shared, transcript);

        // Exchange and verify the password-authentication confirmation MACs.
        byte[] myConfirm = KeySchedule.ComputeConfirmation(passwordKey, transcript, isHost);
        await conn.SendAsync(MessageType.HelloAck, new HelloAckPayload(myConfirm).Serialize(), ct).ConfigureAwait(false);

        Frame? ackFrame = await conn.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new ProtocolException("Connection closed before HelloAck.");
        if (ackFrame.Value.Type != MessageType.HelloAck)
            throw new ProtocolException($"Expected HelloAck, got {ackFrame.Value.Type}.");
        byte[] peerConfirm = HelloAckPayload.Parse(ackFrame.Value.Payload).Confirmation;
        byte[] expectedPeerConfirm = KeySchedule.ComputeConfirmation(passwordKey, transcript, !isHost);
        if (!CryptographicOperations.FixedTimeEquals(peerConfirm, expectedPeerConfirm))
            throw new ProtocolException("Password authentication failed (bad confirmation).");

        // Each side encrypts on its own direction key + own nonce prefix.
        AeadCipher send, recv;
        if (isHost)
        {
            send = new AeadCipher(s2cKey, hostPrefix);
            recv = new AeadCipher(c2sKey, viewerPrefix);
        }
        else
        {
            send = new AeadCipher(c2sKey, viewerPrefix);
            recv = new AeadCipher(s2cKey, hostPrefix);
        }
        conn.ActivateEncryption(send, recv);

        CryptographicOperations.ZeroMemory(passwordKey);
        CryptographicOperations.ZeroMemory(shared);

        return new HandshakeResult(peerHello.ScreenWidth, peerHello.ScreenHeight);
    }
}
