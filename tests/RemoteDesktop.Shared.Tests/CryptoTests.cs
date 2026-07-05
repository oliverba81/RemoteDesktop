using System.Security.Cryptography;
using RemoteDesktop.Shared.Crypto;
using RemoteDesktop.Shared.Protocol;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

public class CryptoTests
{
    [Fact]
    public void Aead_SealOpen_RoundTrips()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] prefix = RandomNumberGenerator.GetBytes(4);
        using var send = new AeadCipher(key, prefix);
        using var recv = new AeadCipher(key, prefix);

        byte[] plaintext = System.Text.Encoding.UTF8.GetBytes("secret payload");
        byte[] sealed0 = send.Seal(0, plaintext);
        Assert.Equal(plaintext, recv.Open(0, sealed0));
    }

    [Fact]
    public void Aead_Tamper_Throws()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] prefix = RandomNumberGenerator.GetBytes(4);
        using var send = new AeadCipher(key, prefix);
        using var recv = new AeadCipher(key, prefix);

        byte[] sealed0 = send.Seal(0, new byte[] { 1, 2, 3, 4 });
        sealed0[0] ^= 0xFF; // flip a ciphertext bit
        Assert.ThrowsAny<CryptographicException>(() => recv.Open(0, sealed0));
    }

    [Fact]
    public void Aead_WrongCounter_Throws()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] prefix = RandomNumberGenerator.GetBytes(4);
        using var send = new AeadCipher(key, prefix);
        using var recv = new AeadCipher(key, prefix);

        byte[] sealed0 = send.Seal(5, new byte[] { 1, 2, 3, 4 });
        Assert.ThrowsAny<CryptographicException>(() => recv.Open(6, sealed0));
    }

    [Fact]
    public void Ecdh_BothSides_AgreeOnSecret()
    {
        using var a = new EphemeralKeyPair();
        using var b = new EphemeralKeyPair();
        byte[] sa = a.DeriveSharedSecret(b.PublicKey);
        byte[] sb = b.DeriveSharedSecret(a.PublicKey);
        Assert.Equal(sa, sb);
    }

    [Fact]
    public void DirectionKeys_AreDistinct()
    {
        byte[] shared = RandomNumberGenerator.GetBytes(32);
        byte[] transcript = RandomNumberGenerator.GetBytes(48);
        var (s2c, c2s) = KeySchedule.DeriveDirectionKeys(shared, transcript);
        Assert.NotEqual(s2c, c2s);
        Assert.Equal(32, s2c.Length);
    }

    [Fact]
    public void Confirmation_MatchesForSamePassword_DiffersByRole()
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] pwKey = KeySchedule.DerivePasswordKey("hunter2!", salt);
        byte[] transcript = RandomNumberGenerator.GetBytes(48);

        byte[] hostConfirm = KeySchedule.ComputeConfirmation(pwKey, transcript, isHost: true);
        byte[] viewerConfirm = KeySchedule.ComputeConfirmation(pwKey, transcript, isHost: false);
        Assert.NotEqual(hostConfirm, viewerConfirm);

        byte[] pwKey2 = KeySchedule.DerivePasswordKey("hunter2!", salt);
        byte[] hostConfirm2 = KeySchedule.ComputeConfirmation(pwKey2, transcript, isHost: true);
        Assert.Equal(hostConfirm, hostConfirm2);
    }

    [Fact]
    public void PresenceProof_IsDeterministic_AndPasswordBound()
    {
        byte[] k1 = KeySchedule.DerivePresenceKey("pw", "123456789");
        byte[] k2 = KeySchedule.DerivePresenceKey("pw", "123456789");
        Assert.Equal(k1, k2);
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        byte[] p1 = KeySchedule.ComputePresenceProof(k1, "123456789", nonce);
        byte[] pWrong = KeySchedule.ComputePresenceProof(
            KeySchedule.DerivePresenceKey("other", "123456789"), "123456789", nonce);
        Assert.NotEqual(p1, pWrong);
    }
}
