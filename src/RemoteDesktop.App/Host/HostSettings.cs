using System.Text.Json;

namespace RemoteDesktop.Host;

/// <summary>
/// Host settings persisted to %LOCALAPPDATA%. DPAPI-bound secrets (the permanent
/// password for unattended mode, a v1.1 feature) live here too and must NOT roam,
/// which is why this is LocalAppData, not Roaming.
/// </summary>
public sealed class HostSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string RelayHost { get; set; } = "";
    public int RelayPort { get; set; } = 9945;

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
}
