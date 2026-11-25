using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ShanaPress.Core.Models;
using ShanaPress.Core.Services;
using ShanaPress.Desktop.Infrastructure;
using ShanaPress.Desktop.Views;

namespace ShanaPress.Desktop.ViewModels;

public sealed class QuickSettingsViewModel : ObservableObject
{
    private readonly HardwareCapabilities _capabilities;
    private readonly PresetRepository _presetRepo;
    private readonly ObservableCollection<PresetItem> _mainPresets;
    private readonly QueueItem? _contextItem;
    private readonly AppSettings _appSettings;
    private readonly Func<Task> _redetectHardwareAsync;
    private readonly List<IRaisableCommand> _commands = new();

    private PresetItem? _selectedPreset;
    private string _cliPreview = string.Empty;
    private string _hardwareStatusSummary = string.Empty;

    public QuickSettingsViewModel(
        EncodingProfile currentProfile,
        HardwareCapabilities capabilities,
        PresetRepository presetRepo,
        ObservableCollection<PresetItem> mainPresets,
        QueueItem? contextItem,
        AppSettings appSettings,
        Func<Task> redetectHardwareAsync)
    {
        WorkingProfile = currentProfile.Clone();
        _capabilities = capabilities;
        _presetRepo = presetRepo;
        _mainPresets = mainPresets;
        _contextItem = contextItem;
        _appSettings = appSettings;
        _redetectHardwareAsync = redetectHardwareAsync;

        BuildOptionLists();

        BrowseSubtitleCommand = Reg(new AsyncRelayCommand(BrowseSubtitleAsync));
        ClearSubtitleCommand = Reg(new RelayCommand(() => SubtitleFilePath = string.Empty));
        BrowseImageWatermarkCommand = Reg(new AsyncRelayCommand(BrowseImageWatermarkAsync));
        BrowseFfmpegCommand = Reg(new AsyncRelayCommand(() => BrowseExecutableAsync(true)));
        BrowseFfprobeCommand = Reg(new AsyncRelayCommand(() => BrowseExecutableAsync(false)));
        RedetectHardwareCommand = Reg(new AsyncRelayCommand(async () =>
        {
            _appSettings.CustomFfmpegPath = CustomFfmpegPath;
            _appSettings.CustomFfprobePath = CustomFfprobePath;
            await _redetectHardwareAsync();
            BuildEncoderOptions();
            RefreshHardwareSummary();
        }));

        LoadPresetCommand = Reg(new RelayCommand(LoadSelectedPreset, () => SelectedPreset is not null));
        SaveNewPresetCommand = Reg(new AsyncRelayCommand(SaveNewPresetAsync));
        OverwritePresetCommand = Reg(new AsyncRelayCommand(OverwriteSelectedPresetAsync, () => SelectedPreset is { IsBuiltIn: false }));
        DeletePresetCommand = Reg(new AsyncRelayCommand(DeleteSelectedPresetAsync, () => SelectedPreset is { IsBuiltIn: false }));
        ImportPresetCommand = Reg(new AsyncRelayCommand(ImportPresetAsync));
        ExportPresetCommand = Reg(new AsyncRelayCommand(ExportPresetAsync, () => SelectedPreset is not null));
        ResetDefaultsCommand = Reg(new RelayCommand(ResetDefaults));
        CopyCliCommand = Reg(new AsyncRelayCommand(async () =>
        {
            var top = DialogOwner ?? MainViewModel.GetMainWindow();
            if (!string.IsNullOrWhiteSpace(CliPreview) && top?.Clipboard is not null)
            {
                await top.Clipboard.SetTextAsync(CliPreview);
            }
        }));

        RefreshHardwareSummary();
        RefreshCliPreview();
    }

    public Window? DialogOwner { get; set; }
    public EncodingProfile WorkingProfile { get; private set; }
    public ObservableCollection<PresetItem> Presets => _mainPresets;

    public ObservableCollection<ChoiceOption<string>> ContainerOptions { get; } = new();
    public ObservableCollection<ChoiceOption<string>> HardwareDecodeOptions { get; } = new();
    public ObservableCollection<ChoiceOption<string>> VideoCodecOptions { get; } = new();
    public ObservableCollection<ChoiceOption<string>> EncoderPresetOptions { get; } = new();
    public ObservableCollection<ChoiceOption<string>> VideoProfileOptions { get; } = new();
    public ObservableCollection<ChoiceOption<string>> PixelFormatOptions { get; } = new();
    public ObservableCollection<ChoiceOption<int>> PresetHeightOptions { get; } = new();
    public ObservableCollection<ChoiceOption<double>> TargetFpsOptions { get; } = new();

    public ObservableCollection<ChoiceOption<string>> AudioCodecOptions { get; } = new();
    public ObservableCollection<ChoiceOption<int>> AudioBitrateOptions { get; } = new();
    public ObservableCollection<ChoiceOption<int>> SampleRateOptions { get; } = new();
    public ObservableCollection<ChoiceOption<int>> ChannelOptions { get; } = new();

    public ObservableCollection<ChoiceOption<RotationMode>> RotationOptions { get; } = new();
    public ObservableCollection<ChoiceOption<DeinterlaceMode>> DeinterlaceOptions { get; } = new();
    public ObservableCollection<ChoiceOption<string>> TonemapOptions { get; } = new();
    public ObservableCollection<ChoiceOption<FilterStrength>> StrengthOptions { get; } = new();
    public ObservableCollection<ChoiceOption<SubtitleMode>> SubtitleModeOptions { get; } = new();
    public ObservableCollection<ChoiceOption<WatermarkPosition>> WatermarkPositionOptions { get; } = new();
    public ObservableCollection<ChoiceOption<string>> WatermarkColorOptions { get; } = new();

    public AsyncRelayCommand BrowseSubtitleCommand { get; }
    public RelayCommand ClearSubtitleCommand { get; }
    public AsyncRelayCommand BrowseImageWatermarkCommand { get; }
    public AsyncRelayCommand BrowseFfmpegCommand { get; }
    public AsyncRelayCommand BrowseFfprobeCommand { get; }
    public AsyncRelayCommand RedetectHardwareCommand { get; }
    public RelayCommand LoadPresetCommand { get; }
    public AsyncRelayCommand SaveNewPresetCommand { get; }
    public AsyncRelayCommand OverwritePresetCommand { get; }
    public AsyncRelayCommand DeletePresetCommand { get; }
    public AsyncRelayCommand ImportPresetCommand { get; }
    public AsyncRelayCommand ExportPresetCommand { get; }
    public RelayCommand ResetDefaultsCommand { get; }
    public AsyncRelayCommand CopyCliCommand { get; }

    private T Reg<T>(T cmd) where T : IRaisableCommand
    {
        _commands.Add(cmd);
        return cmd;
    }

    public string CliPreview { get => _cliPreview; private set => SetProperty(ref _cliPreview, value); }
    public string HardwareStatusSummary { get => _hardwareStatusSummary; private set => SetProperty(ref _hardwareStatusSummary, value); }

    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                foreach (var c in _commands) c.RaiseCanExecuteChanged();
            }
        }
    }

    // SelectedItem ChoiceOption helpers for Avalonia ComboBoxes
    public ChoiceOption<string>? SelectedContainerOption
    {
        get => ContainerOptions.FirstOrDefault(o => o.Value == WorkingProfile.ContainerFormat);
        set
        {
            if (value is null || WorkingProfile.ContainerFormat == value.Value) return;
            WorkingProfile.ContainerFormat = value.Value;
            if (value.Value.Equals("mp3", StringComparison.OrdinalIgnoreCase))
            {
                WorkingProfile.AudioCodec = "libmp3lame";
                OnPropertyChanged(nameof(SelectedAudioCodecOption));
            }
            else if (value.Value.Equals("flac", StringComparison.OrdinalIgnoreCase))
            {
                WorkingProfile.AudioCodec = "flac";
                OnPropertyChanged(nameof(SelectedAudioCodecOption));
            }
            else if (value.Value.Equals("gif", StringComparison.OrdinalIgnoreCase))
            {
                WorkingProfile.AudioCodec = "none";
                OnPropertyChanged(nameof(SelectedAudioCodecOption));
            }
            OnChanged();
            OnPropertyChanged(nameof(IsVideoEncoderConfigurable));
            OnPropertyChanged(nameof(EstimatedTargetBitrateHint));
        }
    }

    public ChoiceOption<string>? SelectedHardwareDecodeOption
    {
        get => HardwareDecodeOptions.FirstOrDefault(o => o.Value == WorkingProfile.HardwareDecode);
        set { if (value is not null) { WorkingProfile.HardwareDecode = value.Value; OnChanged(); } }
    }

    public bool FastStart { get => WorkingProfile.FastStart; set { WorkingProfile.FastStart = value; OnChanged(); } }
    public bool CopyMetadata { get => WorkingProfile.CopyMetadata; set { WorkingProfile.CopyMetadata = value; OnChanged(); } }

    public ChoiceOption<string>? SelectedVideoCodecOption
    {
        get => VideoCodecOptions.FirstOrDefault(o => o.Value == WorkingProfile.VideoCodec);
        set
        {
            if (value is not null && WorkingProfile.VideoCodec != value.Value)
            {
                WorkingProfile.VideoCodec = value.Value;
                RebuildPresetOptionsForCodec(value.Value);
                OnChanged();
                OnPropertyChanged(nameof(MaxQualitySlider));
                OnPropertyChanged(nameof(IsNvencCodec));
                OnPropertyChanged(nameof(IsVideoEncoderConfigurable));
                OnPropertyChanged(nameof(QualityGuidanceText));
            }
        }
    }

    public bool IsVideoEncoderConfigurable => !WorkingProfile.IsVideoDisabled && !WorkingProfile.IsVideoCopy && !WorkingProfile.IsGifContainer;
    public bool IsNvencCodec => WorkingProfile.VideoCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase);

    public RateControlMode RateControlMode
    {
        get => WorkingProfile.RateControlMode;
        set
        {
            if (WorkingProfile.RateControlMode != value)
            {
                WorkingProfile.RateControlMode = value;
                OnChanged();
                OnPropertyChanged(nameof(IsCrfMode));
                OnPropertyChanged(nameof(IsAbrMode));
                OnPropertyChanged(nameof(IsTwoPassMode));
                OnPropertyChanged(nameof(IsBitrateMode));
                OnPropertyChanged(nameof(IsTargetSizeMode));
                OnPropertyChanged(nameof(EstimatedTargetBitrateHint));
            }
        }
    }

    public bool IsCrfMode { get => RateControlMode == RateControlMode.CrfOrCq; set { if (value) RateControlMode = RateControlMode.CrfOrCq; } }
    public bool IsAbrMode { get => RateControlMode == RateControlMode.AverageBitrate; set { if (value) RateControlMode = RateControlMode.AverageBitrate; } }
    public bool IsTwoPassMode { get => RateControlMode == RateControlMode.TwoPassBitrate; set { if (value) RateControlMode = RateControlMode.TwoPassBitrate; } }
    public bool IsBitrateMode => RateControlMode is RateControlMode.AverageBitrate or RateControlMode.TwoPassBitrate;
    public bool IsTargetSizeMode { get => RateControlMode == RateControlMode.TargetFileSize; set { if (value) RateControlMode = RateControlMode.TargetFileSize; } }

    public int MaxQualitySlider =>
        WorkingProfile.VideoCodec is "av1_nvenc" or "libaom-av1" or "libvpx-vp9" ? 63 : 51;

    public int QualityValue
    {
        get => WorkingProfile.QualityValue;
        set { WorkingProfile.QualityValue = value; OnChanged(); OnPropertyChanged(nameof(QualityGuidanceText)); }
    }

    public string QualityGuidanceText
    {
        get
        {
            var q = QualityValue;
            var tag = IsNvencCodec ? "CQ" : WorkingProfile.VideoCodec.Contains("vaapi") ? "QP" : "CRF";
            var desc = q <= 18 ? "视觉无损 / 体积较大"
                : q <= 23 ? "高画质推荐"
                : q <= 28 ? "画质与体积均衡"
                : q <= 34 ? "高压缩比 / 轻微画质损失"
                : "极限压缩";
            return $"{tag} {q} ({desc} · 数值越小画质越高)";
        }
    }

    public int VideoBitrateKbps { get => WorkingProfile.VideoBitrateKbps; set { WorkingProfile.VideoBitrateKbps = Math.Clamp(value, 100, 100000); OnChanged(); } }

    public double TargetFileSizeMb
    {
        get => WorkingProfile.TargetFileSizeMb;
        set
        {
            WorkingProfile.TargetFileSizeMb = Math.Clamp(value, 0.5, 50000);
            OnChanged();
            OnPropertyChanged(nameof(EstimatedTargetBitrateHint));
        }
    }

    public bool TargetSizeUseTwoPass { get => WorkingProfile.TargetSizeUseTwoPass; set { WorkingProfile.TargetSizeUseTwoPass = value; OnChanged(); } }

    public string EstimatedTargetBitrateHint
    {
        get
        {
            var dur = _contextItem?.MediaInfo?.Duration.TotalSeconds ?? 0;
            var durLabel = dur > 0 ? $"按选中视频 {BitrateCalculator.FormatDuration(TimeSpan.FromSeconds(dur))}" : "按 3 分钟示例时长";
            var effectiveDur = dur > 0 ? dur : 180.0;
            var hasAudio = !WorkingProfile.AudioCodec.Equals("none", StringComparison.OrdinalIgnoreCase);
            var kbps = BitrateCalculator.CalculateTargetVideoBitrateKbps(TargetFileSizeMb, effectiveDur, WorkingProfile.AudioBitrateKbps, hasAudio);
            return $"{durLabel}估算视频码率 ≈ {kbps:N0} kbps";
        }
    }

    public ChoiceOption<string>? SelectedEncoderPresetOption
    {
        get => EncoderPresetOptions.FirstOrDefault(o => o.Value == WorkingProfile.Preset);
        set { if (value is not null) { WorkingProfile.Preset = value.Value; OnChanged(); } }
    }

    public ChoiceOption<string>? SelectedVideoProfileOption
    {
        get => VideoProfileOptions.FirstOrDefault(o => o.Value == WorkingProfile.Profile);
        set { if (value is not null) { WorkingProfile.Profile = value.Value; OnChanged(); } }
    }

    public ChoiceOption<string>? SelectedPixelFormatOption
    {
        get => PixelFormatOptions.FirstOrDefault(o => o.Value == WorkingProfile.PixelFormat);
        set { if (value is not null) { WorkingProfile.PixelFormat = value.Value; OnChanged(); } }
    }

    public bool NvencSpatialAq { get => WorkingProfile.NvencSpatialAq; set { WorkingProfile.NvencSpatialAq = value; OnChanged(); } }
    public bool NvencTemporalAq { get => WorkingProfile.NvencTemporalAq; set { WorkingProfile.NvencTemporalAq = value; OnChanged(); } }

    public ResizeMode ResizeMode
    {
        get => WorkingProfile.ResizeMode;
        set
        {
            WorkingProfile.ResizeMode = value;
            OnChanged();
            OnPropertyChanged(nameof(IsResizeOriginal));
            OnPropertyChanged(nameof(IsPresetHeightMode));
            OnPropertyChanged(nameof(IsCustomResizeMode));
        }
    }

    public bool IsResizeOriginal { get => ResizeMode == ResizeMode.Original; set { if (value) ResizeMode = ResizeMode.Original; } }
    public bool IsPresetHeightMode { get => ResizeMode == ResizeMode.PresetHeight; set { if (value) ResizeMode = ResizeMode.PresetHeight; } }
    public bool IsCustomResizeMode { get => ResizeMode == ResizeMode.Custom; set { if (value) ResizeMode = ResizeMode.Custom; } }

    public ChoiceOption<int>? SelectedResizeHeightOption
    {
        get => PresetHeightOptions.FirstOrDefault(o => o.Value == WorkingProfile.ResizeHeight);
        set { if (value is not null) { WorkingProfile.ResizeHeight = value.Value; OnChanged(); } }
    }

    public int CustomWidth { get => WorkingProfile.CustomWidth; set { WorkingProfile.CustomWidth = Math.Max(64, value); OnChanged(); } }
    public int CustomHeight { get => WorkingProfile.CustomHeight; set { WorkingProfile.CustomHeight = Math.Max(64, value); OnChanged(); } }
    public bool KeepAspectRatio { get => WorkingProfile.KeepAspectRatio; set { WorkingProfile.KeepAspectRatio = value; OnChanged(); } }

    public bool IsOriginalFps
    {
        get => WorkingProfile.FrameRateMode == FrameRateMode.Original;
        set { if (value) { WorkingProfile.FrameRateMode = FrameRateMode.Original; OnChanged(); OnPropertyChanged(nameof(IsCustomFps)); } }
    }

    public bool IsCustomFps
    {
        get => WorkingProfile.FrameRateMode == FrameRateMode.Custom;
        set { if (value) { WorkingProfile.FrameRateMode = FrameRateMode.Custom; OnChanged(); OnPropertyChanged(nameof(IsOriginalFps)); } }
    }

    public ChoiceOption<double>? SelectedTargetFpsOption
    {
        get => TargetFpsOptions.FirstOrDefault(o => Math.Abs(o.Value - WorkingProfile.TargetFps) < 0.01);
        set { if (value is not null) { WorkingProfile.TargetFps = value.Value; OnChanged(); } }
    }

    // Audio Tab
    public ChoiceOption<string>? SelectedAudioCodecOption
    {
        get => AudioCodecOptions.FirstOrDefault(o => o.Value == WorkingProfile.AudioCodec);
        set
        {
            if (value is not null)
            {
                WorkingProfile.AudioCodec = value.Value;
                OnChanged();
                OnPropertyChanged(nameof(EstimatedTargetBitrateHint));
            }
        }
    }

    public ChoiceOption<int>? SelectedAudioBitrateOption
    {
        get => AudioBitrateOptions.FirstOrDefault(o => o.Value == WorkingProfile.AudioBitrateKbps);
        set
        {
            if (value is not null)
            {
                WorkingProfile.AudioBitrateKbps = value.Value;
                OnChanged();
                OnPropertyChanged(nameof(EstimatedTargetBitrateHint));
            }
        }
    }

    public ChoiceOption<int>? SelectedSampleRateOption
    {
        get => SampleRateOptions.FirstOrDefault(o => o.Value == WorkingProfile.SampleRate);
        set { if (value is not null) { WorkingProfile.SampleRate = value.Value; OnChanged(); } }
    }

    public ChoiceOption<int>? SelectedChannelOption
    {
        get => ChannelOptions.FirstOrDefault(o => o.Value == WorkingProfile.AudioChannels);
        set { if (value is not null) { WorkingProfile.AudioChannels = value.Value; OnChanged(); } }
    }

    public double VolumeDb { get => WorkingProfile.VolumeDb; set { WorkingProfile.VolumeDb = value; OnChanged(); OnPropertyChanged(nameof(VolumeDbLabel)); } }
    public string VolumeDbLabel => $"{VolumeDb:+0.#;-0.#;0} dB";
    public bool EnableLoudnorm { get => WorkingProfile.EnableLoudnorm; set { WorkingProfile.EnableLoudnorm = value; OnChanged(); } }

    // Filters Tab
    public bool CropEnabled { get => WorkingProfile.CropEnabled; set { WorkingProfile.CropEnabled = value; OnChanged(); } }
    public int CropLeft { get => WorkingProfile.CropLeft; set { WorkingProfile.CropLeft = Math.Max(0, value); OnChanged(); } }
    public int CropTop { get => WorkingProfile.CropTop; set { WorkingProfile.CropTop = Math.Max(0, value); OnChanged(); } }
    public int CropRight { get => WorkingProfile.CropRight; set { WorkingProfile.CropRight = Math.Max(0, value); OnChanged(); } }
    public int CropBottom { get => WorkingProfile.CropBottom; set { WorkingProfile.CropBottom = Math.Max(0, value); OnChanged(); } }

    public ChoiceOption<RotationMode>? SelectedRotationOption
    {
        get => RotationOptions.FirstOrDefault(o => o.Value == WorkingProfile.Rotation);
        set { if (value is not null) { WorkingProfile.Rotation = value.Value; OnChanged(); } }
    }

    public bool FlipHorizontal { get => WorkingProfile.FlipHorizontal; set { WorkingProfile.FlipHorizontal = value; OnChanged(); } }
    public bool FlipVertical { get => WorkingProfile.FlipVertical; set { WorkingProfile.FlipVertical = value; OnChanged(); } }

    public ChoiceOption<DeinterlaceMode>? SelectedDeinterlaceOption
    {
        get => DeinterlaceOptions.FirstOrDefault(o => o.Value == WorkingProfile.DeinterlaceMode);
        set { if (value is not null) { WorkingProfile.DeinterlaceMode = value.Value; OnChanged(); } }
    }

    public bool HdrToSdr { get => WorkingProfile.HdrToSdr; set { WorkingProfile.HdrToSdr = value; OnChanged(); } }

    public ChoiceOption<string>? SelectedTonemapOption
    {
        get => TonemapOptions.FirstOrDefault(o => o.Value == WorkingProfile.HdrTonemapAlgorithm);
        set { if (value is not null) { WorkingProfile.HdrTonemapAlgorithm = value.Value; OnChanged(); } }
    }

    public ChoiceOption<FilterStrength>? SelectedDenoiseOption
    {
        get => StrengthOptions.FirstOrDefault(o => o.Value == WorkingProfile.DenoiseStrength);
        set { if (value is not null) { WorkingProfile.DenoiseStrength = value.Value; OnChanged(); } }
    }

    public ChoiceOption<FilterStrength>? SelectedSharpenOption
    {
        get => StrengthOptions.FirstOrDefault(o => o.Value == WorkingProfile.SharpenStrength);
        set { if (value is not null) { WorkingProfile.SharpenStrength = value.Value; OnChanged(); } }
    }

    // Subtitles & Watermark Tab
    public ChoiceOption<SubtitleMode>? SelectedSubtitleModeOption
    {
        get => SubtitleModeOptions.FirstOrDefault(o => o.Value == WorkingProfile.SubtitleMode);
        set { if (value is not null) { WorkingProfile.SubtitleMode = value.Value; OnChanged(); } }
    }

    public string SubtitleFilePath { get => WorkingProfile.SubtitleFilePath ?? string.Empty; set { WorkingProfile.SubtitleFilePath = value; OnChanged(); } }
    public string SubtitleFontName { get => WorkingProfile.SubtitleFontName; set { WorkingProfile.SubtitleFontName = value; OnChanged(); } }
    public int SubtitleFontSize { get => WorkingProfile.SubtitleFontSize; set { WorkingProfile.SubtitleFontSize = Math.Clamp(value, 8, 96); OnChanged(); } }
    public double SubtitleOutline { get => WorkingProfile.SubtitleOutline; set { WorkingProfile.SubtitleOutline = Math.Clamp(value, 0, 6); OnChanged(); } }

    public bool ImageWatermarkEnabled { get => WorkingProfile.ImageWatermarkEnabled; set { WorkingProfile.ImageWatermarkEnabled = value; OnChanged(); } }
    public string ImageWatermarkPath { get => WorkingProfile.ImageWatermarkPath ?? string.Empty; set { WorkingProfile.ImageWatermarkPath = value; OnChanged(); } }

    public ChoiceOption<WatermarkPosition>? SelectedWatermarkPositionOption
    {
        get => WatermarkPositionOptions.FirstOrDefault(o => o.Value == WorkingProfile.WatermarkPosition);
        set { if (value is not null) { WorkingProfile.WatermarkPosition = value.Value; OnChanged(); } }
    }

    public int WatermarkScalePercent
    {
        get => WorkingProfile.WatermarkScalePercent;
        set { WorkingProfile.WatermarkScalePercent = value; OnChanged(); OnPropertyChanged(nameof(WatermarkScaleLabel)); }
    }
    public string WatermarkScaleLabel => $"宽度占比: {WatermarkScalePercent}%";

    public double WatermarkOpacity
    {
        get => WorkingProfile.WatermarkOpacity;
        set { WorkingProfile.WatermarkOpacity = value; OnChanged(); OnPropertyChanged(nameof(WatermarkOpacityLabel)); }
    }
    public string WatermarkOpacityLabel => $"不透明度: {WatermarkOpacity:P0}";

    public bool TextWatermarkEnabled { get => WorkingProfile.TextWatermarkEnabled; set { WorkingProfile.TextWatermarkEnabled = value; OnChanged(); } }
    public string TextWatermarkContent { get => WorkingProfile.TextWatermarkContent; set { WorkingProfile.TextWatermarkContent = value; OnChanged(); } }
    public int TextWatermarkFontSize { get => WorkingProfile.TextWatermarkFontSize; set { WorkingProfile.TextWatermarkFontSize = Math.Clamp(value, 10, 140); OnChanged(); } }

    public ChoiceOption<string>? SelectedTextWatermarkColorOption
    {
        get => WatermarkColorOptions.FirstOrDefault(o => o.Value == WorkingProfile.TextWatermarkColor);
        set { if (value is not null) { WorkingProfile.TextWatermarkColor = value.Value; OnChanged(); } }
    }

    public ChoiceOption<WatermarkPosition>? SelectedTextWatermarkPositionOption
    {
        get => WatermarkPositionOptions.FirstOrDefault(o => o.Value == WorkingProfile.TextWatermarkPosition);
        set { if (value is not null) { WorkingProfile.TextWatermarkPosition = value.Value; OnChanged(); } }
    }

    public double TextWatermarkOpacity
    {
        get => WorkingProfile.TextWatermarkOpacity;
        set { WorkingProfile.TextWatermarkOpacity = value; OnChanged(); OnPropertyChanged(nameof(TextWatermarkOpacityLabel)); }
    }
    public string TextWatermarkOpacityLabel => $"文字不透明度: {TextWatermarkOpacity:P0}";

    // Advanced Tab
    public string CustomExtraArgs { get => WorkingProfile.CustomExtraArgs; set { WorkingProfile.CustomExtraArgs = value; OnChanged(); } }
    public string CustomFfmpegPath { get => _appSettings.CustomFfmpegPath; set { _appSettings.CustomFfmpegPath = value; OnPropertyChanged(); } }
    public string CustomFfprobePath { get => _appSettings.CustomFfprobePath; set { _appSettings.CustomFfprobePath = value; OnPropertyChanged(); } }

    private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? propName = null)
    {
        OnPropertyChanged(propName);
        RefreshCliPreview();
    }

    private void RefreshCliPreview()
    {
        var defaultSample = OperatingSystem.IsWindows() ? @"C:\Videos\demo_video.mp4" : "/tmp/demo_video.mp4";
        var item = _contextItem ?? new QueueItem(defaultSample);
        var plan = FfmpegCommandBuilder.BuildJobPlan(WorkingProfile, item, isSampleMode: false);
        CliPreview = plan.ToDisplayCommandLine(_capabilities.HasFfmpeg ? _capabilities.FfmpegPath : "ffmpeg" + FfmpegLocator.ExecutableSuffix);
    }

    private void RefreshHardwareSummary()
    {
        HardwareStatusSummary = _capabilities.HasFfmpeg
            ? $"FFmpeg: {_capabilities.FfmpegPath}\n硬件加速: {_capabilities.BuildBadgeText()}"
            : "未找到 ffmpeg 可执行文件，请在下方指定路径或安装 FFmpeg 到系统 PATH。";
    }

    private void BuildOptionLists()
    {
        ContainerOptions.Add(new("MP4 (通用首选)", "mp4"));
        ContainerOptions.Add(new("MKV (多音轨/软字幕首选)", "mkv"));
        ContainerOptions.Add(new("MOV (苹果生态)", "mov"));
        ContainerOptions.Add(new("WebM (网页轻量)", "webm"));
        ContainerOptions.Add(new("AVI (传统格式)", "avi"));
        ContainerOptions.Add(new("TS (MPEG-TS 流)", "ts"));
        ContainerOptions.Add(new("GIF (动态图片)", "gif"));
        ContainerOptions.Add(new("MP3 (纯音频)", "mp3"));
        ContainerOptions.Add(new("FLAC (无损音频)", "flac"));

        HardwareDecodeOptions.Add(new("自动选择 (推荐)", "auto"));
        if (OperatingSystem.IsMacOS())
        {
            HardwareDecodeOptions.Add(new("Apple VideoToolbox", "videotoolbox"));
        }
        else if (OperatingSystem.IsLinux())
        {
            HardwareDecodeOptions.Add(new("Linux VAAPI", "vaapi"));
            HardwareDecodeOptions.Add(new("NVIDIA CUDA", "cuda"));
        }
        else
        {
            HardwareDecodeOptions.Add(new("NVIDIA CUDA", "cuda"));
            HardwareDecodeOptions.Add(new("Direct3D 11 (D3D11VA)", "d3d11va"));
        }
        HardwareDecodeOptions.Add(new("关闭 (纯 CPU 解码)", "off"));

        BuildEncoderOptions();
        RebuildPresetOptionsForCodec(WorkingProfile.VideoCodec);

        VideoProfileOptions.Add(new("自动", "auto"));
        VideoProfileOptions.Add(new("High", "high"));
        VideoProfileOptions.Add(new("Main", "main"));
        VideoProfileOptions.Add(new("Main 10 (10-bit)", "main10"));

        PixelFormatOptions.Add(new("自动", "auto"));
        PixelFormatOptions.Add(new("8-bit (yuv420p，兼容最佳)", "yuv420p"));
        PixelFormatOptions.Add(new("10-bit (yuv420p10le，防色带)", "yuv420p10le"));

        PresetHeightOptions.Add(new("2160p (4K UHD)", 2160));
        PresetHeightOptions.Add(new("1440p (2K QHD)", 1440));
        PresetHeightOptions.Add(new("1080p (Full HD)", 1080));
        PresetHeightOptions.Add(new("720p (HD 社交推荐)", 720));
        PresetHeightOptions.Add(new("480p (标清)", 480));
        PresetHeightOptions.Add(new("360p (超小预览)", 360));

        TargetFpsOptions.Add(new("60 fps", 60));
        TargetFpsOptions.Add(new("50 fps", 50));
        TargetFpsOptions.Add(new("30 fps", 30));
        TargetFpsOptions.Add(new("25 fps", 25));
        TargetFpsOptions.Add(new("24 fps", 24));
        TargetFpsOptions.Add(new("15 fps", 15));

        AudioCodecOptions.Add(new("AAC (通用首选)", "aac"));
        AudioCodecOptions.Add(new("Opus (高压缩比)", "libopus"));
        AudioCodecOptions.Add(new("MP3 (libmp3lame)", "libmp3lame"));
        AudioCodecOptions.Add(new("FLAC (无损)", "flac"));
        AudioCodecOptions.Add(new("直接复制流 (Copy)", "copy"));
        AudioCodecOptions.Add(new("静音 / 禁用音频", "none"));

        AudioBitrateOptions.Add(new("64 kbps (语音)", 64));
        AudioBitrateOptions.Add(new("96 kbps (省流)", 96));
        AudioBitrateOptions.Add(new("128 kbps (标准推荐)", 128));
        AudioBitrateOptions.Add(new("192 kbps (高品质)", 192));
        AudioBitrateOptions.Add(new("256 kbps (高保真)", 256));
        AudioBitrateOptions.Add(new("320 kbps (极高)", 320));

        SampleRateOptions.Add(new("保持原采样率", 0));
        SampleRateOptions.Add(new("48000 Hz", 48000));
        SampleRateOptions.Add(new("44100 Hz", 44100));

        ChannelOptions.Add(new("保持原声道", 0));
        ChannelOptions.Add(new("立体声 (2.0)", 2));
        ChannelOptions.Add(new("单声道 (1.0)", 1));

        RotationOptions.Add(new("不旋转", RotationMode.None));
        RotationOptions.Add(new("顺时针 90°", RotationMode.Cw90));
        RotationOptions.Add(new("逆时针 90°", RotationMode.Ccw90));
        RotationOptions.Add(new("旋转 180°", RotationMode.Rotate180));

        DeinterlaceOptions.Add(new("关闭", DeinterlaceMode.None));
        DeinterlaceOptions.Add(new("BWDIF (高质量推荐)", DeinterlaceMode.Bwdif));
        DeinterlaceOptions.Add(new("YADIF (经典)", DeinterlaceMode.Yadif));

        TonemapOptions.Add(new("Hable (电影感保留高光)", "hable"));
        TonemapOptions.Add(new("Mobius (自然均衡)", "mobius"));
        TonemapOptions.Add(new("Reinhard (明亮通透)", "reinhard"));

        StrengthOptions.Add(new("关闭", FilterStrength.None));
        StrengthOptions.Add(new("轻度", FilterStrength.Light));
        StrengthOptions.Add(new("中度", FilterStrength.Medium));
        StrengthOptions.Add(new("强力", FilterStrength.Strong));

        SubtitleModeOptions.Add(new("保留内嵌软字幕 (若容器支持)", SubtitleMode.KeepSoft));
        SubtitleModeOptions.Add(new("烧录硬字幕到画面 (需指定字幕)", SubtitleMode.BurnIn));
        SubtitleModeOptions.Add(new("移除所有字幕", SubtitleMode.Remove));

        WatermarkPositionOptions.Add(new("右下角", WatermarkPosition.BottomRight));
        WatermarkPositionOptions.Add(new("左下角", WatermarkPosition.BottomLeft));
        WatermarkPositionOptions.Add(new("右上角", WatermarkPosition.TopRight));
        WatermarkPositionOptions.Add(new("左上角", WatermarkPosition.TopLeft));
        WatermarkPositionOptions.Add(new("画面居中", WatermarkPosition.Center));

        WatermarkColorOptions.Add(new("白色 (white)", "white"));
        WatermarkColorOptions.Add(new("黄色 (yellow)", "yellow"));
        WatermarkColorOptions.Add(new("黑色 (black)", "black"));
        WatermarkColorOptions.Add(new("红色 (red)", "red"));
        WatermarkColorOptions.Add(new("青色 (cyan)", "cyan"));
    }

    private void BuildEncoderOptions()
    {
        VideoCodecOptions.Clear();
        string Tag(string codec) => _capabilities.WorkingEncoders.Contains(codec) ? "可用" : "未就绪";

        if (OperatingSystem.IsMacOS())
        {
            VideoCodecOptions.Add(new($"[Apple GPU] HEVC VideoToolbox ({Tag("hevc_videotoolbox")})", "hevc_videotoolbox"));
            VideoCodecOptions.Add(new($"[Apple GPU] H.264 VideoToolbox ({Tag("h264_videotoolbox")})", "h264_videotoolbox"));
        }
        else if (OperatingSystem.IsLinux())
        {
            VideoCodecOptions.Add(new($"[GPU NVENC] HEVC / H.265 ({Tag("hevc_nvenc")})", "hevc_nvenc"));
            VideoCodecOptions.Add(new($"[GPU NVENC] H.264 / AVC ({Tag("h264_nvenc")})", "h264_nvenc"));
            VideoCodecOptions.Add(new($"[GPU NVENC] AV1 ({Tag("av1_nvenc")})", "av1_nvenc"));
            VideoCodecOptions.Add(new($"[GPU VAAPI] HEVC ({Tag("hevc_vaapi")})", "hevc_vaapi"));
            VideoCodecOptions.Add(new($"[GPU VAAPI] H.264 ({Tag("h264_vaapi")})", "h264_vaapi"));
            VideoCodecOptions.Add(new($"[GPU VAAPI] AV1 ({Tag("av1_vaapi")})", "av1_vaapi"));
            VideoCodecOptions.Add(new($"[GPU QSV] H.264 ({Tag("h264_qsv")})", "h264_qsv"));
            VideoCodecOptions.Add(new($"[GPU QSV] HEVC ({Tag("hevc_qsv")})", "hevc_qsv"));
        }
        else
        {
            VideoCodecOptions.Add(new($"[GPU NVENC] H.264 / AVC ({Tag("h264_nvenc")})", "h264_nvenc"));
            VideoCodecOptions.Add(new($"[GPU NVENC] HEVC / H.265 ({Tag("hevc_nvenc")})", "hevc_nvenc"));
            VideoCodecOptions.Add(new($"[GPU NVENC] AV1 ({Tag("av1_nvenc")})", "av1_nvenc"));
            VideoCodecOptions.Add(new($"[GPU AMF] H.264 ({Tag("h264_amf")})", "h264_amf"));
            VideoCodecOptions.Add(new($"[GPU AMF] HEVC ({Tag("hevc_amf")})", "hevc_amf"));
            VideoCodecOptions.Add(new($"[GPU AMF] AV1 ({Tag("av1_amf")})", "av1_amf"));
            VideoCodecOptions.Add(new($"[GPU QSV] H.264 ({Tag("h264_qsv")})", "h264_qsv"));
            VideoCodecOptions.Add(new($"[GPU QSV] HEVC ({Tag("hevc_qsv")})", "hevc_qsv"));
        }

        VideoCodecOptions.Add(new("[CPU] x264 H.264 (经典高兼容)", "libx264"));
        VideoCodecOptions.Add(new("[CPU] x265 HEVC (高压缩比)", "libx265"));
        VideoCodecOptions.Add(new("[CPU] libaom AV1 (极限压缩/慢)", "libaom-av1"));
        VideoCodecOptions.Add(new("[CPU] VP9 (WebM 推荐)", "libvpx-vp9"));
        VideoCodecOptions.Add(new("[无损] 直接复制视频流 (Copy)", "copy"));
        VideoCodecOptions.Add(new("[关闭] 无视频轨 (None)", "none"));
        OnPropertyChanged(nameof(SelectedVideoCodecOption));
    }

    private void RebuildPresetOptionsForCodec(string codec)
    {
        EncoderPresetOptions.Clear();
        if (codec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
        {
            EncoderPresetOptions.Add(new("p1 (最快)", "p1"));
            EncoderPresetOptions.Add(new("p2 (极快)", "p2"));
            EncoderPresetOptions.Add(new("p3 (较快)", "p3"));
            EncoderPresetOptions.Add(new("p4 (均衡)", "p4"));
            EncoderPresetOptions.Add(new("p5 (优质推荐)", "p5"));
            EncoderPresetOptions.Add(new("p6 (慢速高质)", "p6"));
            EncoderPresetOptions.Add(new("p7 (最高质量)", "p7"));
            if (!EncoderPresetOptions.Any(o => o.Value == WorkingProfile.Preset)) WorkingProfile.Preset = "p5";
        }
        else if (codec.Contains("amf", StringComparison.OrdinalIgnoreCase))
        {
            EncoderPresetOptions.Add(new("speed (速度优先)", "speed"));
            EncoderPresetOptions.Add(new("balanced (均衡)", "balanced"));
            EncoderPresetOptions.Add(new("quality (质量优先)", "quality"));
            if (!EncoderPresetOptions.Any(o => o.Value == WorkingProfile.Preset)) WorkingProfile.Preset = "balanced";
        }
        else
        {
            EncoderPresetOptions.Add(new("ultrafast (极快)", "ultrafast"));
            EncoderPresetOptions.Add(new("superfast (超快)", "superfast"));
            EncoderPresetOptions.Add(new("veryfast (很快)", "veryfast"));
            EncoderPresetOptions.Add(new("faster (较快)", "faster"));
            EncoderPresetOptions.Add(new("fast (快)", "fast"));
            EncoderPresetOptions.Add(new("medium (均衡默认)", "medium"));
            EncoderPresetOptions.Add(new("slow (慢速高质)", "slow"));
            EncoderPresetOptions.Add(new("slower (很慢)", "slower"));
            EncoderPresetOptions.Add(new("veryslow (极慢)", "veryslow"));
            if (!EncoderPresetOptions.Any(o => o.Value == WorkingProfile.Preset)) WorkingProfile.Preset = "medium";
        }
        OnPropertyChanged(nameof(SelectedEncoderPresetOption));
    }

    private async Task BrowseSubtitleAsync()
    {
        var owner = DialogOwner ?? MainViewModel.GetMainWindow();
        if (owner is null) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择外挂字幕文件",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("字幕文件") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt"] }, FilePickerFileTypes.All]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SubtitleFilePath = path;
            WorkingProfile.SubtitleMode = SubtitleMode.BurnIn;
            OnPropertyChanged(nameof(SelectedSubtitleModeOption));
            RefreshCliPreview();
        }
    }

    private async Task BrowseImageWatermarkAsync()
    {
        var owner = DialogOwner ?? MainViewModel.GetMainWindow();
        if (owner is null) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择水印图片 (推荐透明背景 PNG)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("图片文件") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] }, FilePickerFileTypes.All]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ImageWatermarkPath = path;
            ImageWatermarkEnabled = true;
        }
    }

    private async Task BrowseExecutableAsync(bool isFfmpeg)
    {
        var owner = DialogOwner ?? MainViewModel.GetMainWindow();
        if (owner is null) return;
        var name = (isFfmpeg ? "ffmpeg" : "ffprobe") + FfmpegLocator.ExecutableSuffix;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"选择 {name}",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.All]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (isFfmpeg) CustomFfmpegPath = path;
            else CustomFfprobePath = path;
        }
    }

    private void LoadSelectedPreset()
    {
        if (SelectedPreset is null) return;
        var prevDirMode = WorkingProfile.OutputDirectoryMode;
        var prevCustomDir = WorkingProfile.CustomOutputDirectory;
        var prevTemplate = WorkingProfile.FileNameTemplate;
        var prevConflict = WorkingProfile.ConflictPolicy;

        WorkingProfile = SelectedPreset.Profile.Clone();
        WorkingProfile.OutputDirectoryMode = prevDirMode;
        WorkingProfile.CustomOutputDirectory = prevCustomDir;
        WorkingProfile.FileNameTemplate = prevTemplate;
        WorkingProfile.ConflictPolicy = prevConflict;

        RebuildPresetOptionsForCodec(WorkingProfile.VideoCodec);
        NotifyAllPropertiesChanged();
    }

    private async Task SaveNewPresetAsync()
    {
        var owner = DialogOwner ?? MainViewModel.GetMainWindow();
        if (owner is null) return;
        var prompt = new InputPromptWindow("保存为新预设", "请输入新预设名称:", $"我的压缩预设 {DateTime.Now:MMdd}");
        var ok = await prompt.ShowDialog<bool>(owner);
        if (ok && !string.IsNullOrWhiteSpace(prompt.ResponseText))
        {
            var item = new PresetItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = prompt.ResponseText.Trim(),
                Category = "自定义",
                Description = WorkingProfile.BuildSummary(),
                IsBuiltIn = false,
                Profile = WorkingProfile.Clone()
            };
            _mainPresets.Add(item);
            _presetRepo.SaveCustomPresets(_mainPresets);
            SelectedPreset = item;
        }
    }

    private async Task OverwriteSelectedPresetAsync()
    {
        if (SelectedPreset is null || SelectedPreset.IsBuiltIn) return;
        SelectedPreset.Profile = WorkingProfile.Clone();
        SelectedPreset.Description = WorkingProfile.BuildSummary();
        _presetRepo.SaveCustomPresets(_mainPresets);
        await MessageDialogWindow.ShowAsync(DialogOwner ?? MainViewModel.GetMainWindow(), "保存成功", $"已更新预设“{SelectedPreset.Name}”。");
    }

    private async Task DeleteSelectedPresetAsync()
    {
        if (SelectedPreset is null || SelectedPreset.IsBuiltIn) return;
        var confirmed = await MessageDialogWindow.ConfirmAsync(DialogOwner ?? MainViewModel.GetMainWindow(), "确认删除", $"确定要删除自定义预设“{SelectedPreset.Name}”吗？");
        if (confirmed)
        {
            _mainPresets.Remove(SelectedPreset);
            _presetRepo.SaveCustomPresets(_mainPresets);
            SelectedPreset = _mainPresets.FirstOrDefault();
        }
    }

    private async Task ImportPresetAsync()
    {
        var owner = DialogOwner ?? MainViewModel.GetMainWindow();
        if (owner is null) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "导入 ShanaPress 预设文件",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON 预设") { Patterns = ["*.json"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var item = PresetRepository.ImportPreset(path);
                if (item is not null)
                {
                    _mainPresets.Add(item);
                    _presetRepo.SaveCustomPresets(_mainPresets);
                    SelectedPreset = item;
                    WorkingProfile = item.Profile.Clone();
                    RebuildPresetOptionsForCodec(WorkingProfile.VideoCodec);
                    NotifyAllPropertiesChanged();
                }
            }
            catch (Exception ex)
            {
                await MessageDialogWindow.ShowAsync(owner, "错误", $"导入失败：{ex.Message}");
            }
        }
    }

    private async Task ExportPresetAsync()
    {
        var owner = DialogOwner ?? MainViewModel.GetMainWindow();
        if (owner is null) return;
        var target = SelectedPreset ?? new PresetItem { Name = "导出预设", Profile = WorkingProfile.Clone() };
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出预设为 JSON",
            SuggestedFileName = $"{target.Name}.json",
            DefaultExtension = "json"
        });
        var path = file?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            PresetRepository.ExportPreset(target, path);
        }
    }

    private void ResetDefaults()
    {
        var recId = PresetRepository.RecommendDefaultPresetId(_capabilities);
        var rec = _mainPresets.FirstOrDefault(p => p.Id == recId);
        WorkingProfile = rec?.Profile.Clone() ?? new EncodingProfile();
        RebuildPresetOptionsForCodec(WorkingProfile.VideoCodec);
        NotifyAllPropertiesChanged();
    }

    private void NotifyAllPropertiesChanged()
    {
        OnPropertyChanged(string.Empty);
        RefreshCliPreview();
    }
}
