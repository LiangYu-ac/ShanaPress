namespace ShanaPress.Core.Services;

public static class FfmpegLocator
{
    public static string? ResolveFfmpeg(string? customPath = null) =>
        ResolveExecutable("ffmpeg.exe", customPath);

    public static string? ResolveFfprobe(string? customPath = null, string? resolvedFfmpegPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
        {
            return Path.GetFullPath(customPath);
        }

        if (!string.IsNullOrWhiteSpace(resolvedFfmpegPath))
        {
            var sibling = Path.Combine(Path.GetDirectoryName(resolvedFfmpegPath) ?? string.Empty, "ffprobe.exe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return ResolveExecutable("ffprobe.exe", null);
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

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(dir, exeName));
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            candidates.Add(Path.Combine(userProfile, ".local", "bin", exeName));
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
