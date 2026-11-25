using System.Text;
using System.Text.Json;

namespace ShanaPress.Core.Models;

public sealed class EncodingProfile
{
    // Container & General
    public string ContainerFormat { get; set; } = "mp4";
    public bool FastStart { get; set; } = true;
    public bool CopyMetadata { get; set; } = true;
    public string HardwareDecode { get; set; } = "auto"; // off, auto, cuda, d3d11va

    // Video Encoding
    public string VideoCodec { get; set; } = "libx264";
    public RateControlMode RateControlMode { get; set; } = RateControlMode.CrfOrCq;
    public int QualityValue { get; set; } = 23;
    public int VideoBitrateKbps { get; set; } = 2500;
    public double TargetFileSizeMb { get; set; } = 25.0;
    public bool TargetSizeUseTwoPass { get; set; } = true;
    public string Preset { get; set; } = "medium";
    public string Profile { get; set; } = "auto";
    public string PixelFormat { get; set; } = "auto";
    public bool NvencSpatialAq { get; set; } = true;
    public bool NvencTemporalAq { get; set; } = false;

    // Video Geometry & Timing
    public ResizeMode ResizeMode { get; set; } = ResizeMode.Original;
    public int ResizeHeight { get; set; } = 1080;
    public int CustomWidth { get; set; } = 1920;
    public int CustomHeight { get; set; } = 1080;
    public bool KeepAspectRatio { get; set; } = true;
    public FrameRateMode FrameRateMode { get; set; } = FrameRateMode.Original;
    public double TargetFps { get; set; } = 30.0;

    // Audio
    public string AudioCodec { get; set; } = "aac";
    public int AudioBitrateKbps { get; set; } = 128;
    public int SampleRate { get; set; } = 0; // 0 = Original
    public int AudioChannels { get; set; } = 0; // 0 = Original
    public double VolumeDb { get; set; } = 0.0;
    public bool EnableLoudnorm { get; set; } = false;

    // Video Filters
    public bool TrimEnabled { get; set; } = false;
    public double TrimStartSeconds { get; set; } = 0.0;
    public double TrimEndSeconds { get; set; } = 0.0;

    public bool CropEnabled { get; set; } = false;
    public int CropLeft { get; set; } = 0;
    public int CropTop { get; set; } = 0;
    public int CropRight { get; set; } = 0;
    public int CropBottom { get; set; } = 0;

    public RotationMode Rotation { get; set; } = RotationMode.None;
    public bool FlipHorizontal { get; set; } = false;
    public bool FlipVertical { get; set; } = false;
    public DeinterlaceMode DeinterlaceMode { get; set; } = DeinterlaceMode.None;
    public bool HdrToSdr { get; set; } = false;
    public string HdrTonemapAlgorithm { get; set; } = "hable";
    public FilterStrength DenoiseStrength { get; set; } = FilterStrength.None;
    public FilterStrength SharpenStrength { get; set; } = FilterStrength.None;

    // Subtitles & Watermark
    public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.KeepSoft;
    public string? SubtitleFilePath { get; set; }
    public string SubtitleFontName { get; set; } = "Microsoft YaHei";
    public int SubtitleFontSize { get; set; } = 22;
    public double SubtitleOutline { get; set; } = 1.5;

    public bool ImageWatermarkEnabled { get; set; } = false;
    public string? ImageWatermarkPath { get; set; }
    public WatermarkPosition WatermarkPosition { get; set; } = WatermarkPosition.BottomRight;
    public int WatermarkMargin { get; set; } = 20;
    public double WatermarkOpacity { get; set; } = 0.75;
    public int WatermarkScalePercent { get; set; } = 15;

    public bool TextWatermarkEnabled { get; set; } = false;
    public string TextWatermarkContent { get; set; } = string.Empty;
    public int TextWatermarkFontSize { get; set; } = 28;
    public string TextWatermarkColor { get; set; } = "white";
    public double TextWatermarkOpacity { get; set; } = 0.8;
    public WatermarkPosition TextWatermarkPosition { get; set; } = WatermarkPosition.BottomRight;

    // Output & Advanced
    public OutputDirectoryMode OutputDirectoryMode { get; set; } = OutputDirectoryMode.SameAsSource;
    public string CustomOutputDirectory { get; set; } = string.Empty;
    public string FileNameTemplate { get; set; } = "{name}_compressed";
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.AutoRename;
    public string CustomExtraArgs { get; set; } = string.Empty;

    public bool IsAudioOnlyContainer =>
        ContainerFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase) ||
        ContainerFormat.Equals("flac", StringComparison.OrdinalIgnoreCase);

    public bool IsGifContainer =>
        ContainerFormat.Equals("gif", StringComparison.OrdinalIgnoreCase);

    public bool IsVideoDisabled =>
        IsAudioOnlyContainer || VideoCodec.Equals("none", StringComparison.OrdinalIgnoreCase);

    public bool IsVideoCopy =>
        !IsAudioOnlyContainer && !IsGifContainer && VideoCodec.Equals("copy", StringComparison.OrdinalIgnoreCase);

    public EncodingProfile Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<EncodingProfile>(json) ?? new EncodingProfile();
    }

    public string BuildSummary()
    {
        var parts = new List<string> { ContainerFormat.ToUpperInvariant() };

        if (IsAudioOnlyContainer)
        {
            parts.Add("仅音频");
        }
        else if (IsGifContainer)
        {
            parts.Add("GIF 动图");
        }
        else if (IsVideoCopy)
        {
            parts.Add("视频流直拷");
        }
        else if (VideoCodec.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add("无视频");
        }
        else
        {
            var codecLabel = VideoCodec switch
            {
                "h264_nvenc" => "H.264 NVENC",
                "hevc_nvenc" => "HEVC NVENC",
                "av1_nvenc" => "AV1 NVENC",
                "h264_amf" => "H.264 AMF",
                "hevc_amf" => "HEVC AMF",
                "av1_amf" => "AV1 AMF",
                "h264_qsv" => "H.264 QSV",
                "hevc_qsv" => "HEVC QSV",
                "av1_qsv" => "AV1 QSV",
                "libx264" => "x264",
                "libx265" => "x265",
                "libaom-av1" => "libaom AV1",
                "libvpx-vp9" => "VP9",
                _ => VideoCodec
            };

            var rcLabel = RateControlMode switch
            {
                RateControlMode.CrfOrCq => VideoCodec.Contains("nvenc") ? $"CQ{QualityValue}" : $"CRF{QualityValue}",
                RateControlMode.AverageBitrate => $"{VideoBitrateKbps}k",
                RateControlMode.TwoPassBitrate => $"{VideoBitrateKbps}k 2-Pass",
                RateControlMode.TargetFileSize => $"目标 {TargetFileSizeMb:0.#}MB",
                _ => string.Empty
            };

            parts.Add($"{codecLabel} {rcLabel}".Trim());
        }

        if (!IsVideoDisabled && !IsVideoCopy)
        {
            var resLabel = ResizeMode switch
            {
                ResizeMode.PresetHeight => $"{ResizeHeight}p",
                ResizeMode.Custom => $"{CustomWidth}x{CustomHeight}",
                _ => "原分辨率"
            };
            parts.Add(resLabel);

            if (FrameRateMode == FrameRateMode.Custom)
            {
                parts.Add($"{TargetFps:0.##}fps");
            }
        }

        if (!IsGifContainer)
        {
            var audioLabel = AudioCodec switch
            {
                "none" => "无音频",
                "copy" => "音频直拷",
                "flac" => "FLAC",
                _ => $"{AudioCodec.Replace("lib", "").ToUpperInvariant()} {AudioBitrateKbps}k"
            };
            parts.Add(audioLabel);
        }

        var flags = new List<string>();
        if (HdrToSdr) flags.Add("HDR→SDR");
        if (SubtitleMode == SubtitleMode.BurnIn && !string.IsNullOrWhiteSpace(SubtitleFilePath)) flags.Add("硬字幕");
        if (ImageWatermarkEnabled || (TextWatermarkEnabled && !string.IsNullOrWhiteSpace(TextWatermarkContent))) flags.Add("水印");
        if (FastStart && (ContainerFormat == "mp4" || ContainerFormat == "mov")) flags.Add("FastStart");
        if (flags.Count > 0)
        {
            parts.Add(string.Join("+", flags));
        }

        return string.Join(" · ", parts);
    }
}
