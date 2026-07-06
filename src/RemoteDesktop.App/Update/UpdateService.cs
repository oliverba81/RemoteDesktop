using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace RemoteDesktop.App.Update;

/// <summary>
/// Self-update from GitHub Releases. Checks the repo's latest release, and — if it
/// is newer than the running build — downloads the win-x64 zip and swaps the
/// install in place via a small updater script that waits for this process to
/// exit (a running single-file exe is locked, so the swap must happen afterwards),
/// then relaunches.
/// </summary>
public static class UpdateService
{
    // The public repo that the CI release workflow publishes to.
    private const string Owner = "oliverba81";
    private const string Repo = "RemoteDesktop";
    private const string AssetName = "RemoteDesktop-win-x64.zip";

    public sealed record UpdateInfo(Version Version, string Tag, string Notes, string AssetUrl);

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Returns the newer release, or null if up to date / offline / no asset.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemoteDesktop", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("prerelease", out JsonElement pre) && pre.GetBoolean()) return null;
        if (!root.TryGetProperty("tag_name", out JsonElement tagEl)) return null;
        string tag = tagEl.GetString() ?? "";
        if (!TryParseTag(tag, out Version version)) return null;
        if (version <= CurrentVersion) return null;

        string? assetUrl = null;
        if (root.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement a in assets.EnumerateArray())
            {
                if (a.TryGetProperty("name", out JsonElement n) &&
                    string.Equals(n.GetString(), AssetName, StringComparison.OrdinalIgnoreCase) &&
                    a.TryGetProperty("browser_download_url", out JsonElement u))
                {
                    assetUrl = u.GetString();
                    break;
                }
            }
        }
        if (assetUrl is null) return null;

        string notes = root.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "";
        return new UpdateInfo(version, tag, notes, assetUrl);
    }

    /// <summary>
    /// Downloads and installs the update, then relaunches. Returns false if the
    /// download/extract failed (the caller keeps running); on success the process
    /// exits so the updater can replace the locked files.
    /// </summary>
    public static async Task<bool> DownloadAndApplyAsync(UpdateInfo update, CancellationToken ct = default)
    {
        string? installDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (installDir is null) return false;

        string workRoot = Path.Combine(Path.GetTempPath(), "RemoteDesktop-update");
        string dlDir = Path.Combine(workRoot, "download");
        string extractDir = Path.Combine(workRoot, "new");
        Directory.CreateDirectory(dlDir);
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        Directory.CreateDirectory(extractDir);

        string zipPath = Path.Combine(dlDir, AssetName);
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RemoteDesktop", "1.0"));
            await using Stream net = await http.GetStreamAsync(update.AssetUrl, ct).ConfigureAwait(false);
            await using var file = File.Create(zipPath);
            await net.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        string exePath = Environment.ProcessPath!;
        string updater = WriteUpdaterScript(extractDir, installDir, exePath);

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{updater}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workRoot,
        };
        Process.Start(psi);
        return true;
    }

    /// <summary>
    /// Writes a batch script that waits for this process to exit, copies the new
    /// files over the install dir, relaunches and deletes itself.
    /// </summary>
    private static string WriteUpdaterScript(string newDir, string installDir, string exePath)
    {
        int pid = Environment.ProcessId;
        string path = Path.Combine(Path.GetTempPath(), "RemoteDesktop-update", "apply-update.cmd");
        string script = $"""
            @echo off
            rem Wait for the running app (PID {pid}) to exit so its files unlock.
            :wait
            tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )
            xcopy /e /y /i "{newDir}\*" "{installDir}" >nul
            start "" "{exePath}"
            del "%~f0"
            """;
        File.WriteAllText(path, script);
        return path;
    }

    // Accepts tags like "v1.2.0" or "1.2.0-0007"; the numeric x.y.z is what matters.
    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        string t = tag.TrimStart('v', 'V');
        int dash = t.IndexOf('-');
        if (dash >= 0) t = t[..dash];
        return Version.TryParse(t, out Version? parsed) && (version = parsed!) is not null;
    }
}
