using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteDesktop.Host;

/// <summary>
/// Host settings persisted to %LOCALAPPDATA%. DPAPI-bound secrets (the permanent
/// device password for unattended mode) live here too and must NOT roam, which is
/// why this is LocalAppData, not Roaming.
/// </summary>
public sealed class HostSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string RelayHost { get; set; } = "";
    public int RelayPort { get; set; } = 9945;

    /// <summary>Zero-based index into the monitor list; clamped on use.</summary>
    public int MonitorIndex { get; set; }

    // ---- unattended access ----

    public bool UnattendedEnabled { get; set; }

    /// <summary>Stable high-entropy session id (generated once, then kept).</summary>
    public string UnattendedId { get; set; } = "";

    /// <summary>DPAPI-protected device password, base64. Null if never set.</summary>
    public string? ProtectedUnattendedPassword { get; set; }

    private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("RemoteDesktop-host-v1");

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesktop");
    private static string FilePath => Path.Combine(Dir, "host.json");

    public static HostSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(FilePath)) ?? new HostSettings();
        }
        catch { /* corrupt/missing → defaults */ }
        return new HostSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        // Atomic replace so a crash mid-write cannot corrupt the file.
        if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
        else File.Move(tmp, FilePath);
    }

    public static string? Protect(string? password)
    {
        if (string.IsNullOrEmpty(password)) return null;
        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(blob);
    }

    /// <summary>Returns the plaintext, or null if absent or undecryptable
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
