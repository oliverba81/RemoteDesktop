using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RemoteDesktop.Shared.Crypto;

/// <summary>
/// Derives the session keys and the password-authentication material.
///
/// Design (see plan "Protokoll &amp; Sicherheit"):
///  * Ephemeral ECDH (NIST P-256, BCL only) provides the shared secret and
///    forward secrecy — a later password leak cannot decrypt recorded sessions.
///  * The relay stores no password-derived verifier, so it has no offline oracle
///    from its stored state.
///  * The password gates authentication via an Argon2id-stretched confirmation
///    MAC over the full transcript, so an active MITM must know the password.
///
/// KNOWN LIMITATION (documented in README): the confirmation MAC is a
/// transcript over public values keyed by the Argon2id password key, so a
/// *malicious* relay that records traffic still has an Argon2id-hardened offline
/// dictionary oracle. A true PAKE (CPace) removes even that and is the planned
/// v1.1 hardening. Argon2id (64 MiB, t=3) makes each guess expensive today.
/// </summary>
public static class KeySchedule
{
    // Argon2id parameters. 64 MiB, 3 passes, single lane.
    private const int ArgonMemoryKib = 64 * 1024;
    private const int ArgonIterations = 3;
    private const int ArgonParallelism = 1;

    public static byte[] DerivePasswordKey(string password, ReadOnlySpan<byte> salt, int length = 32)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt.ToArray(),
            DegreeOfParallelism = ArgonParallelism,
            MemorySize = ArgonMemoryKib,
            Iterations = ArgonIterations,
        };
        return argon.GetBytes(length);
    }

    /// <summary>
    /// Builds the byte string that both peers MAC and derive keys over. Order is
    /// fixed and role-independent (host material first, then viewer).
    /// </summary>
    public static byte[] BuildTranscript(
        ushort version, string sessionId,
        ReadOnlySpan<byte> hostEphPub, ReadOnlySpan<byte> viewerEphPub,
        ReadOnlySpan<byte> hostNoncePrefix, ReadOnlySpan<byte> viewerNoncePrefix,
        ReadOnlySpan<byte> salt)
    {
        var ms = new MemoryStream();
        Span<byte> u16 = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(u16, version);
        ms.Write(u16);
        byte[] sid = Encoding.UTF8.GetBytes(sessionId);
        WriteBlock(ms, sid);
        WriteBlock(ms, hostEphPub);
        WriteBlock(ms, viewerEphPub);
        WriteBlock(ms, hostNoncePrefix);
        WriteBlock(ms, viewerNoncePrefix);
        WriteBlock(ms, salt);
        return ms.ToArray();

        static void WriteBlock(MemoryStream s, ReadOnlySpan<byte> b)
        {
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, b.Length);
            s.Write(len);
            s.Write(b);
        }
    }

    /// <summary>HMAC-SHA256(passwordKey, transcript || role) — the confirmation MAC.</summary>
    public static byte[] ComputeConfirmation(ReadOnlySpan<byte> passwordKey, ReadOnlySpan<byte> transcript, bool isHost)
    {
        byte[] label = Encoding.ASCII.GetBytes(isHost ? "rd-host-confirm-v1" : "rd-viewer-confirm-v1");
        using var hmac = new HMACSHA256(passwordKey.ToArray());
        hmac.TransformBlock(transcript.ToArray(), 0, transcript.Length, null, 0);
        hmac.TransformFinalBlock(label, 0, label.Length);
        return hmac.Hash!;
    }

    /// <summary>
    /// Two 32-byte AEAD keys, one per direction, via HKDF-SHA256 over the ECDH
    /// shared secret with the transcript as HKDF salt and distinct info labels.
    /// </summary>
    public static (byte[] hostToViewer, byte[] viewerToHost) DeriveDirectionKeys(
        ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> transcript)
    {
        var s2c = new byte[32];
        var c2s = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, s2c, transcript,
            Encoding.ASCII.GetBytes("rd-s2c-v1"));
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, c2s, transcript,
            Encoding.ASCII.GetBytes("rd-c2s-v1"));
        return (s2c, c2s);
    }

    /// <summary>
    /// The presence key proves address-book ownership to the relay without
    /// revealing the password. Derived from the password with a distinct label so
    /// it is not interchangeable with the confirmation/session material.
    /// </summary>
    public static byte[] DerivePresenceKey(string password, string sessionId)
    {
        // Session-id-scoped, cheap PBKDF (presence is queried often; the security
        // boundary here is only "hide online/offline", not content secrecy).
        byte[] salt = SHA256.HashData(Encoding.UTF8.GetBytes("rd-presence-salt|" + sessionId));
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations: 100_000, HashAlgorithmName.SHA256, 32);
    }

    /// <summary>HMAC(presenceKey, sessionId || serverNonce) — the presence proof.</summary>
    public static byte[] ComputePresenceProof(ReadOnlySpan<byte> presenceKey, string sessionId, ReadOnlySpan<byte> serverNonce)
    {
        using var hmac = new HMACSHA256(presenceKey.ToArray());
        byte[] sid = Encoding.UTF8.GetBytes(sessionId);
        hmac.TransformBlock(sid, 0, sid.Length, null, 0);
        hmac.TransformFinalBlock(serverNonce.ToArray(), 0, serverNonce.Length);
        return hmac.Hash!;
    }
}
