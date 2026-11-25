using System.Globalization;
using System.Text;
using ShanaPress.Core.Models;

namespace ShanaPress.Core.Services;

public sealed class FfmpegJobPlan
{
    public string SourcePath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public bool ShouldSkip { get; init; }
    public bool IsTwoPass { get; init; }
    public double EffectiveDurationSeconds { get; init; }
    public int ComputedVideoBitrateKbps { get; init; }
    public string? TempPassLogPrefix { get; init; }
    public List<string> Pass1Arguments { get; init; } = new();
    public List<string> Pass2Arguments { get; init; } = new();
    public List<string> DisplayArguments { get; init; } = new();

    public string ToDisplayCommandLine(string ffmpegExe = "ffmpeg")
    {
        var exe = Path.GetFileName(ffmpegExe);
        if (IsTwoPass)
        {
            return $"# Pass 1\n{exe} {FormatArgs(Pass1Arguments, stripProgress: true)}\n\n# Pass 2\n{exe} {FormatArgs(DisplayArguments, stripProgress: false)}";
        }
        return $"{exe} {FormatArgs(DisplayArguments, stripProgress: false)}";
    }

    private static string FormatArgs(IEnumerable<string> args, bool stripProgress)
    {
        var list = args.ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (stripProgress && (a == "-progress" || a == "-stats_period"))
            {
                i++;
                continue;
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(QuoteIfNeeded(a));
        }
        return sb.ToString();
    }

    private static string QuoteIfNeeded(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.Any(ch => char.IsWhiteSpace(ch) || ch == '"' || ch == '\'' || ch == '&' || ch == '|'))
        {
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        }
        return arg;
    }
}

public static class FfmpegCommandBuilder
{
    public static FfmpegJobPlan BuildJobPlan(
        EncodingProfile profile,
        QueueItem item,
        bool isSampleMode = false)
    {
        var mediaInfo = item.MediaInfo;
        var totalDurationSec = mediaInfo?.Duration.TotalSeconds ?? 0;

        // Resolve per-file Trim
        var trimEnabled = item.PerFileTrimEnabled || profile.TrimEnabled;
        var trimStart = item.PerFileTrimEnabled ? item.TrimStartSeconds : profile.TrimStartSeconds;
        var trimEnd = item.PerFileTrimEnabled ? item.TrimEndSeconds : profile.TrimEndSeconds;

        // Resolve per-file Crop
        var cropEnabled = item.PerFileCropEnabled || profile.CropEnabled;
        var cropL = item.PerFileCropEnabled ? item.CropLeft : profile.CropLeft;
        var cropT = item.PerFileCropEnabled ? item.CropTop : profile.CropTop;
        var cropR = item.PerFileCropEnabled ? item.CropRight : profile.CropRight;
        var cropB = item.PerFileCropEnabled ? item.CropBottom : profile.CropBottom;

        // Resolve per-file Subtitle
        var subMode = item.PerFileSubtitleMode ?? profile.SubtitleMode;
        var subPath = !string.IsNullOrWhiteSpace(item.PerFileSubtitlePath)
            ? item.PerFileSubtitlePath
            : profile.SubtitleFilePath;

        // Compute effective duration
        var startSec = trimEnabled ? Math.Max(0, trimStart) : 0;
        var endSec = totalDurationSec;
        if (trimEnabled && trimEnd > startSec)
        {
            endSec = totalDurationSec > 0 ? Math.Min(trimEnd, totalDurationSec) : trimEnd;
        }
        var fullEffectiveDuration = endSec > startSec
            ? Math.Max(0.1, endSec - startSec)
            : Math.Max(0.1, totalDurationSec > 0 ? totalDurationSec : 10.0);

        double? sampleDurationSec = null;
        if (isSampleMode)
        {
            sampleDurationSec = Math.Min(10.0, fullEffectiveDuration);
            // Seek 25% into the effective segment for representative sample
            startSec += Math.Max(0, (fullEffectiveDuration - sampleDurationSec.Value) * 0.25);
        }

        var effectiveDuration = sampleDurationSec ?? fullEffectiveDuration;

        // Resolve Output Path
        var (outputPath, shouldSkip) = isSampleMode
            ? (BuildSampleOutputPath(item.SourcePath, profile.ContainerFormat), false)
            : ResolveOutputPath(item.SourcePath, profile);

        var hasAudioOutput = !profile.IsGifContainer &&
                             !profile.AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                             (mediaInfo is null || mediaInfo.HasAudio);

        var computedBitrateKbps = profile.RateControlMode == RateControlMode.TargetFileSize
            ? BitrateCalculator.CalculateTargetVideoBitrateKbps(
                profile.TargetFileSizeMb,
                fullEffectiveDuration,
                profile.AudioBitrateKbps,
                hasAudioOutput)
            : profile.VideoBitrateKbps;

        var isTrueTwoPass = !isSampleMode &&
                            !profile.IsVideoDisabled &&
                            !profile.IsVideoCopy &&
                            !profile.IsGifContainer &&
                            (profile.RateControlMode == RateControlMode.TwoPassBitrate ||
                             (profile.RateControlMode == RateControlMode.TargetFileSize && profile.TargetSizeUseTwoPass)) &&
                            IsTwoPassCapableSoftwareEncoder(profile.VideoCodec);

        var filterGraph = FfmpegFilterBuilder.BuildVideoFilterGraph(
            profile, mediaInfo, cropEnabled, cropL, cropTop: cropT, cropR, cropB, subMode, subPath);

        var hasHardsubBurnIn = !profile.IsVideoDisabled &&
                               !profile.IsVideoCopy &&
                               subMode == SubtitleMode.BurnIn &&
                               !string.IsNullOrWhiteSpace(subPath) &&
                               File.Exists(subPath);

        var tempPassPrefix = isTrueTwoPass
            ? Path.Combine(Path.GetTempPath(), $"shanapress_pass_{Guid.NewGuid():N}")
            : null;

        if (isTrueTwoPass)
        {
            var nullTarget = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
            var pass1Args = BuildPassArguments(
                profile, item.SourcePath, nullTarget, mediaInfo,
                trimEnabled || isSampleMode, startSec, sampleDurationSec ?? (trimEnabled && trimEnd > startSec ? effectiveDuration : null),
                hasHardsubBurnIn, filterGraph, subMode, computedBitrateKbps,
                passNumber: 1, passLogPrefix: tempPassPrefix, includeProgress: true, overwriteOutput: true);

            var pass2Args = BuildPassArguments(
                profile, item.SourcePath, outputPath, mediaInfo,
                trimEnabled || isSampleMode, startSec, sampleDurationSec ?? (trimEnabled && trimEnd > startSec ? effectiveDuration : null),
                hasHardsubBurnIn, filterGraph, subMode, computedBitrateKbps,
                passNumber: 2, passLogPrefix: tempPassPrefix, includeProgress: true, overwriteOutput: isSampleMode || profile.ConflictPolicy == ConflictPolicy.Overwrite);

            var displayArgs = BuildPassArguments(
                profile, item.SourcePath, outputPath, mediaInfo,
                trimEnabled || isSampleMode, startSec, sampleDurationSec ?? (trimEnabled && trimEnd > startSec ? effectiveDuration : null),
                hasHardsubBurnIn, filterGraph, subMode, computedBitrateKbps,
                passNumber: 2, passLogPrefix: tempPassPrefix, includeProgress: false, overwriteOutput: isSampleMode || profile.ConflictPolicy == ConflictPolicy.Overwrite);

            return new FfmpegJobPlan
            {
                SourcePath = item.SourcePath,
                OutputPath = outputPath,
                ShouldSkip = shouldSkip,
                IsTwoPass = true,
                EffectiveDurationSeconds = effectiveDuration,
                ComputedVideoBitrateKbps = computedBitrateKbps,
                TempPassLogPrefix = tempPassPrefix,
                Pass1Arguments = pass1Args,
                Pass2Arguments = pass2Args,
                DisplayArguments = displayArgs
            };
        }

        var singleArgs = BuildPassArguments(
            profile, item.SourcePath, outputPath, mediaInfo,
            trimEnabled || isSampleMode, startSec, sampleDurationSec ?? (trimEnabled && trimEnd > startSec ? effectiveDuration : null),
            hasHardsubBurnIn, filterGraph, subMode, computedBitrateKbps,
            passNumber: 0, passLogPrefix: null, includeProgress: true, overwriteOutput: isSampleMode || profile.ConflictPolicy == ConflictPolicy.Overwrite);

        var singleDisplayArgs = BuildPassArguments(
            profile, item.SourcePath, outputPath, mediaInfo,
            trimEnabled || isSampleMode, startSec, sampleDurationSec ?? (trimEnabled && trimEnd > startSec ? effectiveDuration : null),
            hasHardsubBurnIn, filterGraph, subMode, computedBitrateKbps,
            passNumber: 0, passLogPrefix: null, includeProgress: false, overwriteOutput: isSampleMode || profile.ConflictPolicy == ConflictPolicy.Overwrite);

        return new FfmpegJobPlan
        {
            SourcePath = item.SourcePath,
            OutputPath = outputPath,
            ShouldSkip = shouldSkip,
            IsTwoPass = false,
            EffectiveDurationSeconds = effectiveDuration,
            ComputedVideoBitrateKbps = computedBitrateKbps,
            Pass2Arguments = singleArgs,
            DisplayArguments = singleDisplayArgs
        };
    }

    private static List<string> BuildPassArguments(
        EncodingProfile profile,
        string sourcePath,
        string outputPath,
        MediaInfo? mediaInfo,
        bool hasTrimOrSample,
        double startSec,
        double? durationSec,
        bool hasHardsubBurnIn,
        FilterGraphBuildResult filterGraph,
        SubtitleMode effectiveSubMode,
        int computedBitrateKbps,
        int passNumber,
        string? passLogPrefix,
        bool includeProgress,
        bool overwriteOutput)
    {
        var args = new List<string> { "-hide_banner" };
        if (overwriteOutput)
        {
            args.Add("-y");
        }

        // Hardware decode / VAAPI device initialization
        if (!profile.IsVideoDisabled && !profile.IsVideoCopy && profile.VideoCodec.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-vaapi_device");
            args.Add("/dev/dri/renderD128");
        }

        if (!profile.IsVideoDisabled && !profile.IsVideoCopy && !string.Equals(profile.HardwareDecode, "off", StringComparison.OrdinalIgnoreCase))
        {
            if (profile.HardwareDecode is "auto" or "cuda" or "d3d11va" or "videotoolbox" or "vaapi")
            {
                args.Add("-hwaccel");
                args.Add(profile.HardwareDecode);
            }
        }

        // Fast seek before -i unless hardsub burn-in requires aligned timestamps
        if (hasTrimOrSample && startSec > 0.001 && !hasHardsubBurnIn)
        {
            args.Add("-ss");
            args.Add(startSec.ToString("0.###", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(sourcePath);

        if (!string.IsNullOrWhiteSpace(filterGraph.ExtraInputPath))
        {
            args.Add("-i");
            args.Add(filterGraph.ExtraInputPath);
        }

        // Accurate seek after -i when hardsub is burned in
        if (hasTrimOrSample && startSec > 0.001 && hasHardsubBurnIn)
        {
            args.Add("-ss");
            args.Add(startSec.ToString("0.###", CultureInfo.InvariantCulture));
        }

        if (hasTrimOrSample && durationSec.HasValue && durationSec.Value > 0.05)
        {
            args.Add("-t");
            args.Add(durationSec.Value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        var canKeepSoftSubtitles = effectiveSubMode == SubtitleMode.KeepSoft &&
                                   SupportsSoftSubtitlesForMedia(profile.ContainerFormat, mediaInfo);

        // Stream mapping & filtergraph
        if (profile.IsVideoDisabled)
        {
            args.Add("-vn");
        }
        else if (filterGraph.UseFilterComplex && !string.IsNullOrEmpty(filterGraph.FilterComplex))
        {
            args.Add("-filter_complex");
            args.Add(filterGraph.FilterComplex);
            args.Add("-map");
            args.Add(filterGraph.VideoMapLabel ?? "[vout]");
            if (passNumber != 1 && !profile.IsGifContainer && !profile.AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-map");
                args.Add("0:a?");
            }
            if (passNumber != 1 && canKeepSoftSubtitles && !profile.IsGifContainer)
            {
                args.Add("-map");
                args.Add("0:s?");
            }
        }
        else
        {
            if (!string.IsNullOrEmpty(filterGraph.VideoFilter))
            {
                args.Add("-vf");
                args.Add(filterGraph.VideoFilter);
            }

            // Explicit mapping when keeping soft subtitles so all streams survive
            if (passNumber != 1 && canKeepSoftSubtitles && !profile.IsGifContainer)
            {
                args.Add("-map");
                args.Add("0:v?");
                if (!profile.AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    args.Add("-map");
                    args.Add("0:a?");
                }
                args.Add("-map");
                args.Add("0:s?");
            }
        }

        // Video Encoder & Rate Control
        if (!profile.IsVideoDisabled)
        {
            if (profile.IsGifContainer)
            {
                args.Add("-loop");
                args.Add("0");
            }
            else if (profile.IsVideoCopy)
            {
                args.Add("-c:v");
                args.Add("copy");
            }
            else
            {
                AppendVideoEncoderArgs(args, profile, computedBitrateKbps, passNumber, passLogPrefix);
            }
        }

        // Audio
        var effectiveAudioCodec = profile.AudioCodec;
        if (profile.ContainerFormat.Equals("mp3", StringComparison.OrdinalIgnoreCase) &&
            !effectiveAudioCodec.Equals("copy", StringComparison.OrdinalIgnoreCase) &&
            !effectiveAudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            effectiveAudioCodec = "libmp3lame";
        }
        else if (profile.ContainerFormat.Equals("flac", StringComparison.OrdinalIgnoreCase) &&
                 !effectiveAudioCodec.Equals("copy", StringComparison.OrdinalIgnoreCase) &&
                 !effectiveAudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            effectiveAudioCodec = "flac";
        }

        if (passNumber == 1 || profile.IsGifContainer || effectiveAudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-an");
        }
        else if (effectiveAudioCodec.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            args.Add("-c:a");
            args.Add("copy");
        }
        else
        {
            args.Add("-c:a");
            args.Add(effectiveAudioCodec);

            if (!effectiveAudioCodec.Equals("flac", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-b:a");
                args.Add($"{Math.Clamp(profile.AudioBitrateKbps, 32, 512)}k");
            }

            if (profile.SampleRate > 0)
            {
                args.Add("-ar");
                args.Add(profile.SampleRate.ToString(CultureInfo.InvariantCulture));
            }
            if (profile.AudioChannels > 0)
            {
                args.Add("-ac");
                args.Add(profile.AudioChannels.ToString(CultureInfo.InvariantCulture));
            }

            var af = FfmpegFilterBuilder.BuildAudioFilter(profile);
            if (!string.IsNullOrEmpty(af))
            {
                args.Add("-af");
                args.Add(af);
            }
        }

        // Subtitles
        if (passNumber == 1 || profile.IsAudioOnlyContainer || profile.IsGifContainer)
        {
            args.Add("-sn");
        }
        else if (canKeepSoftSubtitles)
        {
            args.Add("-c:s");
            args.Add(profile.ContainerFormat is "mp4" or "mov" ? "mov_text" : "copy");
        }
        else
        {
            args.Add("-sn");
        }

        // Container / Metadata flags
        if (passNumber != 1)
        {
            if (!profile.CopyMetadata)
            {
                args.Add("-map_metadata");
                args.Add("-1");
            }
            if (profile.FastStart && (profile.ContainerFormat is "mp4" or "mov"))
            {
                args.Add("-movflags");
                args.Add("+faststart");
            }
        }

        // Custom extra args
        if (!string.IsNullOrWhiteSpace(profile.CustomExtraArgs))
        {
            args.AddRange(TokenizeArguments(profile.CustomExtraArgs));
        }

        // Progress reporting
        if (includeProgress)
        {
            args.Add("-progress");
            args.Add("pipe:1");
            args.Add("-stats_period");
            args.Add("0.25");
        }

        if (passNumber == 1)
        {
            args.Add("-f");
            args.Add("null");
        }

        args.Add(outputPath);
        return args;
    }

    private static void AppendVideoEncoderArgs(
        List<string> args,
        EncodingProfile profile,
        int computedBitrateKbps,
        int passNumber,
        string? passLogPrefix)
    {
        var codec = profile.VideoCodec;
        args.Add("-c:v");
        args.Add(codec);

        var isNvenc = codec.Contains("nvenc", StringComparison.OrdinalIgnoreCase);
        var isAmf = codec.Contains("amf", StringComparison.OrdinalIgnoreCase);
        var isQsv = codec.Contains("qsv", StringComparison.OrdinalIgnoreCase);
        var isVideoToolbox = codec.Contains("videotoolbox", StringComparison.OrdinalIgnoreCase);
        var isVaapi = codec.Contains("vaapi", StringComparison.OrdinalIgnoreCase);
        var isAv1OrVp9 = codec.Contains("av1", StringComparison.OrdinalIgnoreCase) || codec.Contains("vp9", StringComparison.OrdinalIgnoreCase);

        if (isNvenc)
        {
            args.Add("-preset");
            args.Add(NormalizeNvencPreset(profile.Preset));

            var isMultipassRequested = profile.RateControlMode == RateControlMode.TwoPassBitrate ||
                                       (profile.RateControlMode == RateControlMode.TargetFileSize && profile.TargetSizeUseTwoPass);

            if (profile.RateControlMode == RateControlMode.CrfOrCq)
            {
                var maxQ = codec.Equals("av1_nvenc", StringComparison.OrdinalIgnoreCase) ? 63 : 51;
                var cq = Math.Clamp(profile.QualityValue, 1, maxQ);
                args.Add("-rc");
                args.Add("vbr");
                args.Add("-cq");
                args.Add(cq.ToString(CultureInfo.InvariantCulture));
                args.Add("-b:v");
                args.Add("0");
            }
            else
            {
                args.Add("-rc");
                args.Add("vbr");
                args.Add("-b:v");
                args.Add($"{computedBitrateKbps}k");
                args.Add("-maxrate");
                args.Add($"{(int)Math.Round(computedBitrateKbps * 1.5)}k");
                args.Add("-bufsize");
                args.Add($"{computedBitrateKbps * 2}k");

                if (isMultipassRequested)
                {
                    args.Add("-multipass");
                    args.Add("fullres");
                }
            }

            if (profile.NvencSpatialAq)
            {
                args.Add("-spatial-aq");
                args.Add("1");
            }
            if (profile.NvencTemporalAq)
            {
                args.Add("-temporal-aq");
                args.Add("1");
            }
        }
        else if (isAmf)
        {
            args.Add("-quality");
            args.Add(NormalizeAmfQuality(profile.Preset));

            if (profile.RateControlMode == RateControlMode.CrfOrCq)
            {
                var qp = Math.Clamp(profile.QualityValue, 1, 51);
                args.Add("-rc");
                args.Add("cqp");
                args.Add("-qp_i");
                args.Add(qp.ToString(CultureInfo.InvariantCulture));
                args.Add("-qp_p");
                args.Add(qp.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                args.Add("-rc");
                args.Add("vbr_latency");
                args.Add("-b:v");
                args.Add($"{computedBitrateKbps}k");
            }
        }
        else if (isQsv)
        {
            args.Add("-preset");
            args.Add(NormalizeQsvPreset(profile.Preset));

            if (profile.RateControlMode == RateControlMode.CrfOrCq)
            {
                args.Add("-global_quality");
                args.Add(Math.Clamp(profile.QualityValue, 1, 51).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                args.Add("-b:v");
                args.Add($"{computedBitrateKbps}k");
            }
        }
        else if (isVideoToolbox)
        {
            if (profile.RateControlMode == RateControlMode.CrfOrCq)
            {
                // VideoToolbox quality scale is 1..100 (higher = better quality)
                var vtQ = Math.Clamp(100 - (int)Math.Round(profile.QualityValue * 1.65), 15, 95);
                args.Add("-q:v");
                args.Add(vtQ.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                args.Add("-b:v");
                args.Add($"{computedBitrateKbps}k");
            }

            args.Add("-allow_sw");
            args.Add("1");

            if (codec.Equals("hevc_videotoolbox", StringComparison.OrdinalIgnoreCase))
            {
                args.Add("-tag:v");
                args.Add("hvc1");
            }
        }
        else if (isVaapi)
        {
            if (profile.RateControlMode == RateControlMode.CrfOrCq)
            {
                args.Add("-qp");
                args.Add(Math.Clamp(profile.QualityValue, 1, 51).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                args.Add("-b:v");
                args.Add($"{computedBitrateKbps}k");
            }
        }
        else
        {
            // Software encoders: libx264, libx265, libvpx-vp9, libaom-av1
            if (codec is "libx264" or "libx265")
            {
                args.Add("-preset");
                args.Add(NormalizeSoftwarePreset(profile.Preset));
            }
            else if (codec == "libaom-av1")
            {
                args.Add("-cpu-used");
                args.Add("6");
            }
            else if (codec == "libvpx-vp9")
            {
                args.Add("-cpu-used");
                args.Add("2");
            }

            if (profile.RateControlMode == RateControlMode.CrfOrCq)
            {
                var maxCrf = isAv1OrVp9 ? 63 : 51;
                args.Add("-crf");
                args.Add(Math.Clamp(profile.QualityValue, 0, maxCrf).ToString(CultureInfo.InvariantCulture));
                if (isAv1OrVp9)
                {
                    args.Add("-b:v");
                    args.Add("0");
                }
            }
            else
            {
                args.Add("-b:v");
                args.Add($"{computedBitrateKbps}k");
            }

            if (passNumber > 0 && !string.IsNullOrEmpty(passLogPrefix))
            {
                args.Add("-pass");
                args.Add(passNumber.ToString(CultureInfo.InvariantCulture));
                args.Add("-passlogfile");
                args.Add(passLogPrefix);
            }
        }

        // Profile & Pixel format
        if (!string.IsNullOrWhiteSpace(profile.Profile) &&
            !profile.Profile.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !isAv1OrVp9)
        {
            args.Add("-profile:v");
            args.Add(profile.Profile);
        }

        if (!string.IsNullOrWhiteSpace(profile.PixelFormat) &&
            !profile.PixelFormat.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
            !isVaapi)
        {
            args.Add("-pix_fmt");
            args.Add(profile.PixelFormat);
        }
        else if (codec == "libx264")
        {
            // Maximum player compatibility for H.264
            args.Add("-pix_fmt");
            args.Add("yuv420p");
        }
    }

    public static (string OutputPath, bool ShouldSkip) ResolveOutputPath(string sourcePath, EncodingProfile profile)
    {
        var sourceDir = Path.GetDirectoryName(sourcePath) ?? Environment.CurrentDirectory;
        var outDir = profile.OutputDirectoryMode == OutputDirectoryMode.CustomFolder &&
                     !string.IsNullOrWhiteSpace(profile.CustomOutputDirectory)
            ? profile.CustomOutputDirectory.Trim()
            : sourceDir;

        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var template = string.IsNullOrWhiteSpace(profile.FileNameTemplate) ? "{name}_compressed" : profile.FileNameTemplate.Trim();
        var shortCodec = profile.VideoCodec
            .Replace("_nvenc", "")
            .Replace("_amf", "")
            .Replace("_qsv", "")
            .Replace("_videotoolbox", "")
            .Replace("_vaapi", "")
            .Replace("lib", "");

        var baseName = template
            .Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
            .Replace("{codec}", shortCodec, StringComparison.OrdinalIgnoreCase)
            .Replace("{preset}", profile.Preset, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(ch, '_');
        }
        if (string.IsNullOrWhiteSpace(baseName)) baseName = $"{stem}_compressed";

        var ext = "." + profile.ContainerFormat.TrimStart('.').ToLowerInvariant();
        var candidate = Path.Combine(outDir, baseName + ext);

        // Safety guard: never overwrite the input file in-place
        if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase))
        {
            baseName += "_compressed";
            candidate = Path.Combine(outDir, baseName + ext);
        }

        if (File.Exists(candidate))
        {
            if (profile.ConflictPolicy == ConflictPolicy.Skip)
            {
                return (candidate, true);
            }
            if (profile.ConflictPolicy == ConflictPolicy.AutoRename)
            {
                var idx = 1;
                while (File.Exists(candidate))
                {
                    candidate = Path.Combine(outDir, $"{baseName}_{idx}{ext}");
                    idx++;
                }
            }
        }

        return (candidate, false);
    }

    private static string BuildSampleOutputPath(string sourcePath, string containerFormat)
    {
        var sampleDir = Path.Combine(Path.GetTempPath(), "ShanaPress_Samples");
        Directory.CreateDirectory(sampleDir);
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = "." + containerFormat.TrimStart('.').ToLowerInvariant();
        return Path.Combine(sampleDir, $"{stem}_sample_{DateTime.Now:HHmmss}{ext}");
    }

    public static bool IsTwoPassCapableSoftwareEncoder(string codec) =>
        codec is "libx264" or "libx265" or "libvpx-vp9" or "libaom-av1";

    private static bool SupportsSoftSubtitlesForMedia(string container, MediaInfo? mediaInfo)
    {
        var fmt = container.ToLowerInvariant();
        if (fmt is "mkv" or "ts") return true;
        if (fmt is not ("mp4" or "mov")) return false;

        if (mediaInfo is null || !mediaInfo.HasSubtitles) return true;
        return mediaInfo.SubtitleStreams.Any(s =>
            s.CodecName.ToLowerInvariant() is "subrip" or "srt" or "ass" or "ssa" or "mov_text" or "webvtt" or "text");
    }

    private static string NormalizeNvencPreset(string preset) =>
        preset is "p1" or "p2" or "p3" or "p4" or "p5" or "p6" or "p7" ? preset : "p5";

    private static string NormalizeAmfQuality(string preset) =>
        preset is "speed" or "balanced" or "quality" ? preset : "balanced";

    private static string NormalizeQsvPreset(string preset) =>
        preset is "veryfast" or "faster" or "fast" or "medium" or "slow" or "slower" or "veryslow" ? preset : "medium";

    private static string NormalizeSoftwarePreset(string preset) =>
        preset is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast" or "medium" or "slow" or "slower" or "veryslow" ? preset : "medium";

    public static List<string> TokenizeArguments(string rawArgs)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(rawArgs)) return result;

        var sb = new StringBuilder();
        var inQuotes = false;
        var quoteChar = '"';

        for (var i = 0; i < rawArgs.Length; i++)
        {
            var c = rawArgs[i];
            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0)
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
        {
            result.Add(sb.ToString());
        }

        return result;
    }
}
