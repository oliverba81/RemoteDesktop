using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RemoteDesktop.Shared.Crypto;
using RemoteDesktop.Shared.Protocol;
using RemoteDesktop.Shared.Transport;

namespace RemoteDesktop.Viewer;

/// <summary>
/// Uploads and downloads the address book as an encrypted blob through the relay.
/// The passphrase never leaves this process: it is stretched into a lookup id,
/// an auth key and an AES key locally, and only ciphertext plus a hash-derived
/// auth key reach the relay.
/// </summary>
public static class ProfileSync
{
    public sealed class SyncException : Exception
    {
        public SyncException(string message) : base(message) { }
    }

    public static async Task UploadAsync(
        string relayHost, int relayPort, string passphrase, AddressBook book, CancellationToken ct)
    {
        ProfileCrypto.ProfileKeys keys = ProfileCrypto.DeriveKeys(passphrase);
        byte[] plaintext = Encoding.UTF8.GetBytes(SerializeBook(book));
        byte[] blob = ProfileCrypto.Encrypt(keys.EncKey, plaintext);

        Frame resp = await RoundTripAsync(relayHost, relayPort, MessageType.ProfilePut,
            new ProfilePutPayload(ProtocolConstants.Version, keys.ProfileId, keys.AuthKey, blob).Serialize(), ct);

        if (resp.Type == MessageType.ProfileAck) return;
        throw Explain(resp);
    }

    /// <summary>Downloads and decrypts the remote book, or null if none is stored yet.</summary>
    public static async Task<AddressBook?> DownloadAsync(
        string relayHost, int relayPort, string passphrase, CancellationToken ct)
    {
        ProfileCrypto.ProfileKeys keys = ProfileCrypto.DeriveKeys(passphrase);

        Frame resp = await RoundTripAsync(relayHost, relayPort, MessageType.ProfileGet,
            new ProfileGetPayload(ProtocolConstants.Version, keys.ProfileId, keys.AuthKey).Serialize(), ct);

        if (resp.Type == MessageType.SessionError)
        {
            SessionErrorPayload err = SessionErrorPayload.Parse(resp.Payload);
            if (err.Code == SessionErrorCode.ProfileNotFound) return null;
            throw Explain(resp);
        }
        if (resp.Type != MessageType.ProfileData)
            throw new SyncException($"Unexpected relay reply ({resp.Type}).");

        byte[] blob = ProfileDataPayload.Parse(resp.Payload).Blob;
        byte[] plaintext;
        try
        {
            plaintext = ProfileCrypto.Decrypt(keys.EncKey, blob);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new SyncException("Could not decrypt the profile — wrong passphrase.");
        }
        return DeserializeBook(Encoding.UTF8.GetString(plaintext));
    }

    private static async Task<Frame> RoundTripAsync(
        string relayHost, int relayPort, MessageType type, byte[] payload, CancellationToken ct)
    {
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(relayHost, relayPort, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new SyncException($"Cannot reach the relay: {ex.Message}");
        }
        client.NoDelay = true;
        using var conn = new FramedConnection(client.GetStream());
        await conn.SendAsync(type, payload, ct).ConfigureAwait(false);
        Frame? resp = await conn.ReceiveAsync(ct).ConfigureAwait(false);
        if (resp is null) throw new SyncException("Relay closed the connection.");
        return resp.Value;
    }

    private static SyncException Explain(Frame resp)
    {
        if (resp.Type == MessageType.SessionError)
            return new SyncException(SessionErrorPayload.Parse(resp.Payload).Message);
        return new SyncException($"Unexpected relay reply ({resp.Type}).");
    }

    // The synced document is the entries only (with their DPAPI-protected passwords
    // dropped — those are machine-bound and would not decrypt elsewhere). Passwords
    // still travel end-to-end: the whole document is re-encrypted under the sync
    // passphrase, and a plaintext password field is carried so it survives the trip.
    private sealed record SyncEntry(string Name, string RelayHost, int RelayPort, string SessionId, string? Password);
    private sealed record SyncDoc(int Version, List<SyncEntry> Entries);

    private static string SerializeBook(AddressBook book)
    {
        var doc = new SyncDoc(1, book.Entries.Select(e => new SyncEntry(
            e.DisplayName, e.RelayHost, e.RelayPort, e.SessionId,
            AddressBook.Unprotect(e.ProtectedPassword))).ToList());
        return JsonSerializer.Serialize(doc);
    }

    private static AddressBook DeserializeBook(string json)
    {
        SyncDoc? doc = JsonSerializer.Deserialize<SyncDoc>(json);
        var book = new AddressBook();
        if (doc is null) return book;
        foreach (SyncEntry e in doc.Entries)
        {
            book.Entries.Add(new AddressBookEntry
            {
                DisplayName = e.Name,
                RelayHost = e.RelayHost,
                RelayPort = e.RelayPort,
                SessionId = e.SessionId,
                ProtectedPassword = AddressBook.Protect(e.Password),
            });
        }
        return book;
    }
}
