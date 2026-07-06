using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Viewer;

public sealed class AddressBookEntry
{
    public string DisplayName { get; set; } = "";
    public string RelayHost { get; set; } = "";
    public int RelayPort { get; set; } = 9945;
    public string SessionId { get; set; } = "";

    /// <summary>DPAPI-protected password, base64. Null if not stored.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Transient, never persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? Online { get; set; }
}

/// <summary>
/// Address book of known targets. Stored in %LOCALAPPDATA% (not Roaming — DPAPI
/// CurrentUser blobs are machine+user bound and would not decrypt after roaming).
/// Passwords are encrypted per field so a single bad blob never loses the whole
/// book, and writes are atomic.
/// </summary>
public sealed class AddressBook
{
    public int SchemaVersion { get; set; } = 1;
    public List<AddressBookEntry> Entries { get; set; } = new();

    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("RemoteDesktop-addressbook-v1");

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesktop");
    private static string FilePath => Path.Combine(Dir, "addressbook.json");

    public static AddressBook Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AddressBook>(File.ReadAllText(FilePath)) ?? new AddressBook();
        }
        catch { /* corrupt → start fresh rather than crash */ }
        return new AddressBook();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak");
        else File.Move(tmp, FilePath);
    }

    public static string? Protect(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    /// <summary>Returns the plaintext password, or null if absent or undecryptable
    /// (e.g. the blob was copied from another machine/user).</summary>
    public static string? Unprotect(string? protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword)) return null;
        try
        {
            byte[] blob = Convert.FromBase64String(protectedPassword);
            byte[] plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}
