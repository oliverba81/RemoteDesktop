using System.Buffers.Binary;
using System.Security.Cryptography;
using RemoteDesktop.Shared.Protocol;

namespace RemoteDesktop.Shared.Crypto;

/// <summary>
/// One-directional AES-256-GCM channel. The 12-byte nonce is a 4-byte
/// per-direction random prefix (exchanged in Hello) concatenated with an 8-byte
/// counter that is transmitted explicitly in every frame — so a reconnect or a
/// dropped frame can never silently desynchronize the keystream.
///
/// Direction-separated keys (see <see cref="KeySchedule"/>) make a (key, nonce)
/// reuse between the two directions structurally impossible, independent of the
/// random prefixes.
/// </summary>
public sealed class AeadCipher : IDisposable
{
    private readonly AesGcm _gcm;
    private readonly byte[] _noncePrefix;

    public AeadCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> noncePrefix)
    {
        if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes.", nameof(key));
        if (noncePrefix.Length != ProtocolConstants.NoncePrefixSize)
            throw new ArgumentException("Nonce prefix must be 4 bytes.", nameof(noncePrefix));
        _gcm = new AesGcm(key, ProtocolConstants.GcmTagSize);
        _noncePrefix = noncePrefix.ToArray();
    }

    private void BuildNonce(ulong counter, Span<byte> nonce)
    {
        _noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[ProtocolConstants.NoncePrefixSize..], counter);
    }

    /// <summary>Encrypts <paramref name="plaintext"/> and returns ciphertext||tag.</summary>
    public byte[] Seal(ulong counter, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[ProtocolConstants.NonceSize];
        BuildNonce(counter, nonce);

        var output = new byte[plaintext.Length + ProtocolConstants.GcmTagSize];
        Span<byte> ciphertext = output.AsSpan(0, plaintext.Length);
        Span<byte> tag = output.AsSpan(plaintext.Length);
        _gcm.Encrypt(nonce, plaintext, ciphertext, tag);
        return output;
    }

    /// <summary>Decrypts ciphertext||tag. Throws <see cref="CryptographicException"/>
    /// on any tampering (the GCM tag check), which the caller treats as a fatal
    /// protocol violation.</summary>
    public byte[] Open(ulong counter, ReadOnlySpan<byte> ciphertextAndTag)
    {
        if (ciphertextAndTag.Length < ProtocolConstants.GcmTagSize)
            throw new ProtocolException("AEAD frame too short for tag.");
        Span<byte> nonce = stackalloc byte[ProtocolConstants.NonceSize];
        BuildNonce(counter, nonce);

        int ptLen = ciphertextAndTag.Length - ProtocolConstants.GcmTagSize;
        ReadOnlySpan<byte> ciphertext = ciphertextAndTag[..ptLen];
        ReadOnlySpan<byte> tag = ciphertextAndTag[ptLen..];
        var plaintext = new byte[ptLen];
        _gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public void Dispose() => _gcm.Dispose();
}
