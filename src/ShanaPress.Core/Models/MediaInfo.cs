using System.Text;

namespace ShanaPress.Core.Models;

public sealed class MediaInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FormatName { get; set; } = string.Empty;
    public string FormatLongName { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public long FileSizeBytes { get; set; }
    public long OverallBitrateKbps { get; set; }

    public VideoStreamInfo? VideoStream { get; set; }
    public List<AudioStreamInfo> AudioStreams { get; set; } = new();
    public List<SubtitleStreamInfo> SubtitleStreams { get; set; } = new();

    public bool HasVideo => VideoStream is not null;
    public bool HasAudio => AudioStreams.Count > 0;
    public bool HasSubtitles => SubtitleStreams.Count > 0;
    public bool IsHdr => VideoStream?.IsHdr ?? false;

    public string BuildVideoSummary()
    {
        if (VideoStream is null)
        {
            return AudioStreams.Count > 0 ? $"纯音频 ({AudioStreams[0].CodecName})" : "无视频流";
        }

        var fps = VideoStream.FrameRate > 0 ? $" {VideoStream.FrameRate:0.##}fps" : string.Empty;
        var hdr = VideoStream.IsHdr ? " [HDR]" : string.Empty;
        return $"{VideoStream.Width}×{VideoStream.Height}{fps} · {VideoStream.CodecName.ToUpperInvariant()}{hdr}";
    }

    public string BuildDetailedReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== 容器 / 常规信息 ===");
        sb.AppendLine($"文件路径: {FilePath}");
        sb.AppendLine($"封装格式: {FormatLongName} ({FormatName})");
        sb.AppendLine($"文件大小: {Services.BitrateCalculator.FormatFileSize(FileSizeBytes)} ({FileSizeBytes:N0} 字节)");
        sb.AppendLine($"总时长: {Services.BitrateCalculator.FormatDuration(Duration)}");
        sb.AppendLine($"总码率: {OverallBitrateKbps} kbps");

        if (VideoStream is not null)
        {
            sb.AppendLine();
            sb.AppendLine("=== 视频流 ===");
            sb.AppendLine($"编码格式: {VideoStream.CodecName} ({VideoStream.CodecLongName})");
            sb.AppendLine($"Profile: {VideoStream.Profile}");
            sb.AppendLine($"分辨率: {VideoStream.Width} × {VideoStream.Height}");
            sb.AppendLine($"帧率: {VideoStream.FrameRate:0.###} fps ({VideoStream.RawFrameRate})");
            sb.AppendLine($"像素格式: {VideoStream.PixelFormat}");
            sb.AppendLine($"视频码率: {(VideoStream.BitrateKbps > 0 ? $"{VideoStream.BitrateKbps} kbps" : "未知/可变")}");
            sb.AppendLine($"色彩空间: primaries={VideoStream.ColorPrimaries}, transfer={VideoStream.ColorTransfer}, space={VideoStream.ColorSpace}");
            sb.AppendLine($"动态范围: {(VideoStream.IsHdr ? "HDR (高动态范围)" : "SDR (标准动态范围)")}");
        }

        if (AudioStreams.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"=== 音频流 ({AudioStreams.Count}) ===");
            for (var i = 0; i < AudioStreams.Count; i++)
            {
                var a = AudioStreams[i];
                sb.AppendLine($"#{i + 1} [流 {a.Index}] {a.CodecName} · {a.SampleRate} Hz · {a.Channels} 声道 ({a.ChannelLayout}) · {(a.BitrateKbps > 0 ? $"{a.BitrateKbps} kbps" : "未知")} · 语言: {a.Language}");
            }
        }

        if (SubtitleStreams.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"=== 内嵌字幕流 ({SubtitleStreams.Count}) ===");
            for (var i = 0; i < SubtitleStreams.Count; i++)
            {
                var s = SubtitleStreams[i];
                sb.AppendLine($"#{i + 1} [流 {s.Index}] {s.CodecName} · 语言: {s.Language} · 标题: {s.Title}");
            }
        }

        return sb.ToString();
    }
}

public sealed class VideoStreamInfo
{
    public int Index { get; set; }
    public string CodecName { get; set; } = string.Empty;
    public string CodecLongName { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public double FrameRate { get; set; }
    public string RawFrameRate { get; set; } = string.Empty;
    public string PixelFormat { get; set; } = string.Empty;
    public long BitrateKbps { get; set; }
    public string ColorTransfer { get; set; } = string.Empty;
    public string ColorPrimaries { get; set; } = string.Empty;
    public string ColorSpace { get; set; } = string.Empty;

    public bool IsHdr =>
        ColorTransfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) ||
        ColorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);

    public bool IsHlg =>
        ColorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
}

public sealed class AudioStreamInfo
{
    public int Index { get; set; }
    public string CodecName { get; set; } = string.Empty;
    public int Channels { get; set; }
    public string ChannelLayout { get; set; } = string.Empty;
    public int SampleRate { get; set; }
    public long BitrateKbps { get; set; }
    public string Language { get; set; } = "und";
}

public sealed class SubtitleStreamInfo
{
    public int Index { get; set; }
    public string CodecName { get; set; } = string.Empty;
    public string Language { get; set; } = "und";
    public string Title { get; set; } = string.Empty;
}
