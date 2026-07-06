using System.Text;
using RemoteDesktop.Relay;
using RemoteDesktop.Shared.Crypto;
using Xunit;

namespace RemoteDesktop.Shared.Tests;

public class ProfileSyncTests
{
    [Fact]
    public void SamePassphrase_DerivesSameIdAndKeys()
    {
        var a = ProfileCrypto.DeriveKeys("correct horse battery staple");
        var b = ProfileCrypto.DeriveKeys("correct horse battery staple");
        Assert.Equal(a.ProfileId, b.ProfileId);
        Assert.Equal(a.AuthKey, b.AuthKey);
        Assert.Equal(a.EncKey, b.EncKey);
        Assert.Equal(32, a.ProfileId.Length);
    }

    [Fact]
    public void DifferentPassphrase_DerivesDifferentId()
    {
        var a = ProfileCrypto.DeriveKeys("passphrase one");
        var b = ProfileCrypto.DeriveKeys("passphrase two");
        Assert.NotEqual(a.ProfileId, b.ProfileId);
        Assert.NotEqual(a.AuthKey, b.AuthKey);
    }

    [Fact]
    public void EncryptDecrypt_RoundTrips()
    {
        var keys = ProfileCrypto.DeriveKeys("sync me");
        byte[] plaintext = Encoding.UTF8.GetBytes("{\"entries\":[]}");
        byte[] blob = ProfileCrypto.Encrypt(keys.EncKey, plaintext);
        Assert.NotEqual(plaintext, blob[..plaintext.Length]); // actually encrypted
        byte[] back = ProfileCrypto.Decrypt(keys.EncKey, blob);
        Assert.Equal(plaintext, back);
    }

    [Fact]
    public void Decrypt_WithWrongKey_Throws()
    {
        var a = ProfileCrypto.DeriveKeys("right");
        var b = ProfileCrypto.DeriveKeys("wrong");
        byte[] blob = ProfileCrypto.Encrypt(a.EncKey, Encoding.UTF8.GetBytes("secret"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => ProfileCrypto.Decrypt(b.EncKey, blob));
    }

    [Fact]
    public void Store_PutThenGet_ReturnsBlob()
    {
        string dir = NewTempDir();
        var store = new ProfileStore(dir, 1024 * 1024);
        var keys = ProfileCrypto.DeriveKeys("passphrase");
        byte[] blob = ProfileCrypto.Encrypt(keys.EncKey, Encoding.UTF8.GetBytes("hello"));

        Assert.Equal(ProfileStore.PutResult.Stored, store.Put(keys.ProfileId, keys.AuthKey, blob));
        var (status, got) = store.Get(keys.ProfileId, keys.AuthKey);
        Assert.Equal(ProfileStore.GetStatus.Ok, status);
        Assert.Equal(blob, got);
    }

    [Fact]
    public void Store_Get_WithWrongAuthKey_Fails()
    {
        string dir = NewTempDir();
        var store = new ProfileStore(dir, 1024 * 1024);
        var owner = ProfileCrypto.DeriveKeys("owner-pass");
        byte[] blob = ProfileCrypto.Encrypt(owner.EncKey, Encoding.UTF8.GetBytes("x"));
        store.Put(owner.ProfileId, owner.AuthKey, blob);

        // Same id, tampered auth key → rejected.
        byte[] wrongAuth = (byte[])owner.AuthKey.Clone();
        wrongAuth[0] ^= 0xFF;
        var (status, _) = store.Get(owner.ProfileId, wrongAuth);
        Assert.Equal(ProfileStore.GetStatus.AuthFailed, status);
    }

    [Fact]
    public void Store_Overwrite_RequiresMatchingAuthKey()
    {
        string dir = NewTempDir();
        var store = new ProfileStore(dir, 1024 * 1024);
        var owner = ProfileCrypto.DeriveKeys("owner");
        store.Put(owner.ProfileId, owner.AuthKey, new byte[] { 1, 2, 3 });

        byte[] wrongAuth = (byte[])owner.AuthKey.Clone();
        wrongAuth[5] ^= 0xFF;
        Assert.Equal(ProfileStore.PutResult.AuthFailed,
            store.Put(owner.ProfileId, wrongAuth, new byte[] { 9 }));
    }

    [Fact]
    public void Store_Get_Missing_ReturnsNotFound()
    {
        var store = new ProfileStore(NewTempDir(), 1024 * 1024);
        var keys = ProfileCrypto.DeriveKeys("nobody");
        var (status, _) = store.Get(keys.ProfileId, keys.AuthKey);
        Assert.Equal(ProfileStore.GetStatus.NotFound, status);
    }

    [Fact]
    public void Store_Put_OversizedBlob_Rejected()
    {
        var store = new ProfileStore(NewTempDir(), maxBlobBytes: 16);
        var keys = ProfileCrypto.DeriveKeys("big");
        Assert.Equal(ProfileStore.PutResult.TooLarge,
            store.Put(keys.ProfileId, keys.AuthKey, new byte[64]));
    }

    private static string NewTempDir()
    {
        // Guid.NewGuid avoids the banned Date/random APIs and collisions across tests.
        string dir = Path.Combine(Path.GetTempPath(), "rd-profile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
