using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShanaPress.Core.Models;

namespace ShanaPress.Core.Services;

public sealed partial class MediaProbeService
{
    public async Task<MediaInfo> ProbeAsync(string ffprobePath, string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ffprobePath) || !File.Exists(ffprobePath))
        {
            throw new FileNotFoundException("找不到 ffprobe.exe", ffprobePath);
        }
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("找不到媒体文件", filePath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffprobePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("quiet");
        psi.ArgumentList.Add("-print_format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("-show_format");
        psi.ArgumentList.Add("-show_streams");
        psi.ArgumentList.Add(filePath);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffprobe 进程");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var json = await stdoutTask;
        var err = await stderrTask;
        if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"ffprobe 解析失败 (exit {proc.ExitCode}): {err}");
        }

        return ParseProbeJson(json, filePath);
    }

    public static MediaInfo ParseProbeJson(string json, string filePath)
    {
        var info = new MediaInfo { FilePath = filePath };
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("format", out var fmt))
        {
            info.FormatName = GetStr(fmt, "format_name");
            info.FormatLongName = GetStr(fmt, "format_long_name");
            if (double.TryParse(GetStr(fmt, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var durSec))
            {
                info.Duration = TimeSpan.FromSeconds(Math.Max(0, durSec));
            }
            if (long.TryParse(GetStr(fmt, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
            {
                info.FileSizeBytes = size;
            }
            if (long.TryParse(GetStr(fmt, "bit_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bps))
            {
                info.OverallBitrateKbps = bps / 1000;
            }
        }

        if (info.FileSizeBytes <= 0 && File.Exists(filePath))
        {
            info.FileSizeBytes = new FileInfo(filePath).Length;
        }

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                var codecType = GetStr(s, "codec_type");
                var index = s.TryGetProperty("index", out var idxProp) && idxProp.TryGetInt32(out var idx) ? idx : 0;

                if (codecType == "video")
                {
                    // Skip attached cover art / thumbnail pictures
                    if (s.TryGetProperty("disposition", out var disp) &&
                        disp.TryGetProperty("attached_pic", out var ap) &&
                        ap.TryGetInt32(out var apVal) && apVal == 1)
                    {
                        continue;
                    }

                    if (info.VideoStream is null)
                    {
                        var rFps = GetStr(s, "avg_frame_rate");
                        if (string.IsNullOrEmpty(rFps) || rFps == "0/0")
                        {
                            rFps = GetStr(s, "r_frame_rate");
                        }

                        var vs = new VideoStreamInfo
                        {
                            Index = index,
                            CodecName = GetStr(s, "codec_name"),
                            CodecLongName = GetStr(s, "codec_long_name"),
                            Profile = GetStr(s, "profile"),
                            Width = s.TryGetProperty("width", out var w) && w.TryGetInt32(out var wv) ? wv : 0,
                            Height = s.TryGetProperty("height", out var h) && h.TryGetInt32(out var hv) ? hv : 0,
                            RawFrameRate = rFps,
                            FrameRate = ParseRational(rFps),
                            PixelFormat = GetStr(s, "pix_fmt"),
                            ColorTransfer = GetStr(s, "color_transfer"),
                            ColorPrimaries = GetStr(s, "color_primaries"),
                            ColorSpace = GetStr(s, "color_space"),
                            BitrateKbps = long.TryParse(GetStr(s, "bit_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vbps)
                                ? vbps / 1000
                                : 0
                        };

                        if (info.Duration == TimeSpan.Zero &&
                            double.TryParse(GetStr(s, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sDur))
                        {
                            info.Duration = TimeSpan.FromSeconds(Math.Max(0, sDur));
                        }

                        info.VideoStream = vs;
                    }
                }
                else if (codecType == "audio")
                {
                    var lang = "und";
                    if (s.TryGetProperty("tags", out var tags))
                    {
                        var l = GetStr(tags, "language");
                        if (!string.IsNullOrWhiteSpace(l)) lang = l;
                    }

                    info.AudioStreams.Add(new AudioStreamInfo
                    {
                        Index = index,
                        CodecName = GetStr(s, "codec_name"),
                        Channels = s.TryGetProperty("channels", out var ch) && ch.TryGetInt32(out var chv) ? chv : 0,
                        ChannelLayout = GetStr(s, "channel_layout"),
                        SampleRate = int.TryParse(GetStr(s, "sample_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sr) ? sr : 0,
                        BitrateKbps = long.TryParse(GetStr(s, "bit_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var abps) ? abps / 1000 : 0,
                        Language = lang
                    });
                }
                else if (codecType == "subtitle")
                {
                    var lang = "und";
                    var title = string.Empty;
                    if (s.TryGetProperty("tags", out var tags))
                    {
                        var l = GetStr(tags, "language");
                        if (!string.IsNullOrWhiteSpace(l)) lang = l;
                        title = GetStr(tags, "title");
                    }

                    info.SubtitleStreams.Add(new SubtitleStreamInfo
                    {
                        Index = index,
                        CodecName = GetStr(s, "codec_name"),
                        Language = lang,
                        Title = title
                    });
                }
            }
        }

        return info;
    }

    public async Task<byte[]> ExtractFramePngAsync(
        string ffmpegPath,
        string filePath,
        TimeSpan timestamp,
        int maxWidth = 960,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException("找不到 ffmpeg.exe", ffmpegPath);
        }

        var sec = Math.Max(0, timestamp.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(sec);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add($"scale='min(iw,{maxWidth})':-2");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("image2pipe");
        psi.ArgumentList.Add("-vcodec");
        psi.ArgumentList.Add("png");
        psi.ArgumentList.Add("-");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg");
        using var ms = new MemoryStream();
        var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);

        await Task.WhenAll(copyTask, proc.WaitForExitAsync(ct));
        _ = await errTask;

        if (proc.ExitCode != 0 || ms.Length == 0)
        {
            return Array.Empty<byte>();
        }
        return ms.ToArray();
    }

    public async Task<(int Left, int Top, int Right, int Bottom)?> DetectCropAsync(
        string ffmpegPath,
        string filePath,
        TimeSpan timestamp,
        int sourceWidth,
        int sourceHeight,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath) || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return null;
        }

        var sec = Math.Max(0, timestamp.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-ss");
        psi.ArgumentList.Add(sec);
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(filePath);
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add("cropdetect=24:2:0");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("null");
        psi.ArgumentList.Add("-");

        using var proc = Process.Start(psi);
        if (proc is null) return null;

        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(ct));
        var stderr = await errTask;

        var matches = CropRegex().Matches(stderr);
        if (matches.Count == 0) return null;

        var last = matches[^1];
        var cw = int.Parse(last.Groups[1].Value, CultureInfo.InvariantCulture);
        var ch = int.Parse(last.Groups[2].Value, CultureInfo.InvariantCulture);
        var cx = int.Parse(last.Groups[3].Value, CultureInfo.InvariantCulture);
        var cy = int.Parse(last.Groups[4].Value, CultureInfo.InvariantCulture);

        var left = Math.Max(0, cx);
        var top = Math.Max(0, cy);
        var right = Math.Max(0, sourceWidth - (cx + cw));
        var bottom = Math.Max(0, sourceHeight - (cy + ch));
        return (left, top, right, bottom);
    }

    private static string GetStr(JsonElement element, string property)
    {
        if (element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static double ParseRational(string rational)
    {
        if (string.IsNullOrWhiteSpace(rational)) return 0;
        var slash = rational.IndexOf('/');
        if (slash < 0)
        {
            return double.TryParse(rational, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
        if (double.TryParse(rational[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
            double.TryParse(rational[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
            den > 0)
        {
            return num / den;
        }
        return 0;
    }

    [GeneratedRegex(@"crop=(\d+):(\d+):(\d+):(\d+)")]
    private static partial Regex CropRegex();
}
