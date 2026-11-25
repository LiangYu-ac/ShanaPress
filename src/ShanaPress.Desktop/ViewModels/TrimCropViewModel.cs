using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShanaPress.Core.Models;
using ShanaPress.Core.Services;
using ShanaPress.Desktop.Infrastructure;

namespace ShanaPress.Desktop.ViewModels;

public sealed class TrimCropViewModel : ObservableObject
{
    private readonly QueueItem _item;
    private readonly EncodingProfile _globalProfile;
    private readonly HardwareCapabilities _capabilities;
    private readonly MediaProbeService _probeService;

    private CancellationTokenSource? _frameDebounceCts;
    private Bitmap? _previewImage;
    private string _previewStatusText = "正在提取预览帧...";
    private double _currentPositionSeconds;

    private bool _trimEnabled;
    private double _trimStartSeconds;
    private double _trimEndSeconds;

    private bool _cropEnabled;
    private int _cropLeft;
    private int _cropTop;
    private int _cropRight;
    private int _cropBottom;

    private SubtitleMode _subtitleMode;
    private string _subtitleFilePath = string.Empty;

    public TrimCropViewModel(
        QueueItem item,
        EncodingProfile globalProfile,
        HardwareCapabilities capabilities,
        MediaProbeService probeService)
    {
        _item = item;
        _globalProfile = globalProfile;
        _capabilities = capabilities;
        _probeService = probeService;

        SourceWidth = item.MediaInfo?.VideoStream?.Width ?? 1920;
        SourceHeight = item.MediaInfo?.VideoStream?.Height ?? 1080;
        TotalDurationSeconds = Math.Max(1.0, item.MediaInfo?.Duration.TotalSeconds ?? 60.0);

        _trimEnabled = item.PerFileTrimEnabled || globalProfile.TrimEnabled;
        _trimStartSeconds = item.PerFileTrimEnabled ? item.TrimStartSeconds : globalProfile.TrimStartSeconds;
        _trimEndSeconds = item.PerFileTrimEnabled && item.TrimEndSeconds > 0
            ? item.TrimEndSeconds
            : (globalProfile.TrimEndSeconds > 0 ? globalProfile.TrimEndSeconds : TotalDurationSeconds);

        _cropEnabled = item.PerFileCropEnabled || globalProfile.CropEnabled;
        _cropLeft = item.PerFileCropEnabled ? item.CropLeft : globalProfile.CropLeft;
        _cropTop = item.PerFileCropEnabled ? item.CropTop : globalProfile.CropTop;
        _cropRight = item.PerFileCropEnabled ? item.CropRight : globalProfile.CropRight;
        _cropBottom = item.PerFileCropEnabled ? item.CropBottom : globalProfile.CropBottom;

        _subtitleMode = item.PerFileSubtitleMode ?? globalProfile.SubtitleMode;
        _subtitleFilePath = item.PerFileSubtitlePath ?? globalProfile.SubtitleFilePath ?? string.Empty;

        _currentPositionSeconds = Math.Clamp(_trimStartSeconds > 0 ? _trimStartSeconds : TotalDurationSeconds * 0.15, 0, TotalDurationSeconds);

        StepBackward5Command = new RelayCommand(() => SeekBy(-5));
        StepBackward1Command = new RelayCommand(() => SeekBy(-1));
        StepForward1Command = new RelayCommand(() => SeekBy(1));
        StepForward5Command = new RelayCommand(() => SeekBy(5));
        SetStartHereCommand = new RelayCommand(() =>
        {
            TrimEnabled = true;
            TrimStartSeconds = CurrentPositionSeconds;
            if (TrimEndSeconds <= TrimStartSeconds) TrimEndSeconds = TotalDurationSeconds;
        });
        SetEndHereCommand = new RelayCommand(() =>
        {
            TrimEnabled = true;
            TrimEndSeconds = CurrentPositionSeconds;
            if (TrimEndSeconds <= TrimStartSeconds) TrimStartSeconds = 0;
        });
        ResetTrimCommand = new RelayCommand(() =>
        {
            TrimEnabled = false;
            TrimStartSeconds = 0;
            TrimEndSeconds = TotalDurationSeconds;
        });
        ResetCropCommand = new RelayCommand(() =>
        {
            CropEnabled = false;
            CropLeft = CropTop = CropRight = CropBottom = 0;
        });
        AutoDetectCropCommand = new AsyncRelayCommand(AutoDetectCropAsync, () => _capabilities.HasFfmpeg);
        BrowseSubtitleCommand = new AsyncRelayCommand(BrowseSubtitleAsync);
        ClearSubtitleCommand = new RelayCommand(() =>
        {
            SubtitleFilePath = string.Empty;
            SelectedSubtitleModeOption = SubtitleModeOptions.FirstOrDefault(o => o.Value == _globalProfile.SubtitleMode);
        });
        ClearOverridesCommand = new RelayCommand(ClearOverrides);

        ScheduleFrameRefresh();
    }

    public event Action? CropVisualChanged;

    public Window? DialogOwner { get; set; }
    public string WindowTitle => $"区间剪辑与画面裁切 — {_item.FileName}";
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public double TotalDurationSeconds { get; }

    public RelayCommand StepBackward5Command { get; }
    public RelayCommand StepBackward1Command { get; }
    public RelayCommand StepForward1Command { get; }
    public RelayCommand StepForward5Command { get; }
    public RelayCommand SetStartHereCommand { get; }
    public RelayCommand SetEndHereCommand { get; }
    public RelayCommand ResetTrimCommand { get; }
    public RelayCommand ResetCropCommand { get; }
    public AsyncRelayCommand AutoDetectCropCommand { get; }
    public AsyncRelayCommand BrowseSubtitleCommand { get; }
    public RelayCommand ClearSubtitleCommand { get; }
    public RelayCommand ClearOverridesCommand { get; }

    public IReadOnlyList<ChoiceOption<SubtitleMode>> SubtitleModeOptions { get; } =
    [
        new("保留软字幕", SubtitleMode.KeepSoft),
        new("烧录硬字幕", SubtitleMode.BurnIn),
        new("移除字幕", SubtitleMode.Remove)
    ];

    public ChoiceOption<SubtitleMode>? SelectedSubtitleModeOption
    {
        get => SubtitleModeOptions.FirstOrDefault(o => o.Value == _subtitleMode);
        set
        {
            if (value is not null && _subtitleMode != value.Value)
            {
                _subtitleMode = value.Value;
                OnPropertyChanged();
            }
        }
    }

    public Bitmap? PreviewImage { get => _previewImage; private set => SetProperty(ref _previewImage, value); }
    public string PreviewStatusText { get => _previewStatusText; private set => SetProperty(ref _previewStatusText, value); }

    public double CurrentPositionSeconds
    {
        get => _currentPositionSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0, TotalDurationSeconds);
            if (SetProperty(ref _currentPositionSeconds, clamped))
            {
                OnPropertyChanged(nameof(CurrentPositionText));
                ScheduleFrameRefresh();
            }
        }
    }

    public string CurrentPositionText => BitrateCalculator.FormatDurationPrecise(TimeSpan.FromSeconds(CurrentPositionSeconds));

    public bool TrimEnabled
    {
        get => _trimEnabled;
        set { if (SetProperty(ref _trimEnabled, value)) OnPropertyChanged(nameof(TrimSummaryText)); }
    }

    public double TrimStartSeconds
    {
        get => _trimStartSeconds;
        set
        {
            var v = Math.Clamp(value, 0, TotalDurationSeconds);
            if (SetProperty(ref _trimStartSeconds, v))
            {
                OnPropertyChanged(nameof(TrimStartText));
                OnPropertyChanged(nameof(TrimSummaryText));
            }
        }
    }

    public double TrimEndSeconds
    {
        get => _trimEndSeconds;
        set
        {
            var v = Math.Clamp(value, 0, TotalDurationSeconds);
            if (SetProperty(ref _trimEndSeconds, v))
            {
                OnPropertyChanged(nameof(TrimEndText));
                OnPropertyChanged(nameof(TrimSummaryText));
            }
        }
    }

    public string TrimStartText
    {
        get => BitrateCalculator.FormatDurationPrecise(TimeSpan.FromSeconds(TrimStartSeconds));
        set { if (BitrateCalculator.TryParseTimestamp(value, out var sec)) TrimStartSeconds = sec; }
    }

    public string TrimEndText
    {
        get => BitrateCalculator.FormatDurationPrecise(TimeSpan.FromSeconds(TrimEndSeconds));
        set { if (BitrateCalculator.TryParseTimestamp(value, out var sec)) TrimEndSeconds = sec; }
    }

    public string TrimSummaryText
    {
        get
        {
            if (!TrimEnabled) return $"全片完整导出 ({BitrateCalculator.FormatDuration(TimeSpan.FromSeconds(TotalDurationSeconds))})";
            var dur = Math.Max(0, TrimEndSeconds - TrimStartSeconds);
            return $"截取片段时长: {BitrateCalculator.FormatDurationPrecise(TimeSpan.FromSeconds(dur))}";
        }
    }

    public bool CropEnabled
    {
        get => _cropEnabled;
        set
        {
            if (SetProperty(ref _cropEnabled, value))
            {
                OnPropertyChanged(nameof(CropResultDimensionText));
                CropVisualChanged?.Invoke();
            }
        }
    }

    public int CropLeft
    {
        get => _cropLeft;
        set { if (SetProperty(ref _cropLeft, Math.Clamp(value, 0, Math.Max(0, SourceWidth - 16)))) { OnPropertyChanged(nameof(CropResultDimensionText)); CropVisualChanged?.Invoke(); } }
    }

    public int CropTop
    {
        get => _cropTop;
        set { if (SetProperty(ref _cropTop, Math.Clamp(value, 0, Math.Max(0, SourceHeight - 16)))) { OnPropertyChanged(nameof(CropResultDimensionText)); CropVisualChanged?.Invoke(); } }
    }

    public int CropRight
    {
        get => _cropRight;
        set { if (SetProperty(ref _cropRight, Math.Clamp(value, 0, Math.Max(0, SourceWidth - 16)))) { OnPropertyChanged(nameof(CropResultDimensionText)); CropVisualChanged?.Invoke(); } }
    }

    public int CropBottom
    {
        get => _cropBottom;
        set { if (SetProperty(ref _cropBottom, Math.Clamp(value, 0, Math.Max(0, SourceHeight - 16)))) { OnPropertyChanged(nameof(CropResultDimensionText)); CropVisualChanged?.Invoke(); } }
    }

    public string CropResultDimensionText
    {
        get
        {
            var outW = Math.Max(2, SourceWidth - CropLeft - CropRight);
            var outH = Math.Max(2, SourceHeight - CropTop - CropBottom);
            outW -= outW % 2;
            outH -= outH % 2;
            return CropEnabled
                ? $"裁切后输出画面: {outW} × {outH} (原 {SourceWidth} × {SourceHeight})"
                : $"原画面尺寸: {SourceWidth} × {SourceHeight}";
        }
    }

    public string SubtitleFilePath { get => _subtitleFilePath; set => SetProperty(ref _subtitleFilePath, value); }

    private void SeekBy(double deltaSeconds) =>
        CurrentPositionSeconds = Math.Clamp(CurrentPositionSeconds + deltaSeconds, 0, TotalDurationSeconds);

    private void ScheduleFrameRefresh()
    {
        if (!_capabilities.HasFfmpeg)
        {
            PreviewStatusText = "未检测到 ffmpeg，无法提取预览帧";
            return;
        }

        _frameDebounceCts?.Cancel();
        _frameDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _frameDebounceCts = cts;

        _ = RefreshFrameAsync(CurrentPositionSeconds, cts.Token);
    }

    private async Task RefreshFrameAsync(double sec, CancellationToken ct)
    {
        try
        {
            await Task.Delay(120, ct);
            await Dispatcher.UIThread.InvokeAsync(() =>
                PreviewStatusText = $"正在提取 {BitrateCalculator.FormatDurationPrecise(TimeSpan.FromSeconds(sec))} 画面...");

            var pngBytes = await _probeService.ExtractFramePngAsync(
                _capabilities.FfmpegPath, _item.SourcePath, TimeSpan.FromSeconds(sec), 900, ct);

            if (ct.IsCancellationRequested || pngBytes.Length == 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() => PreviewStatusText = "该时间点暂无画面帧");
                return;
            }

            using var ms = new MemoryStream(pngBytes);
            var bmp = new Bitmap(ms);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var old = PreviewImage;
                PreviewImage = bmp;
                old?.Dispose();
                PreviewStatusText = $"预览时间点 {BitrateCalculator.FormatDurationPrecise(TimeSpan.FromSeconds(sec))} · {SourceWidth}×{SourceHeight}";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => PreviewStatusText = $"提取失败: {ex.Message}");
        }
    }

    private async Task AutoDetectCropAsync()
    {
        PreviewStatusText = "正在自动检测黑边...";
        var detected = await _probeService.DetectCropAsync(
            _capabilities.FfmpegPath,
            _item.SourcePath,
            TimeSpan.FromSeconds(CurrentPositionSeconds),
            SourceWidth,
            SourceHeight);

        if (detected is null)
        {
            PreviewStatusText = "未能检测到黑边区域";
            return;
        }

        var (l, t, r, b) = detected.Value;
        CropLeft = l;
        CropTop = t;
        CropRight = r;
        CropBottom = b;
        CropEnabled = l > 0 || t > 0 || r > 0 || b > 0;
        PreviewStatusText = CropEnabled
            ? $"黑边检测完成: 左 {l} · 上 {t} · 右 {r} · 下 {b}"
            : "画面无明显黑边";
    }

    private async Task BrowseSubtitleAsync()
    {
        var owner = DialogOwner ?? MainViewModel.GetMainWindow();
        if (owner is null) return;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "为当前视频指定外挂字幕",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("字幕文件") { Patterns = ["*.srt", "*.ass", "*.ssa", "*.vtt"] }, FilePickerFileTypes.All]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            SubtitleFilePath = path;
            SelectedSubtitleModeOption = SubtitleModeOptions.FirstOrDefault(o => o.Value == SubtitleMode.BurnIn);
        }
    }

    private void ClearOverrides()
    {
        _item.ClearPerFileOverrides();
        TrimEnabled = _globalProfile.TrimEnabled;
        TrimStartSeconds = _globalProfile.TrimStartSeconds;
        TrimEndSeconds = _globalProfile.TrimEndSeconds > 0 ? _globalProfile.TrimEndSeconds : TotalDurationSeconds;
        CropEnabled = _globalProfile.CropEnabled;
        CropLeft = _globalProfile.CropLeft;
        CropTop = _globalProfile.CropTop;
        CropRight = _globalProfile.CropRight;
        CropBottom = _globalProfile.CropBottom;
        SelectedSubtitleModeOption = SubtitleModeOptions.FirstOrDefault(o => o.Value == _globalProfile.SubtitleMode);
        SubtitleFilePath = _globalProfile.SubtitleFilePath ?? string.Empty;
    }

    public void ApplyToItem()
    {
        _item.PerFileTrimEnabled = TrimEnabled;
        _item.TrimStartSeconds = TrimStartSeconds;
        _item.TrimEndSeconds = TrimEndSeconds;

        _item.PerFileCropEnabled = CropEnabled;
        _item.CropLeft = CropLeft;
        _item.CropTop = CropTop;
        _item.CropRight = CropRight;
        _item.CropBottom = CropBottom;

        _item.PerFileSubtitlePath = string.IsNullOrWhiteSpace(SubtitleFilePath) ? null : SubtitleFilePath;
        _item.PerFileSubtitleMode = !string.IsNullOrWhiteSpace(SubtitleFilePath) || _subtitleMode != _globalProfile.SubtitleMode
            ? _subtitleMode
            : null;
    }
}
