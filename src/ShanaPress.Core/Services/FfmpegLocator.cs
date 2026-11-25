namespace ShanaPress.Core.Services;

public static class FfmpegLocator
{
    public static string ExecutableSuffix => OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    public static string? ResolveFfmpeg(string? customPath = null) =>
        ResolveExecutable("ffmpeg" + ExecutableSuffix, customPath);

    public static string? ResolveFfprobe(string? customPath = null, string? resolvedFfmpegPath = null)
    {
        var exeName = "ffprobe" + ExecutableSuffix;

        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return Path.GetFullPath(customPath);
        }

        if (!string.IsNullOrWhiteSpace(resolvedFfmpegPath))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(resolvedFfmpegPath) ?? string.Empty, exeName);
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return ResolveExecutable(exeName, null);
    }

    private static string? ResolveExecutable(string exeName, string? customPath)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return Path.GetFullPath(customPath);
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, exeName),
            Path.Combine(baseDir, "ffmpeg", exeName),
            Path.Combine(baseDir, "tools", exeName)
        };

        if (OperatingSystem.IsMacOS())
        {
            // macOS .app bundle: Contents/MacOS/ -> Contents/Resources/ffmpeg/
            candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", "ffmpeg", exeName)));
            candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "Resources", exeName)));
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(dir, exeName));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, ".local", "bin", exeName));
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(userProfile))
            {
                candidates.Add(Path.Combine(userProfile, "scoop", "shims", exeName));
            }
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exeName));
            }
            candidates.Add(Path.Combine(@"C:\ffmpeg\bin", exeName));
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                candidates.Add(Path.Combine(programFiles, "ffmpeg", "bin", exeName));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(Path.Combine("/opt/homebrew/bin", exeName));
            candidates.Add(Path.Combine("/usr/local/bin", exeName));
            candidates.Add(Path.Combine("/opt/local/bin", exeName));
        }
        else
        {
            candidates.Add(Path.Combine("/usr/bin", exeName));
            candidates.Add(Path.Combine("/usr/local/bin", exeName));
            candidates.Add(Path.Combine("/snap/bin", exeName));
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // Ignore malformed PATH entries
            }
        }

        return null;
    }
}
