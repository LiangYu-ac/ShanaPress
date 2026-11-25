using System.Text.Json;
using System.Text.Json.Serialization;
using ShanaPress.Core.Models;

namespace ShanaPress.Core.Services;

public sealed class PresetRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _presetsFilePath;

    public PresetRepository(string? storageDirectory = null)
    {
        var dir = storageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShanaPress");
        Directory.CreateDirectory(dir);
        _presetsFilePath = Path.Combine(dir, "presets.json");
    }

    public static List<PresetItem> GetBuiltInPresets()
    {
        var macPresets = new List<PresetItem>
        {
            new()
            {
                Id = "builtin-vt-hevc",
                Name = "Apple VideoToolbox HEVC 极速高质量 (MP4)",
                Category = "Apple 硬件加速",
                Description = "Apple Silicon / Mac 硬件加速 HEVC 编码，QuickTime 原生兼容",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "hevc_videotoolbox",
                    RateControlMode = RateControlMode.CrfOrCq,
                    QualityValue = 25,
                    AudioCodec = "aac",
                    AudioBitrateKbps = 128,
                    FastStart = true
                }
            },
            new()
            {
                Id = "builtin-vt-h264",
                Name = "Apple VideoToolbox H.264 极速通用 (MP4)",
                Category = "Apple 硬件加速",
                Description = "Mac 硬件加速 H.264 编码，兼容全平台播放",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "h264_videotoolbox",
                    RateControlMode = RateControlMode.CrfOrCq,
                    QualityValue = 23,
                    AudioCodec = "aac",
                    AudioBitrateKbps = 128,
                    FastStart = true
                }
            }
        };

        var linuxVaapiPreset = new PresetItem
        {
            Id = "builtin-vaapi-hevc",
            Name = "Linux VAAPI HEVC 硬件加速 (MP4)",
            Category = "GPU 硬件加速",
            Description = "Intel / AMD 集成与独立显卡 Linux VAAPI 硬件编码",
            IsBuiltIn = true,
            Profile = new EncodingProfile
            {
                ContainerFormat = "mp4",
                VideoCodec = "hevc_vaapi",
                RateControlMode = RateControlMode.CrfOrCq,
                QualityValue = 26,
                AudioCodec = "aac",
                AudioBitrateKbps = 128,
                FastStart = true
            }
        };

        var commonPresets = new List<PresetItem>
        {
            new()
            {
                Id = "builtin-nvenc-hevc",
                Name = "RTX NVENC HEVC 极速高质量 (MP4)",
                Category = "GPU 硬件加速",
                Description = "NVIDIA 显卡硬件加速，体积小画质高，速度极快",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "hevc_nvenc",
                    RateControlMode = RateControlMode.CrfOrCq,
                    QualityValue = 26,
                    Preset = "p5",
                    NvencSpatialAq = true,
                    AudioCodec = "aac",
                    AudioBitrateKbps = 128,
                    FastStart = true
                }
            },
            new()
            {
                Id = "builtin-nvenc-h264",
                Name = "RTX NVENC H.264 极速通用 (MP4)",
                Category = "GPU 硬件加速",
                Description = "最强设备兼容性 + NVIDIA 显卡极速编码",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "h264_nvenc",
                    RateControlMode = RateControlMode.CrfOrCq,
                    QualityValue = 24,
                    Preset = "p5",
                    NvencSpatialAq = true,
                    AudioCodec = "aac",
                    AudioBitrateKbps = 128,
                    FastStart = true
                }
            },
            new()
            {
                Id = "builtin-nvenc-av1",
                Name = "RTX NVENC AV1 极限压缩 (MP4)",
                Category = "GPU 硬件加速",
                Description = "RTX 40 系专属 AV1 硬件编码，同等画质体积最小",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "av1_nvenc",
                    RateControlMode = RateControlMode.CrfOrCq,
                    QualityValue = 30,
                    Preset = "p5",
                    NvencSpatialAq = true,
                    AudioCodec = "aac",
                    AudioBitrateKbps = 128,
                    FastStart = true
                }
            },
            new()
            {
                Id = "builtin-cpu-x264",
                Name = "CPU x264 通用兼容高画质 (MP4)",
                Category = "CPU 高画质",
                Description = "经典软件编码，所有手机/网页/播放器完美兼容",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "libx264",
                    RateControlMode = RateControlMode.CrfOrCq,
                    QualityValue = 23,
                    Preset = "medium",
                    PixelFormat = "yuv420p",
                    AudioCodec = "aac",
                    AudioBitrateKbps = 128,
                    FastStart = true
                }
            },
            new()
            {
                Id = "builtin-cpu-x265",
                Name = "CPU x265 高压缩比存档 (MKV)",
                Category = "CPU 高画质",
                Description = "高压缩率存档首选，适合收藏高清片源",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mkv",
                    VideoCodec = "libx265",
                    RateControlMode = RateControlMode.CrfOrCq,
                    QualityValue = 26,
                    Preset = "medium",
                    AudioCodec = "libopus",
                    AudioBitrateKbps = 128
                }
            },
            new()
            {
                Id = "builtin-social-720p",
                Name = "微信 / 社交分享小体积 (720p MP4)",
                Category = "社交与限额",
                Description = "限制 720p 30fps，大幅瘦身方便社交软件发送",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "libx264",
                    RateControlMode = RateControlMode.CrfOrCq,
                    QualityValue = 26,
                    Preset = "fast",
                    PixelFormat = "yuv420p",
                    ResizeMode = ResizeMode.PresetHeight,
                    ResizeHeight = 720,
                    FrameRateMode = FrameRateMode.Custom,
                    TargetFps = 30,
                    AudioCodec = "aac",
                    AudioBitrateKbps = 96,
                    FastStart = true
                }
            },
            new()
            {
                Id = "builtin-target-25mb",
                Name = "按目标体积压缩 25MB (2-Pass MP4)",
                Category = "社交与限额",
                Description = "自动计算码率并采用二压，精准控制在 25MB 以内",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "libx264",
                    RateControlMode = RateControlMode.TargetFileSize,
                    TargetFileSizeMb = 25.0,
                    TargetSizeUseTwoPass = true,
                    Preset = "medium",
                    PixelFormat = "yuv420p",
                    AudioCodec = "aac",
                    AudioBitrateKbps = 128,
                    FastStart = true
                }
            },
            new()
            {
                Id = "builtin-stream-copy",
                Name = "无损快速改封装 (Stream Copy MP4)",
                Category = "实用工具",
                Description = "不重编码直接复制音视频流，秒级完成格式转换或剪辑",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp4",
                    VideoCodec = "copy",
                    AudioCodec = "copy",
                    FastStart = true
                }
            },
            new()
            {
                Id = "builtin-audio-mp3",
                Name = "仅提取音频 (MP3 192k)",
                Category = "实用工具",
                Description = "剥离视频画面，提取高质量 MP3 音轨",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "mp3",
                    VideoCodec = "none",
                    AudioCodec = "libmp3lame",
                    AudioBitrateKbps = 192
                }
            },
            new()
            {
                Id = "builtin-gif",
                Name = "高清动态表情图 (GIF 15fps)",
                Category = "实用工具",
                Description = "双通道调色板优化，生成流畅清晰的 GIF 动图",
                IsBuiltIn = true,
                Profile = new EncodingProfile
                {
                    ContainerFormat = "gif",
                    VideoCodec = "gif",
                    FrameRateMode = FrameRateMode.Custom,
                    TargetFps = 15,
                    AudioCodec = "none",
                    SubtitleMode = SubtitleMode.Remove
                }
            }
        };

        var ordered = new List<PresetItem>();
        if (OperatingSystem.IsMacOS())
        {
            ordered.AddRange(macPresets);
            ordered.AddRange(commonPresets);
        }
        else if (OperatingSystem.IsLinux())
        {
            ordered.Add(commonPresets[0]); // nvenc hevc
            ordered.Add(linuxVaapiPreset);
            ordered.AddRange(commonPresets.Skip(1));
        }
        else
        {
            ordered.AddRange(commonPresets);
            ordered.AddRange(macPresets);
            ordered.Add(linuxVaapiPreset);
        }
        return ordered;
    }

    public List<PresetItem> LoadAll(HardwareCapabilities? caps = null)
    {
        var result = GetBuiltInPresets();
        if (File.Exists(_presetsFilePath))
        {
            try
            {
                var json = File.ReadAllText(_presetsFilePath);
                var custom = JsonSerializer.Deserialize<List<PresetItem>>(json, JsonOptions);
                if (custom is not null)
                {
                    foreach (var item in custom)
                    {
                        item.IsBuiltIn = false;
                        result.Add(item);
                    }
                }
            }
            catch
            {
                // Corrupt user preset file should not break startup
            }
        }
        return result;
    }

    public static string RecommendDefaultPresetId(HardwareCapabilities caps)
    {
        if (caps.HasVideoToolboxHevc) return "builtin-vt-hevc";
        if (caps.HasVideoToolboxH264) return "builtin-vt-h264";
        if (caps.HasNvencHevc) return "builtin-nvenc-hevc";
        if (caps.HasNvencH264) return "builtin-nvenc-h264";
        if (caps.HasVaapiHevc) return "builtin-vaapi-hevc";
        return "builtin-cpu-x264";
    }

    public void SaveCustomPresets(IEnumerable<PresetItem> allPresets)
    {
        var customOnly = allPresets.Where(p => !p.IsBuiltIn).ToList();
        var json = JsonSerializer.Serialize(customOnly, JsonOptions);
        File.WriteAllText(_presetsFilePath, json);
    }

    public static void ExportPreset(PresetItem preset, string targetPath)
    {
        var clone = new PresetItem
        {
            Id = preset.Id,
            Name = preset.Name,
            Category = preset.Category,
            Description = preset.Description,
            IsBuiltIn = false,
            Profile = preset.Profile.Clone()
        };
        var json = JsonSerializer.Serialize(clone, JsonOptions);
        File.WriteAllText(targetPath, json);
    }

    public static PresetItem? ImportPreset(string sourcePath)
    {
        var json = File.ReadAllText(sourcePath);
        var item = JsonSerializer.Deserialize<PresetItem>(json, JsonOptions);
        if (item is null) return null;
        item.Id = Guid.NewGuid().ToString("N");
        item.IsBuiltIn = false;
        if (string.IsNullOrWhiteSpace(item.Name))
        {
            item.Name = Path.GetFileNameWithoutExtension(sourcePath);
        }
        return item;
    }
}
