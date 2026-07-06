using System.Security.Cryptography;

namespace RemoteDesktop.Relay;

/// <summary>
/// Blind, at-rest storage for encrypted profile blobs (the viewer's address book,
/// synced across machines). Each profile is one file named by its client-derived
/// profile id. The file is  authVerifier(32) || encryptedBlob : the relay checks
/// the caller's auth key against the stored verifier but never sees the passphrase
/// or the plaintext. Compromise of this directory reveals only ciphertext.
/// </summary>
public sealed class ProfileStore
{
    private readonly string _dir;
    private readonly int _maxBlobBytes;

    public ProfileStore(string dir, int maxBlobBytes)
    {
        _dir = dir;
        _maxBlobBytes = maxBlobBytes;
        Directory.CreateDirectory(_dir);
    }

    public enum PutResult { Stored, AuthFailed, TooLarge }
    public enum GetStatus { Ok, NotFound, AuthFailed }

    public PutResult Put(string profileId, byte[] authKey, byte[] blob)
    {
        if (!IsValidId(profileId)) return PutResult.AuthFailed;
        if (blob.Length > _maxBlobBytes) return PutResult.TooLarge;

        byte[] verifier = SHA256.HashData(authKey);
        string path = PathFor(profileId);

        lock (Gate(profileId))
        {
            if (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path);
                if (existing.Length >= 32 &&
                    !CryptographicOperations.FixedTimeEquals(existing.AsSpan(0, 32), verifier))
                    return PutResult.AuthFailed;
            }

            var payload = new byte[32 + blob.Length];
            verifier.CopyTo(payload, 0);
            blob.CopyTo(payload, 32);

            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, payload);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return PutResult.Stored;
        }
    }

    public (GetStatus Status, byte[] Blob) Get(string profileId, byte[] authKey)
    {
        if (!IsValidId(profileId)) return (GetStatus.NotFound, Array.Empty<byte>());
        string path = PathFor(profileId);

        lock (Gate(profileId))
        {
            if (!File.Exists(path)) return (GetStatus.NotFound, Array.Empty<byte>());
            byte[] stored = File.ReadAllBytes(path);
            if (stored.Length < 32) return (GetStatus.NotFound, Array.Empty<byte>());

            byte[] verifier = SHA256.HashData(authKey);
            if (!CryptographicOperations.FixedTimeEquals(stored.AsSpan(0, 32), verifier))
                return (GetStatus.AuthFailed, Array.Empty<byte>());

            return (GetStatus.Ok, stored[32..]);
        }
    }

    // Profile ids are 32 lowercase hex chars (16 bytes) — validate strictly so the
    // id can never escape the storage directory.
    private static bool IsValidId(string id) =>
        id.Length == 32 && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private string PathFor(string profileId) => Path.Combine(_dir, profileId + ".bin");

    // Per-id lock so concurrent put/get for the same profile can't interleave.
    private readonly object[] _locks = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private object Gate(string profileId) => _locks[(uint)profileId.GetHashCode() % _locks.Length];
}
