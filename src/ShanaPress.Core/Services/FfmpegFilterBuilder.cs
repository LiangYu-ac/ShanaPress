using System.Globalization;
using System.Text;
using ShanaPress.Core.Models;

namespace ShanaPress.Core.Services;

public sealed class FilterGraphBuildResult
{
    public bool UseFilterComplex { get; init; }
    public string? VideoFilter { get; init; }
    public string? FilterComplex { get; init; }
    public string? ExtraInputPath { get; init; }
    public string? VideoMapLabel { get; init; }
}

public static class FfmpegFilterBuilder
{
    /// <summary>
    /// Escapes a Windows file path for use inside single quotes in FFmpeg filters (subtitles, ass, fontfile).
    /// Example: C:\dir [1]\sub'1'.srt -> C\:/dir [1]/sub'\\\''1'\\\''.srt
    /// </summary>
    public static string EscapeFilterPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var forward = path.Replace('\\', '/');
        return forward
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "'\\\\\\''", StringComparison.Ordinal);
    }

    /// <summary>
    /// Escapes text for FFmpeg drawtext with expansion=none inside single quotes.
    /// </summary>
    public static string EscapeDrawtextText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "'\\''", StringComparison.Ordinal);
    }

    public static FilterGraphBuildResult BuildVideoFilterGraph(
        EncodingProfile profile,
        MediaInfo? mediaInfo,
        bool cropEnabled,
        int cropLeft,
        int cropTop,
        int cropRight,
        int cropBottom,
        SubtitleMode effectiveSubtitleMode,
        string? effectiveSubtitlePath)
    {
        if (profile.IsVideoDisabled || profile.IsVideoCopy)
        {
            return new FilterGraphBuildResult();
        }

        var filters = new List<string>();

        // 1. Deinterlace
        if (profile.DeinterlaceMode == DeinterlaceMode.Bwdif)
        {
            filters.Add("bwdif=mode=0:parity=-1:deint=1");
        }
        else if (profile.DeinterlaceMode == DeinterlaceMode.Yadif)
        {
            filters.Add("yadif=mode=0:parity=-1:deint=0");
        }

        // 2. Crop
        if (cropEnabled && (cropLeft > 0 || cropTop > 0 || cropRight > 0 || cropBottom > 0))
        {
            var l = Math.Max(0, cropLeft);
            var t = Math.Max(0, cropTop);
            var r = Math.Max(0, cropRight);
            var b = Math.Max(0, cropBottom);
            filters.Add($"crop=trunc((iw-{l}-{r})/2)*2:trunc((ih-{t}-{b})/2)*2:{l}:{t}");
        }

        // 3. Rotation & Flip
        switch (profile.Rotation)
        {
            case RotationMode.Cw90:
                filters.Add("transpose=1");
                break;
            case RotationMode.Ccw90:
                filters.Add("transpose=2");
                break;
            case RotationMode.Rotate180:
                filters.Add("transpose=1,transpose=1");
                break;
        }

        if (profile.FlipHorizontal) filters.Add("hflip");
        if (profile.FlipVertical) filters.Add("vflip");

        // 4. Resize / Scale
        if (profile.ResizeMode == ResizeMode.PresetHeight && profile.ResizeHeight > 0)
        {
            filters.Add($"scale=-2:{profile.ResizeHeight}:flags=lanczos");
        }
        else if (profile.ResizeMode == ResizeMode.Custom && profile.CustomWidth > 0 && profile.CustomHeight > 0)
        {
            var w = MakeEven(profile.CustomWidth);
            var h = MakeEven(profile.CustomHeight);
            if (profile.KeepAspectRatio)
            {
                filters.Add($"scale={w}:{h}:force_original_aspect_ratio=decrease:flags=lanczos,scale=trunc(iw/2)*2:trunc(ih/2)*2");
            }
            else
            {
                filters.Add($"scale={w}:{h}:flags=lanczos");
            }
        }
        else if (profile.IsGifContainer)
        {
            // Keep GIF width sane if not explicitly resized
            filters.Add("scale='min(640,iw)':-2:flags=lanczos");
        }

        // 5. Frame Rate
        if (profile.FrameRateMode == FrameRateMode.Custom && profile.TargetFps > 0)
        {
            filters.Add($"fps={profile.TargetFps.ToString("0.###", CultureInfo.InvariantCulture)}");
        }
        else if (profile.IsGifContainer)
        {
            filters.Add("fps=15");
        }

        // 6. HDR to SDR Tone Mapping (verified zscale chain)
        if (profile.HdrToSdr)
        {
            var tin = (mediaInfo?.VideoStream?.IsHlg ?? false) ? "arib-std-b67" : "smpte2084";
            var algo = profile.HdrTonemapAlgorithm switch
            {
                "mobius" => "mobius",
                "reinhard" => "reinhard",
                _ => "hable"
            };
            filters.Add($"zscale=pin=bt2020:tin={tin}:min=bt2020nc:rin=tv:t=linear:npl=100:p=bt709,format=gbrpf32le,tonemap=tonemap={algo}:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
        }

        // 7. Denoise
        switch (profile.DenoiseStrength)
        {
            case FilterStrength.Light:
                filters.Add("hqdn3d=1.5:1.5:6:6");
                break;
            case FilterStrength.Medium:
                filters.Add("hqdn3d=3:2:6:4.5");
                break;
            case FilterStrength.Strong:
                filters.Add("hqdn3d=5:4:8:6");
                break;
        }

        // 8. Sharpen
        switch (profile.SharpenStrength)
        {
            case FilterStrength.Light:
                filters.Add("unsharp=5:5:0.5:5:5:0.0");
                break;
            case FilterStrength.Medium:
                filters.Add("unsharp=5:5:0.9:5:5:0.0");
                break;
            case FilterStrength.Strong:
                filters.Add("unsharp=5:5:1.4:5:5:0.0");
                break;
        }

        // 9. Hardsub Burn-in
        if (effectiveSubtitleMode == SubtitleMode.BurnIn &&
            !string.IsNullOrWhiteSpace(effectiveSubtitlePath) &&
            File.Exists(effectiveSubtitlePath))
        {
            var escapedSub = EscapeFilterPath(effectiveSubtitlePath);
            var ext = Path.GetExtension(effectiveSubtitlePath);
            if (ext.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".ssa", StringComparison.OrdinalIgnoreCase))
            {
                filters.Add($"ass='{escapedSub}'");
            }
            else
            {
                var fontName = string.IsNullOrWhiteSpace(profile.SubtitleFontName) ? "Microsoft YaHei" : profile.SubtitleFontName.Replace("'", "", StringComparison.Ordinal);
                var fontSize = Math.Clamp(profile.SubtitleFontSize, 8, 96);
                var outline = Math.Clamp(profile.SubtitleOutline, 0, 6).ToString("0.##", CultureInfo.InvariantCulture);
                filters.Add($"subtitles='{escapedSub}':force_style='FontName={fontName},FontSize={fontSize},Outline={outline}'");
            }
        }

        // 10. Text Watermark
        if (profile.TextWatermarkEnabled && !string.IsNullOrWhiteSpace(profile.TextWatermarkContent))
        {
            var fontPath = ResolveSystemFontPath();
            var fontPart = !string.IsNullOrEmpty(fontPath) ? $"fontfile='{EscapeFilterPath(fontPath)}':" : string.Empty;
            var escapedText = EscapeDrawtextText(profile.TextWatermarkContent);
            var size = Math.Clamp(profile.TextWatermarkFontSize, 10, 140);
            var alpha = Math.Clamp(profile.TextWatermarkOpacity, 0.05, 1.0).ToString("0.00", CultureInfo.InvariantCulture);
            var color = string.IsNullOrWhiteSpace(profile.TextWatermarkColor) ? "white" : profile.TextWatermarkColor.Trim();
            var (xExpr, yExpr) = GetDrawtextCoords(profile.TextWatermarkPosition, Math.Max(4, profile.WatermarkMargin));

            filters.Add($"drawtext={fontPart}expansion=none:text='{escapedText}':fontsize={size}:fontcolor={color}@{alpha}:x={xExpr}:y={yExpr}:shadowcolor=black@0.55:shadowx=2:shadowy=2");
        }

        // Check Image Watermark & GIF Palettegen -> requires -filter_complex
        var hasImageWatermark = profile.ImageWatermarkEnabled &&
                                !string.IsNullOrWhiteSpace(profile.ImageWatermarkPath) &&
                                File.Exists(profile.ImageWatermarkPath);

        if (hasImageWatermark || profile.IsGifContainer)
        {
            var graph = new StringBuilder();
            var currentLabel = "0:v";

            if (filters.Count > 0)
            {
                graph.Append($"[{currentLabel}]{string.Join(",", filters)}[vbase]");
                currentLabel = "vbase";
            }

            if (hasImageWatermark)
            {
                var scalePct = Math.Clamp(profile.WatermarkScalePercent, 3, 80);
                var opacity = Math.Clamp(profile.WatermarkOpacity, 0.05, 1.0).ToString("0.00", CultureInfo.InvariantCulture);
                var (ox, oy) = GetOverlayCoords(profile.WatermarkPosition, Math.Max(0, profile.WatermarkMargin));

                if (graph.Length > 0) graph.Append(';');
                graph.Append($"[1:v][{currentLabel}]scale2ref=w=iw*0+main_w*{scalePct}/100:h=ow/mdar[wm_s][vbase_ref];");
                graph.Append($"[wm_s]format=rgba,colorchannelmixer=aa={opacity}[wm_a];");
                graph.Append($"[vbase_ref][wm_a]overlay={ox}:{oy}[vwm]");
                currentLabel = "vwm";
            }

            if (profile.IsGifContainer)
            {
                if (graph.Length > 0) graph.Append(';');
                graph.Append($"[{currentLabel}]split[gif0][gif1];[gif0]palettegen=stats_mode=diff[gifp];[gif1][gifp]paletteuse=dither=bayer:bayer_scale=3[vgif]");
                currentLabel = "vgif";
            }

            return new FilterGraphBuildResult
            {
                UseFilterComplex = true,
                FilterComplex = graph.ToString(),
                ExtraInputPath = hasImageWatermark ? profile.ImageWatermarkPath : null,
                VideoMapLabel = $"[{currentLabel}]"
            };
        }

        // Ensure even dimensions when converting to yuv420p if no resize/crop already handled it
        return new FilterGraphBuildResult
        {
            UseFilterComplex = false,
            VideoFilter = filters.Count > 0 ? string.Join(",", filters) : null
        };
    }

    public static string? BuildAudioFilter(EncodingProfile profile)
    {
        if (profile.IsGifContainer ||
            profile.AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            profile.AudioCodec.Equals("copy", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = new List<string>();
        if (Math.Abs(profile.VolumeDb) >= 0.1)
        {
            parts.Add($"volume={profile.VolumeDb.ToString("0.##", CultureInfo.InvariantCulture)}dB");
        }
        if (profile.EnableLoudnorm)
        {
            parts.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }

        return parts.Count > 0 ? string.Join(",", parts) : null;
    }

    private static (string X, string Y) GetOverlayCoords(WatermarkPosition pos, int margin) => pos switch
    {
        WatermarkPosition.TopLeft => ($"{margin}", $"{margin}"),
        WatermarkPosition.TopRight => ($"W-w-{margin}", $"{margin}"),
        WatermarkPosition.BottomLeft => ($"{margin}", $"H-h-{margin}"),
        WatermarkPosition.Center => ("(W-w)/2", "(H-h)/2"),
        _ => ($"W-w-{margin}", $"H-h-{margin}")
    };

    private static (string X, string Y) GetDrawtextCoords(WatermarkPosition pos, int margin) => pos switch
    {
        WatermarkPosition.TopLeft => ($"{margin}", $"{margin}"),
        WatermarkPosition.TopRight => ($"w-tw-{margin}", $"{margin}"),
        WatermarkPosition.BottomLeft => ($"{margin}", $"h-th-{margin}"),
        WatermarkPosition.Center => ("(w-tw)/2", "(h-th)/2"),
        _ => ($"w-tw-{margin}", $"h-th-{margin}")
    };

    private static int MakeEven(int value) => Math.Max(2, value - (value % 2));

    private static string ResolveSystemFontPath()
    {
        var fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] candidates =
        [
            Path.Combine(fontsDir, "msyh.ttc"),
            Path.Combine(fontsDir, "msyhbd.ttc"),
            Path.Combine(fontsDir, "simhei.ttf"),
            Path.Combine(fontsDir, "simsun.ttc"),
            Path.Combine(fontsDir, "arial.ttf")
        ];
        return candidates.FirstOrDefault(File.Exists) ?? string.Empty;
    }
}
