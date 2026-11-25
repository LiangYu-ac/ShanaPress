using System.Globalization;

namespace ShanaPress.Core.Services;

public static class BitrateCalculator
{
    public const double DefaultOverheadReserveRatio = 0.025;

    public static int CalculateTargetVideoBitrateKbps(
        double targetSizeMb,
        double effectiveDurationSeconds,
        int audioBitrateKbps,
        bool hasAudio)
    {
        if (targetSizeMb <= 0 || effectiveDurationSeconds <= 0.1)
        {
            return 1000;
        }

        // Reserve 2.5% for container muxing overhead (MP4/MKV headers, indices)
        var usableBits = targetSizeMb * 1024.0 * 1024.0 * 8.0 * (1.0 - DefaultOverheadReserveRatio);
        var totalKbps = usableBits / (effectiveDurationSeconds * 1000.0);
        var effectiveAudioKbps = hasAudio ? Math.Max(0, audioBitrateKbps) : 0;
        var videoKbps = (int)Math.Floor(totalKbps - effectiveAudioKbps);
        return Math.Max(100, videoKbps);
    }

    public static long EstimateFullSizeBytes(
        long sampleBytes,
        double sampleDurationSeconds,
        double fullEffectiveDurationSeconds)
    {
        if (sampleBytes <= 0 || sampleDurationSeconds <= 0.1 || fullEffectiveDurationSeconds <= 0.1)
        {
            return 0;
        }

        var ratio = fullEffectiveDurationSeconds / sampleDurationSeconds;
        return (long)Math.Round(sampleBytes * ratio);
    }

    public static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;
        while (value >= 1024.0 && unitIndex < units.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{bytes} B" : $"{value:0.##} {units[unitIndex]}";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return "00:00";
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    public static string FormatDurationPrecise(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";
    }

    public static string FormatEta(TimeSpan eta)
    {
        if (eta < TimeSpan.Zero) eta = TimeSpan.Zero;
        if (eta.TotalHours >= 1)
        {
            return $"{(int)eta.TotalHours}h{eta.Minutes:00}m";
        }
        if (eta.TotalMinutes >= 1)
        {
            return $"{eta.Minutes}m{eta.Seconds:00}s";
        }
        return $"{eta.Seconds}s";
    }

    public static bool TryParseTimestamp(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rawSec) && rawSec >= 0)
        {
            seconds = rawSec;
            return true;
        }

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) && ts >= TimeSpan.Zero)
        {
            seconds = ts.TotalSeconds;
            return true;
        }

        return false;
    }

    public static string FormatCompressionPercent(long sourceBytes, long outputBytes)
    {
        if (sourceBytes <= 0 || outputBytes <= 0) return string.Empty;
        var diffPercent = (outputBytes - sourceBytes) * 100.0 / sourceBytes;
        return diffPercent <= 0
            ? $"{diffPercent:0.0}%"
            : $"+{diffPercent:0.0}%";
    }
}
