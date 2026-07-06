using System.Security.Cryptography;
using System.Text;

namespace RemoteDesktop.Shared.Crypto;

/// <summary>
/// Client-side crypto for the encrypted profile sync. A user passphrase is
/// stretched once with Argon2id and split (via HKDF) into three independent
/// values:
///
///  * <see cref="ProfileKeys.ProfileId"/> — the public lookup id the relay stores
///    the blob under. Derived from the passphrase, so it reveals nothing: reversing
///    it means brute-forcing an Argon2id-hardened passphrase.
///  * <see cref="ProfileKeys.AuthKey"/> — proves write/read rights for that id. The
///    relay only ever stores SHA-256(authKey), so stolen relay storage cannot be
///    used to overwrite a profile.
///  * <see cref="ProfileKeys.EncKey"/> — AES-256-GCM key for the content. Never
///    leaves the client, so the relay stores an opaque blob it can never read.
///
/// The relay is therefore a blind store: compromise reveals only ciphertext whose
/// decryption still requires the Argon2id-hardened passphrase.
/// </summary>
public static class ProfileCrypto
{
    // Fixed, public salt: the passphrase must map to a stable profile id across
    // machines, so the salt cannot be random. Argon2id still makes each passphrase
    // guess expensive, and the id/keys reveal nothing without the passphrase.
    private static readonly byte[] RootSalt = SHA256.HashData(Encoding.ASCII.GetBytes("rd-profile-salt-v1"));

    public sealed record ProfileKeys(string ProfileId, byte[] AuthKey, byte[] EncKey);

    public static ProfileKeys DeriveKeys(string passphrase)
    {
        byte[] root = KeySchedule.DerivePasswordKey(passphrase, RootSalt, 32);
        try
        {
            byte[] idBytes = Hkdf(root, "rd-profile-id-v1", 16);
            byte[] auth = Hkdf(root, "rd-profile-auth-v1", 32);
            byte[] enc = Hkdf(root, "rd-profile-enc-v1", 32);
            return new ProfileKeys(Convert.ToHexString(idBytes).ToLowerInvariant(), auth, enc);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(root);
        }
    }

    /// <summary>Relay-side verifier for an auth key (what the relay stores/compares).</summary>
    public static byte[] AuthVerifier(ReadOnlySpan<byte> authKey) => SHA256.HashData(authKey);

    /// <summary>Encrypts plaintext to  nonce(12) || ciphertext || tag(16).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> encKey, ReadOnlySpan<byte> plaintext)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using var gcm = new AesGcm(encKey.ToArray(), 16);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var blob = new byte[12 + ciphertext.Length + 16];
        nonce.CopyTo(blob, 0);
        ciphertext.CopyTo(blob, 12);
        tag.CopyTo(blob, 12 + ciphertext.Length);
        return blob;
    }

    /// <summary>Reverses <see cref="Encrypt"/>. Throws on a wrong key or tampering.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> encKey, ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 12 + 16)
            throw new CryptographicException("Profile blob too short.");
        ReadOnlySpan<byte> nonce = blob[..12];
        ReadOnlySpan<byte> tag = blob[^16..];
        ReadOnlySpan<byte> ciphertext = blob[12..^16];
        var plaintext = new byte[ciphertext.Length];
        using var gcm = new AesGcm(encKey.ToArray(), 16);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    private static byte[] Hkdf(ReadOnlySpan<byte> ikm, string info, int length)
    {
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, output,
            salt: RootSalt, info: Encoding.ASCII.GetBytes(info));
        return output;
    }
}
