namespace RemoteDesktop.Shared.Util;

/// <summary>
/// Sanitizes a filename received from an untrusted peer. Path.GetFileName alone
/// stops directory traversal but not Windows device names, trailing dots/spaces,
/// alternate-data-stream colons, or invalid characters — all handled here. The
/// caller must additionally verify the resolved full path stays inside the target
/// directory.
/// </summary>
public static class SafeFileName
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Superset of Path.GetInvalidFileNameChars() that is stable across platforms
    // (we sanitize on the receiver which may be building on Linux CI).
    private static readonly char[] Invalid =
        Enumerable.Range(0, 32).Select(i => (char)i)
            .Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' })
            .ToArray();

    public static string Sanitize(string proposed, string fallback = "download")
    {
        // Strip any directory component the peer may have included.
        string name = proposed;
        int lastSlash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSlash >= 0) name = name[(lastSlash + 1)..];

        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
        name = sb.ToString();

        // Trailing dots and spaces are stripped by Windows and can alias files.
        name = name.TrimEnd(' ', '.');

        if (name.Length == 0) return fallback;

        // Reserved device name (with or without extension) → prefix.
        string stem = name;
        int dot = stem.IndexOf('.');
        if (dot >= 0) stem = stem[..dot];
        if (ReservedNames.Contains(stem)) name = "_" + name;

        // Keep it well under MAX_PATH once the directory + " (1)" + ".part" is added.
        const int maxLen = 200;
        if (name.Length > maxLen)
        {
            string ext = Path.GetExtension(name);
            if (ext.Length > 16) ext = "";
            name = name[..(maxLen - ext.Length)] + ext;
        }

        return name;
    }

    /// <summary>Returns a non-colliding path in <paramref name="directory"/> by
    /// appending " (1)", " (2)", … before the extension if needed.</summary>
    public static string ResolveCollision(string directory, string fileName)
    {
        string candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 1; i < 10_000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){ext}");
    }
}
