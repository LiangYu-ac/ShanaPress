using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ShanaPress.Core.Models;
using ShanaPress.Core.Services;
using ShanaPress.Desktop.Infrastructure;
using ShanaPress.Desktop.Views;

namespace ShanaPress.Desktop.ViewModels;

public sealed record ChoiceOption<T>(string Display, T Value);

public sealed class MainViewModel : ObservableObject
{
    private static readonly HashSet<string> SupportedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".webm", ".ts", ".m2ts", ".mts",
        ".m4v", ".mpg", ".mpeg", ".vob", ".3gp", ".rmvb", ".asf", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg"
    };

    private readonly AppSettingsService _settingsService;
    private readonly PresetRepository _presetRepo;
    private readonly MediaProbeService _probeService = new();
    private readonly AppSettings _settings;

    private readonly object _runnerLock = new();
    private readonly List<FfmpegProcessRunner> _activeRunners = new();
    private readonly List<string> _logLines = new();
    private readonly object _logLock = new();
    private readonly List<IRaisableCommand> _commands = new();

    private CancellationTokenSource? _encodeCts;
    private PresetItem? _selectedPreset;
    private EncodingProfile _activeProfile;
    private HardwareCapabilities _capabilities = new();
    private QueueItem? _selectedQueueItem;
    private List<QueueItem> _selectedItems = new();

    private string _hardwareBadgeText = "正在检测 FFmpeg 与硬件编码能力...";
    private bool _hasWorkingHwEncoder;
    private string _activeSummaryText = string.Empty;
    private string _liveCliPreview = string.Empty;
    private string _logText = string.Empty;
    private bool _isBottomPanelExpanded;
    private int _bottomPanelTabIndex;

    private bool _isEncoding;
    private bool _isPaused;
    private double _overallProgressPercent;
    private string _overallStatusText = "就绪 — 请拖入视频文件或点击“添加文件”";
    private string _spaceSavedText = string.Empty;
    private bool _isDarkTheme = true;

    public MainViewModel(AppSettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _settings = settings;
        _presetRepo = new PresetRepository();

        _isDarkTheme = settings.IsDarkTheme;
        _isBottomPanelExpanded = settings.IsBottomPanelExpanded;

        foreach (var preset in _presetRepo.LoadAll())
        {
            Presets.Add(preset);
        }

        _selectedPreset = Presets.FirstOrDefault(p => p.Id == settings.SelectedPresetId) ?? Presets.FirstOrDefault();
        _activeProfile = settings.LastProfile?.Clone() ?? _selectedPreset?.Profile.Clone() ?? new EncodingProfile();

        AddFilesCommand = Reg(new AsyncRelayCommand(AddFilesAsync, () => !IsEncoding));
        AddFolderCommand = Reg(new AsyncRelayCommand(AddFolderAsync, () => !IsEncoding));
        RemoveSelectedCommand = Reg(new RelayCommand(RemoveSelected, () => !IsEncoding && _selectedItems.Count > 0));
        ClearQueueCommand = Reg(new RelayCommand(ClearQueue, () => !IsEncoding && QueueItems.Count > 0));

        OpenQuickSettingsCommand = Reg(new AsyncRelayCommand(OpenQuickSettingsAsync, () => !IsEncoding));
        OpenTrimCropCommand = Reg(new AsyncRelayCommand(OpenTrimCropAsync, () => !IsEncoding && SelectedQueueItem is not null));
        RunSampleTestCommand = Reg(new AsyncRelayCommand(RunSampleTestAsync, () => !IsEncoding && SelectedQueueItem is not null));

        StartEncodeCommand = Reg(new AsyncRelayCommand(StartEncodeAsync, () => !IsEncoding && QueueItems.Any(i => i.Status is QueueItemStatus.Pending or QueueItemStatus.Failed or QueueItemStatus.Cancelled)));
        PauseResumeCommand = Reg(new RelayCommand(TogglePauseResume, () => IsEncoding));
        StopEncodeCommand = Reg(new RelayCommand(StopEncode, () => IsEncoding));

        ToggleThemeCommand = Reg(new RelayCommand(() => IsDarkTheme = !IsDarkTheme));
        BrowseOutputFolderCommand = Reg(new AsyncRelayCommand(BrowseOutputFolderAsync, () => !IsEncoding));
        OpenOutputFolderCommand = Reg(new RelayCommand(OpenCurrentOutputFolder));

        OpenSelectedSourceFolderCommand = Reg(new RelayCommand(() => PlatformShellHelper.RevealFile(SelectedQueueItem?.SourcePath), () => SelectedQueueItem is not null));
        OpenSelectedOutputFolderCommand = Reg(new RelayCommand(() => PlatformShellHelper.RevealFile(SelectedQueueItem?.OutputPath), () => SelectedQueueItem is not null && !string.IsNullOrWhiteSpace(SelectedQueueItem.OutputPath)));
        ShowMediaInfoCommand = Reg(new AsyncRelayCommand(ShowMediaInfoAsync, () => SelectedQueueItem?.MediaInfo is not null));
        ResetSelectedStatusCommand = Reg(new RelayCommand(ResetSelectedStatus, () => !IsEncoding && _selectedItems.Count > 0));

        CopyCliCommand = Reg(new AsyncRelayCommand(() => CopyToClipboardAsync(LiveCliPreview)));
        CopyLogCommand = Reg(new AsyncRelayCommand(() => CopyToClipboardAsync(LogText)));
        ClearLogCommand = Reg(new RelayCommand(() =>
        {
            lock (_logLock) _logLines.Clear();
            LogText = string.Empty;
        }));

        RefreshSummaries();
        _ = DetectCapabilitiesAsync();
    }

    private T Reg<T>(T cmd) where T : IRaisableCommand
    {
        _commands.Add(cmd);
        return cmd;
    }

    private void RaiseAllCanExecute()
    {
        foreach (var cmd in _commands) cmd.RaiseCanExecuteChanged();
    }

    public ObservableCollection<QueueItem> QueueItems { get; } = new();
    public ObservableCollection<PresetItem> Presets { get; } = new();

    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4];

    public IReadOnlyList<ChoiceOption<ConflictPolicy>> ConflictPolicyOptions { get; } =
    [
        new("自动重命名 (_1)", ConflictPolicy.AutoRename),
        new("覆盖同名文件", ConflictPolicy.Overwrite),
        new("跳过已存在文件", ConflictPolicy.Skip)
    ];

    public IReadOnlyList<ChoiceOption<PostEncodeAction>> PostEncodeActionOptions { get; } =
    [
        new("完成后无操作", PostEncodeAction.None),
        new("打开输出文件夹", PostEncodeAction.OpenOutputFolder),
        new("播放完成提示音", PostEncodeAction.PlayBeep),
        new("倒计时后自动关机", PostEncodeAction.ShutdownPc)
    ];

    public AsyncRelayCommand AddFilesCommand { get; }
    public AsyncRelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public AsyncRelayCommand OpenQuickSettingsCommand { get; }
    public AsyncRelayCommand OpenTrimCropCommand { get; }
    public AsyncRelayCommand RunSampleTestCommand { get; }
    public AsyncRelayCommand StartEncodeCommand { get; }
    public RelayCommand PauseResumeCommand { get; }
    public RelayCommand StopEncodeCommand { get; }
    public RelayCommand ToggleThemeCommand { get; }
    public AsyncRelayCommand BrowseOutputFolderCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand OpenSelectedSourceFolderCommand { get; }
    public RelayCommand OpenSelectedOutputFolderCommand { get; }
    public AsyncRelayCommand ShowMediaInfoCommand { get; }
    public RelayCommand ResetSelectedStatusCommand { get; }
    public AsyncRelayCommand CopyCliCommand { get; }
    public AsyncRelayCommand CopyLogCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public HardwareCapabilities Capabilities => _capabilities;

    public EncodingProfile ActiveProfile
    {
        get => _activeProfile;
        set
        {
            _activeProfile = value;
            OnPropertyChanged();
            SyncProfileBoundProperties();
            RefreshSummaries();
        }
    }

    public PresetItem? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value) && value is not null)
            {
                var prevDirMode = _activeProfile.OutputDirectoryMode;
                var prevCustomDir = _activeProfile.CustomOutputDirectory;
                var prevTemplate = _activeProfile.FileNameTemplate;
                var prevConflict = _activeProfile.ConflictPolicy;

                var cloned = value.Profile.Clone();
                cloned.OutputDirectoryMode = prevDirMode;
                cloned.CustomOutputDirectory = prevCustomDir;
                cloned.FileNameTemplate = prevTemplate;
                cloned.ConflictPolicy = prevConflict;

                _settings.SelectedPresetId = value.Id;
                ActiveProfile = cloned;
            }
        }
    }

    public QueueItem? SelectedQueueItem
    {
        get => _selectedQueueItem;
        set
        {
            if (SetProperty(ref _selectedQueueItem, value))
            {
                RefreshSummaries();
                RaiseAllCanExecute();
            }
        }
    }

    public void UpdateSelectedItems(IEnumerable<QueueItem> selected)
    {
        _selectedItems = selected.ToList();
        if (_selectedItems.Count > 0 && (_selectedQueueItem is null || !_selectedItems.Contains(_selectedQueueItem)))
        {
            SelectedQueueItem = _selectedItems[0];
        }
        RaiseAllCanExecute();
    }

    public string HardwareBadgeText { get => _hardwareBadgeText; private set => SetProperty(ref _hardwareBadgeText, value); }
    public bool HasWorkingHwEncoder { get => _hasWorkingHwEncoder; private set => SetProperty(ref _hasWorkingHwEncoder, value); }
    public string ActiveSummaryText { get => _activeSummaryText; private set => SetProperty(ref _activeSummaryText, value); }
    public string LiveCliPreview { get => _liveCliPreview; private set => SetProperty(ref _liveCliPreview, value); }
    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }

    public bool IsBottomPanelExpanded
    {
        get => _isBottomPanelExpanded;
        set { if (SetProperty(ref _isBottomPanelExpanded, value)) _settings.IsBottomPanelExpanded = value; }
    }

    public int BottomPanelTabIndex { get => _bottomPanelTabIndex; set => SetProperty(ref _bottomPanelTabIndex, value); }

    public bool IsEncoding
    {
        get => _isEncoding;
        private set
        {
            if (SetProperty(ref _isEncoding, value))
            {
                OnPropertyChanged(nameof(CanEditSettings));
                RaiseAllCanExecute();
            }
        }
    }

    public bool CanEditSettings => !IsEncoding;

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseResumeLabel));
            }
        }
    }

    public string PauseResumeLabel => IsPaused ? "继续编码" : "暂停编码";
    public double OverallProgressPercent { get => _overallProgressPercent; private set => SetProperty(ref _overallProgressPercent, value); }
    public string OverallStatusText { get => _overallStatusText; private set => SetProperty(ref _overallStatusText, value); }
    public string SpaceSavedText { get => _spaceSavedText; private set => SetProperty(ref _spaceSavedText, value); }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                _settings.IsDarkTheme = value;
                if (Application.Current is { } app)
                {
                    app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
                }
                OnPropertyChanged(nameof(ThemeButtonText));
            }
        }
    }

    public string ThemeButtonText => IsDarkTheme ? "浅色模式" : "深色模式";

    public bool IsSameAsSource
    {
        get => _activeProfile.OutputDirectoryMode == OutputDirectoryMode.SameAsSource;
        set
        {
            if (value)
            {
                _activeProfile.OutputDirectoryMode = OutputDirectoryMode.SameAsSource;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsCustomFolder));
                RefreshSummaries();
            }
        }
    }

    public bool IsCustomFolder
    {
        get => _activeProfile.OutputDirectoryMode == OutputDirectoryMode.CustomFolder;
        set
        {
            if (value)
            {
                _activeProfile.OutputDirectoryMode = OutputDirectoryMode.CustomFolder;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSameAsSource));
                RefreshSummaries();
            }
        }
    }

    public string CustomOutputDirectory
    {
        get => _activeProfile.CustomOutputDirectory;
        set
        {
            if (_activeProfile.CustomOutputDirectory != value)
            {
                _activeProfile.CustomOutputDirectory = value;
                OnPropertyChanged();
                RefreshSummaries();
            }
        }
    }

    public string FileNameTemplate
    {
        get => _activeProfile.FileNameTemplate;
        set
        {
            if (_activeProfile.FileNameTemplate != value)
            {
                _activeProfile.FileNameTemplate = value;
                OnPropertyChanged();
                RefreshSummaries();
            }
        }
    }

    public ChoiceOption<ConflictPolicy> SelectedConflictPolicyOption
    {
        get => ConflictPolicyOptions.FirstOrDefault(o => o.Value == _activeProfile.ConflictPolicy) ?? ConflictPolicyOptions[0];
        set
        {
            if (value is not null && _activeProfile.ConflictPolicy != value.Value)
            {
                _activeProfile.ConflictPolicy = value.Value;
                OnPropertyChanged();
                RefreshSummaries();
            }
        }
    }

    public int MaxConcurrency
    {
        get => _settings.MaxConcurrency;
        set
        {
            var clamped = Math.Clamp(value, 1, 4);
            if (_settings.MaxConcurrency != clamped)
            {
                _settings.MaxConcurrency = clamped;
                OnPropertyChanged();
            }
        }
    }

    public ChoiceOption<PostEncodeAction> SelectedPostEncodeOption
    {
        get => PostEncodeActionOptions.FirstOrDefault(o => o.Value == _settings.PostEncodeAction) ?? PostEncodeActionOptions[0];
        set
        {
            if (value is not null && _settings.PostEncodeAction != value.Value)
            {
                _settings.PostEncodeAction = value.Value;
                OnPropertyChanged();
            }
        }
    }

    private void SyncProfileBoundProperties()
    {
        OnPropertyChanged(nameof(IsSameAsSource));
        OnPropertyChanged(nameof(IsCustomFolder));
        OnPropertyChanged(nameof(CustomOutputDirectory));
        OnPropertyChanged(nameof(FileNameTemplate));
        OnPropertyChanged(nameof(SelectedConflictPolicyOption));
    }

    public async Task DetectCapabilitiesAsync()
    {
        HardwareBadgeText = "正在检测 FFmpeg 与硬件加速能力...";
        _capabilities = await HardwareDetector.DetectAsync(_settings.CustomFfmpegPath, _settings.CustomFfprobePath);
        HasWorkingHwEncoder = _capabilities.HasAnyHardwareEncoder;
        HardwareBadgeText = _capabilities.BuildBadgeText();

        if (_capabilities.HasFfmpeg &&
            !_activeProfile.IsVideoDisabled &&
            !_activeProfile.IsVideoCopy &&
            !_activeProfile.IsGifContainer &&
            (_activeProfile.VideoCodec.Contains("nvenc") ||
             _activeProfile.VideoCodec.Contains("amf") ||
             _activeProfile.VideoCodec.Contains("qsv") ||
             _activeProfile.VideoCodec.Contains("videotoolbox") ||
             _activeProfile.VideoCodec.Contains("vaapi")) &&
            !_capabilities.WorkingEncoders.Contains(_activeProfile.VideoCodec))
        {
            var fallbackId = PresetRepository.RecommendDefaultPresetId(_capabilities);
            var fallbackPreset = Presets.FirstOrDefault(p => p.Id == fallbackId);
            if (fallbackPreset is not null)
            {
                SelectedPreset = fallbackPreset;
            }
        }

        RefreshSummaries();
    }

    public async Task AddPathsAsync(IEnumerable<string> paths)
    {
        var filesToAdd = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                if (IsSupportedOrExplicitFile(p))
                {
                    filesToAdd.Add(Path.GetFullPath(p));
                }
            }
            else if (Directory.Exists(p))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories))
                    {
                        if (SupportedMediaExtensions.Contains(Path.GetExtension(f)))
                        {
                            filesToAdd.Add(Path.GetFullPath(f));
                        }
                    }
                }
                catch { }
            }
        }

        var existingPaths = new HashSet<string>(QueueItems.Select(q => q.SourcePath), StringComparer.OrdinalIgnoreCase);
        var addedItems = new List<QueueItem>();

        foreach (var file in filesToAdd.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existingPaths.Contains(file)) continue;
            existingPaths.Add(file);

            var size = File.Exists(file) ? new FileInfo(file).Length : 0;
            var item = new QueueItem(file, size);
            QueueItems.Add(item);
            addedItems.Add(item);
        }

        if (SelectedQueueItem is null && QueueItems.Count > 0)
        {
            SelectedQueueItem = QueueItems[0];
        }

        UpdateOverallStats();
        RefreshSummaries();
        RaiseAllCanExecute();

        if (addedItems.Count > 0 && _capabilities.HasFfprobe)
        {
            await ProbeItemsAsync(addedItems);
        }
    }

    private async Task ProbeItemsAsync(List<QueueItem> items)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (item.Status == QueueItemStatus.Pending) item.Status = QueueItemStatus.Probing;
                });

                var info = await _probeService.ProbeAsync(_capabilities.FfprobePath, item.SourcePath);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.MediaInfo = info;
                    if (item.SourceSizeBytes <= 0 && info.FileSizeBytes > 0)
                    {
                        item.SourceSizeBytes = info.FileSizeBytes;
                    }
                    if (item.Status == QueueItemStatus.Probing)
                    {
                        item.Status = QueueItemStatus.Pending;
                    }
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (item.Status == QueueItemStatus.Probing) item.Status = QueueItemStatus.Pending;
                    AppendLog($"[ffprobe] {item.FileName}: {ex.Message}");
                });
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        RefreshSummaries();
    }

    private async Task AddFilesAsync()
    {
        var win = GetMainWindow();
        if (win is null) return;
        var files = await win.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要压缩的视频 / 音频文件",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("媒体文件")
                {
                    Patterns = ["*.mp4","*.mkv","*.mov","*.avi","*.wmv","*.flv","*.webm","*.ts","*.m2ts","*.mts","*.m4v","*.mpg","*.mpeg","*.3gp","*.mp3","*.wav","*.flac","*.m4a"]
                },
                FilePickerFileTypes.All
            ]
        });

        var localPaths = files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (localPaths.Count > 0)
        {
            await AddPathsAsync(localPaths);
        }
    }

    private async Task AddFolderAsync()
    {
        var win = GetMainWindow();
        if (win is null) return;
        var folders = await win.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择包含视频文件的文件夹（将自动递归扫描）",
            AllowMultiple = false
        });

        var localPaths = folders.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
        if (localPaths.Count > 0)
        {
            await AddPathsAsync(localPaths);
        }
    }

    private void RemoveSelected()
    {
        foreach (var item in _selectedItems.ToList())
        {
            QueueItems.Remove(item);
        }
        _selectedItems.Clear();
        if (SelectedQueueItem is not null && !QueueItems.Contains(SelectedQueueItem))
        {
            SelectedQueueItem = QueueItems.FirstOrDefault();
        }
        UpdateOverallStats();
        RefreshSummaries();
        RaiseAllCanExecute();
    }

    private void ClearQueue()
    {
        QueueItems.Clear();
        _selectedItems.Clear();
        SelectedQueueItem = null;
        UpdateOverallStats();
        RefreshSummaries();
        RaiseAllCanExecute();
    }

    private void ResetSelectedStatus()
    {
        foreach (var item in _selectedItems)
        {
            item.ResetForReEncode();
        }
        UpdateOverallStats();
        RaiseAllCanExecute();
    }

    private async Task OpenQuickSettingsAsync()
    {
        var owner = GetMainWindow();
        if (owner is null) return;

        var vm = new QuickSettingsViewModel(
            _activeProfile,
            _capabilities,
            _presetRepo,
            Presets,
            SelectedQueueItem,
            _settings,
            DetectCapabilitiesAsync);

        var win = new QuickSettingsWindow { DataContext = vm };
        var result = await win.ShowDialog<bool>(owner);
        if (result)
        {
            ActiveProfile = vm.WorkingProfile.Clone();
            SaveSettings();
        }
    }

    private async Task OpenTrimCropAsync()
    {
        var owner = GetMainWindow();
        if (owner is null || SelectedQueueItem is null) return;

        var vm = new TrimCropViewModel(SelectedQueueItem, _activeProfile, _capabilities, _probeService);
        var win = new TrimCropWindow { DataContext = vm };
        var result = await win.ShowDialog<bool>(owner);
        if (result)
        {
            RefreshSummaries();
        }
    }

    private async Task RunSampleTestAsync()
    {
        var owner = GetMainWindow();
        if (SelectedQueueItem is null) return;
        if (!_capabilities.HasFfmpeg)
        {
            await MessageDialogWindow.ShowAsync(owner, "ShanaPress", "未检测到 ffmpeg，请先安装 FFmpeg 或在快速设置中配置路径。");
            return;
        }

        var item = SelectedQueueItem;
        if (item.MediaInfo is null && _capabilities.HasFfprobe)
        {
            try
            {
                item.MediaInfo = await _probeService.ProbeAsync(_capabilities.FfprobePath, item.SourcePath);
            }
            catch { }
        }

        var plan = FfmpegCommandBuilder.BuildJobPlan(_activeProfile, item, isSampleMode: true);
        var runner = new FfmpegProcessRunner();
        var sw = Stopwatch.StartNew();

        OverallStatusText = $"正在对 {item.FileName} 执行 10 秒快速试压预估...";
        AppendLog($"=== 开始 10 秒试压: {item.FileName} ===");
        AppendLog(plan.ToDisplayCommandLine(_capabilities.FfmpegPath));

        try
        {
            var progress = new Progress<EncodingProgress>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    OverallProgressPercent = p.Percent;
                    OverallStatusText = $"试压中 ({item.FileName}): {p.Percent:0.0}% · {p.Speed:0.00}x";
                });
            });

            var ok = await runner.RunJobAsync(_capabilities.FfmpegPath, plan, progress, AppendLog, CancellationToken.None);
            sw.Stop();

            if (!ok || !File.Exists(plan.OutputPath))
            {
                await MessageDialogWindow.ShowAsync(owner, "试压失败", "试压失败，请展开底部日志查看 FFmpeg 输出。");
                return;
            }

            var sampleBytes = new FileInfo(plan.OutputPath).Length;
            var fullPlan = FfmpegCommandBuilder.BuildJobPlan(_activeProfile, item, isSampleMode: false);
            var estimatedFullBytes = BitrateCalculator.EstimateFullSizeBytes(
                sampleBytes,
                plan.EffectiveDurationSeconds,
                fullPlan.EffectiveDurationSeconds);

            var result = new SampleTestResult
            {
                SourceFileName = item.FileName,
                SampleOutputPath = plan.OutputPath,
                SourceSizeBytes = item.SourceSizeBytes,
                SampleSizeBytes = sampleBytes,
                SampleDurationSeconds = plan.EffectiveDurationSeconds,
                FullEffectiveDurationSeconds = fullPlan.EffectiveDurationSeconds,
                EstimatedFullSizeBytes = estimatedFullBytes,
                ElapsedEncodeTime = sw.Elapsed,
                ProfileSummary = _activeProfile.BuildSummary()
            };

            if (owner is not null)
            {
                var sampleWin = new SamplePreviewWindow(result, () => _ = OpenQuickSettingsAsync());
                await sampleWin.ShowDialog(owner);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"[试压异常] {ex.Message}");
            await MessageDialogWindow.ShowAsync(owner, "试压失败", $"试压过程中出错：{ex.Message}");
        }
        finally
        {
            OverallProgressPercent = 0;
            UpdateOverallStats();
        }
    }

    private async Task StartEncodeAsync()
    {
        var owner = GetMainWindow();
        if (!_capabilities.HasFfmpeg)
        {
            await MessageDialogWindow.ShowAsync(owner, "ShanaPress", "未检测到 ffmpeg，请在快速设置中指定路径。");
            return;
        }

        var targets = QueueItems
            .Where(i => i.Status is QueueItemStatus.Pending or QueueItemStatus.Failed or QueueItemStatus.Cancelled)
            .ToList();

        if (targets.Count == 0)
        {
            await MessageDialogWindow.ShowAsync(owner, "提示", "队列中没有待处理的任务。如需重新编码已完成的文件，请右键选择“重置选中状态为等待”。");
            return;
        }

        foreach (var t in targets) t.ResetForReEncode();

        _encodeCts = new CancellationTokenSource();
        IsEncoding = true;
        IsPaused = false;
        SaveSettings();

        var ct = _encodeCts.Token;
        using var gate = new SemaphoreSlim(Math.Clamp(MaxConcurrency, 1, 4));
        var completedOk = 0;
        var cancelled = false;
        string? lastOutputDir = null;

        AppendLog($"=== 开始批量编码: {targets.Count} 个任务 (并发数: {MaxConcurrency}) ===");
        AppendLog($"当前配置: {_activeProfile.BuildSummary()}");

        try
        {
            var tasks = targets.Select(async item =>
            {
                await gate.WaitAsync(ct);
                var runner = new FfmpegProcessRunner();
                lock (_runnerLock) _activeRunners.Add(runner);

                try
                {
                    ct.ThrowIfCancellationRequested();

                    if (item.MediaInfo is null && _capabilities.HasFfprobe)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => item.Status = QueueItemStatus.Probing);
                        try
                        {
                            var info = await _probeService.ProbeAsync(_capabilities.FfprobePath, item.SourcePath, ct);
                            await Dispatcher.UIThread.InvokeAsync(() => item.MediaInfo = info);
                        }
                        catch { }
                    }

                    var plan = FfmpegCommandBuilder.BuildJobPlan(_activeProfile, item, isSampleMode: false);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.OutputPath = plan.OutputPath;
                        item.TotalPasses = plan.IsTwoPass ? 2 : 1;
                    });

                    if (plan.ShouldSkip)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            item.Status = QueueItemStatus.Skipped;
                            item.ProgressPercent = 100;
                            UpdateOverallStats();
                        });
                        AppendLog($"[跳过已存在] {plan.OutputPath}");
                        return;
                    }

                    lastOutputDir = Path.GetDirectoryName(plan.OutputPath);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Status = QueueItemStatus.Encoding;
                        UpdateOverallStats();
                    });

                    AppendLog($"[启动编码] {item.FileName} -> {Path.GetFileName(plan.OutputPath)}");

                    var progress = new Progress<EncodingProgress>(p =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            item.ProgressPercent = p.Percent;
                            item.CurrentPass = p.CurrentPass;
                            item.TotalPasses = p.TotalPasses;
                            item.CurrentFps = p.Fps;
                            item.CurrentSpeed = p.Speed;
                            item.CurrentBitrate = p.Bitrate;
                            item.Eta = p.Eta;
                            UpdateOverallStats();
                        });
                    });

                    var success = await runner.RunJobAsync(_capabilities.FfmpegPath, plan, progress, line =>
                    {
                        AppendLog($"[{item.FileName}] {line}");
                    }, ct);

                    if (success && File.Exists(plan.OutputPath))
                    {
                        var outLen = new FileInfo(plan.OutputPath).Length;
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            item.OutputSizeBytes = outLen;
                            item.ProgressPercent = 100;
                            item.Status = QueueItemStatus.Completed;
                            UpdateOverallStats();
                        });
                        Interlocked.Increment(ref completedOk);
                        AppendLog($"[完成] {item.FileName} ({BitrateCalculator.FormatFileSize(item.SourceSizeBytes)} -> {BitrateCalculator.FormatFileSize(outLen)}, {item.CompressionRatioText})");
                    }
                    else
                    {
                        throw new InvalidOperationException("未生成输出文件");
                    }
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Status = QueueItemStatus.Cancelled;
                        UpdateOverallStats();
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.Status = QueueItemStatus.Failed;
                        item.ErrorMessage = ex.Message;
                        UpdateOverallStats();
                    });
                    AppendLog($"[失败] {item.FileName}: {ex.Message}");
                }
                finally
                {
                    lock (_runnerLock) _activeRunners.Remove(runner);
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            IsEncoding = false;
            IsPaused = false;
            _encodeCts?.Dispose();
            _encodeCts = null;
            UpdateOverallStats();
            RaiseAllCanExecute();
        }

        if (!cancelled && _settings.PostEncodeAction != PostEncodeAction.None && completedOk > 0)
        {
            var folder = lastOutputDir ?? (_activeProfile.OutputDirectoryMode == OutputDirectoryMode.CustomFolder
                ? _activeProfile.CustomOutputDirectory
                : Path.GetDirectoryName(targets[0].SourcePath));

            await PlatformShellHelper.ExecutePostEncodeAsync(_settings.PostEncodeAction, folder, owner);
        }
    }

    private void TogglePauseResume()
    {
        lock (_runnerLock)
        {
            if (_activeRunners.Count == 0) return;

            if (IsPaused)
            {
                foreach (var r in _activeRunners) r.Resume();
                IsPaused = false;
                foreach (var item in QueueItems.Where(q => q.Status == QueueItemStatus.Paused))
                {
                    item.Status = QueueItemStatus.Encoding;
                }
                AppendLog("[用户操作] 已恢复编码");
            }
            else
            {
                foreach (var r in _activeRunners) r.Pause();
                IsPaused = true;
                foreach (var item in QueueItems.Where(q => q.Status == QueueItemStatus.Encoding))
                {
                    item.Status = QueueItemStatus.Paused;
                }
                AppendLog("[用户操作] 已暂停编码");
            }
        }
        UpdateOverallStats();
    }

    private void StopEncode()
    {
        AppendLog("[用户操作] 正在停止编码队列...");
        _encodeCts?.Cancel();
    }

    private async Task BrowseOutputFolderAsync()
    {
        var win = GetMainWindow();
        if (win is null) return;
        var folders = await win.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择输出文件夹",
            AllowMultiple = false
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            CustomOutputDirectory = path;
            IsCustomFolder = true;
        }
    }

    private void OpenCurrentOutputFolder()
    {
        string? dir = null;
        if (_activeProfile.OutputDirectoryMode == OutputDirectoryMode.CustomFolder &&
            !string.IsNullOrWhiteSpace(_activeProfile.CustomOutputDirectory))
        {
            dir = _activeProfile.CustomOutputDirectory;
        }
        else if (SelectedQueueItem is not null)
        {
            dir = Path.GetDirectoryName(SelectedQueueItem.SourcePath);
        }
        else if (QueueItems.Count > 0)
        {
            dir = Path.GetDirectoryName(QueueItems[0].SourcePath);
        }

        PlatformShellHelper.OpenFolder(dir);
    }

    private async Task ShowMediaInfoAsync()
    {
        var owner = GetMainWindow();
        if (owner is null || SelectedQueueItem?.MediaInfo is null) return;
        var win = new MediaInfoWindow(SelectedQueueItem.MediaInfo);
        await win.ShowDialog(owner);
    }

    public void RefreshSummaries()
    {
        ActiveSummaryText = _activeProfile.BuildSummary();
        var defaultSample = OperatingSystem.IsWindows() ? @"C:\Videos\sample_input.mp4" : "/tmp/sample_input.mp4";
        var sampleItem = SelectedQueueItem ?? QueueItems.FirstOrDefault() ?? new QueueItem(defaultSample);
        var plan = FfmpegCommandBuilder.BuildJobPlan(_activeProfile, sampleItem, isSampleMode: false);
        LiveCliPreview = plan.ToDisplayCommandLine(_capabilities.HasFfmpeg ? _capabilities.FfmpegPath : "ffmpeg" + FfmpegLocator.ExecutableSuffix);
    }

    private void UpdateOverallStats()
    {
        if (QueueItems.Count == 0)
        {
            OverallProgressPercent = 0;
            OverallStatusText = "就绪 — 请拖入视频文件或文件夹，点击“快速设置 (F6)”调整压缩参数";
            SpaceSavedText = string.Empty;
            return;
        }

        var done = QueueItems.Count(q => q.Status == QueueItemStatus.Completed);
        var failed = QueueItems.Count(q => q.Status == QueueItemStatus.Failed);
        var encoding = QueueItems.Count(q => q.Status is QueueItemStatus.Encoding or QueueItemStatus.Paused);

        var totalProgress = QueueItems.Sum(q => q.Status is QueueItemStatus.Completed or QueueItemStatus.Skipped ? 100.0 : q.ProgressPercent) / QueueItems.Count;
        OverallProgressPercent = Math.Clamp(totalProgress, 0, 100);

        if (IsEncoding)
        {
            OverallStatusText = IsPaused
                ? $"已暂停 ({done}/{QueueItems.Count} 已完成)"
                : $"正在编码 ({encoding} 个活动任务 · 已完成 {done}/{QueueItems.Count}) · 总进度 {OverallProgressPercent:0.0}%";
        }
        else
        {
            OverallStatusText = failed > 0
                ? $"队列共 {QueueItems.Count} 个文件 · 已完成 {done} · 失败 {failed}"
                : $"队列共 {QueueItems.Count} 个文件 · 已完成 {done}";
        }

        var completedItems = QueueItems.Where(q => q.Status == QueueItemStatus.Completed && q.OutputSizeBytes > 0 && q.SourceSizeBytes > 0).ToList();
        if (completedItems.Count > 0)
        {
            var srcSum = completedItems.Sum(q => q.SourceSizeBytes);
            var outSum = completedItems.Sum(q => q.OutputSizeBytes);
            var saved = srcSum - outSum;
            var pct = BitrateCalculator.FormatCompressionPercent(srcSum, outSum);
            SpaceSavedText = saved >= 0
                ? $"总计节省 {BitrateCalculator.FormatFileSize(saved)} ({pct})"
                : $"总计增加 {BitrateCalculator.FormatFileSize(-saved)} ({pct})";
        }
        else
        {
            SpaceSavedText = string.Empty;
        }
    }

    private void AppendLog(string line)
    {
        string snapshot;
        lock (_logLock)
        {
            _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_logLines.Count > 600)
            {
                _logLines.RemoveRange(0, _logLines.Count - 600);
            }
            snapshot = string.Join(Environment.NewLine, _logLines);
        }

        Dispatcher.UIThread.Post(() => LogText = snapshot);
    }

    public void SaveSettings()
    {
        _settings.LastProfile = _activeProfile.Clone();
        _settingsService.Save(_settings);
    }

    private static async Task CopyToClipboardAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var top = GetMainWindow();
        if (top?.Clipboard is not null)
        {
            await top.Clipboard.SetTextAsync(text);
        }
    }

    public static Window? GetMainWindow() =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    private static bool IsSupportedOrExplicitFile(string path) =>
        SupportedMediaExtensions.Contains(Path.GetExtension(path)) || File.Exists(path);
}
