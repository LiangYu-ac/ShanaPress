namespace ShanaPress.Core.Models;

public sealed class EncodingProgress
{
    public double Percent { get; set; }
    public int CurrentPass { get; set; } = 1;
    public int TotalPasses { get; set; } = 1;
    public TimeSpan OutTime { get; set; }
    public double Fps { get; set; }
    public double Speed { get; set; }
    public string Bitrate { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public TimeSpan? Eta { get; set; }
    public bool IsEnd { get; set; }
}

public sealed class SampleTestResult
{
    public string SourceFileName { get; set; } = string.Empty;
    public string SampleOutputPath { get; set; } = string.Empty;
    public long SourceSizeBytes { get; set; }
    public long SampleSizeBytes { get; set; }
    public double SampleDurationSeconds { get; set; }
    public double FullEffectiveDurationSeconds { get; set; }
    public long EstimatedFullSizeBytes { get; set; }
    public TimeSpan ElapsedEncodeTime { get; set; }
    public string ProfileSummary { get; set; } = string.Empty;
}

public sealed class HardwareCapabilities
{
    public string FfmpegPath { get; set; } = string.Empty;
    public string FfprobePath { get; set; } = string.Empty;
    public string FfmpegVersion { get; set; } = string.Empty;
    public bool HasFfmpeg => !string.IsNullOrWhiteSpace(FfmpegPath) && File.Exists(FfmpegPath);
    public bool HasFfprobe => !string.IsNullOrWhiteSpace(FfprobePath) && File.Exists(FfprobePath);

    public List<string> GpuNames { get; set; } = new();
    public HashSet<string> WorkingEncoders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AvailableEncoders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasNvencH264 => WorkingEncoders.Contains("h264_nvenc");
    public bool HasNvencHevc => WorkingEncoders.Contains("hevc_nvenc");
    public bool HasNvencAv1 => WorkingEncoders.Contains("av1_nvenc");
    public bool HasAnyNvenc => HasNvencH264 || HasNvencHevc || HasNvencAv1;

    public bool HasAmfH264 => WorkingEncoders.Contains("h264_amf");
    public bool HasAmfHevc => WorkingEncoders.Contains("hevc_amf");
    public bool HasAmfAv1 => WorkingEncoders.Contains("av1_amf");
    public bool HasAnyAmf => HasAmfH264 || HasAmfHevc || HasAmfAv1;

    public bool HasQsvH264 => WorkingEncoders.Contains("h264_qsv");
    public bool HasQsvHevc => WorkingEncoders.Contains("hevc_qsv");
    public bool HasQsvAv1 => WorkingEncoders.Contains("av1_qsv");
    public bool HasAnyQsv => HasQsvH264 || HasQsvHevc || HasQsvAv1;

    public string BuildBadgeText()
    {
        if (!HasFfmpeg)
        {
            return "未检测到 FFmpeg — 请在快速设置中指定路径";
        }

        var parts = new List<string>();
        if (HasAnyNvenc)
        {
            var codecs = new List<string>();
            if (HasNvencH264) codecs.Add("H.264");
            if (HasNvencHevc) codecs.Add("HEVC");
            if (HasNvencAv1) codecs.Add("AV1");
            var nvGpu = GpuNames.FirstOrDefault(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ?? "NVIDIA GPU";
            parts.Add(HasAnyAmf || HasAnyQsv
                ? $"{nvGpu} (NVENC)"
                : $"{nvGpu} (NVENC {string.Join("/", codecs)})");
        }
        if (HasAnyAmf)
        {
            var codecs = new List<string>();
            if (HasAmfH264) codecs.Add("H.264");
            if (HasAmfHevc) codecs.Add("HEVC");
            if (HasAmfAv1) codecs.Add("AV1");
            parts.Add(HasAnyNvenc ? "AMD AMF" : $"AMD AMF ({string.Join("/", codecs)})");
        }
        if (HasAnyQsv)
        {
            parts.Add("Intel QSV");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "CPU 软件编码模式 (x264 / x265)";
    }
}

public sealed class PresetItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "自定义";
    public string Description { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public EncodingProfile Profile { get; set; } = new();
}

public sealed class AppSettings
{
    public bool IsDarkTheme { get; set; } = true;
    public string CustomFfmpegPath { get; set; } = string.Empty;
    public string CustomFfprobePath { get; set; } = string.Empty;
    public string SelectedPresetId { get; set; } = "builtin-nvenc-hevc";
    public int MaxConcurrency { get; set; } = 1;
    public PostEncodeAction PostEncodeAction { get; set; } = PostEncodeAction.None;
    public bool IsBottomPanelExpanded { get; set; } = false;
    public EncodingProfile? LastProfile { get; set; }
}
