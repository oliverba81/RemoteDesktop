using System.Security.Cryptography;

namespace RemoteDesktop.Shared.Crypto;

/// <summary>
/// An ephemeral ECDH key pair (NIST P-256, BCL only — no native dependency, so it
/// also runs on the Synology relay). Discarded after the session, giving forward
/// secrecy.
/// </summary>
public sealed class EphemeralKeyPair : IDisposable
{
    private readonly ECDiffieHellman _ecdh;

    public EphemeralKeyPair()
    {
        _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = _ecdh.PublicKey.ExportSubjectPublicKeyInfo();
    }

    /// <summary>This peer's public key as a portable SubjectPublicKeyInfo blob.</summary>
    public byte[] PublicKey { get; }

    /// <summary>Raw ECDH shared secret against the peer's SPKI public key.</summary>
    public byte[] DeriveSharedSecret(byte[] peerPublicKeySpki)
    {
        using var peer = ECDiffieHellman.Create();
        try
        {
            peer.ImportSubjectPublicKeyInfo(peerPublicKeySpki, out _);
        }
        catch (CryptographicException ex)
        {
            throw new Protocol.ProtocolException("Invalid peer public key.", ex);
        }
        return _ecdh.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public void Dispose() => _ecdh.Dispose();
}
