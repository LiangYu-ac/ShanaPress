using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ShanaPress.App.Infrastructure;
using ShanaPress.App.Views;
using ShanaPress.Core.Models;
using ShanaPress.Core.Services;

namespace ShanaPress.App.ViewModels;

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

    private CancellationTokenSource? _encodeCts;
    private PresetItem? _selectedPreset;
    private EncodingProfile _activeProfile;
    private HardwareCapabilities _capabilities = new();
    private QueueItem? _selectedQueueItem;
    private List<QueueItem> _selectedItems = new();

    private string _hardwareBadgeText = "正在检测硬件编码能力...";
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

        AddFilesCommand = new AsyncRelayCommand(AddFilesAsync, () => !IsEncoding);
        AddFolderCommand = new AsyncRelayCommand(AddFolderAsync, () => !IsEncoding);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => !IsEncoding && _selectedItems.Count > 0);
        ClearQueueCommand = new RelayCommand(ClearQueue, () => !IsEncoding && QueueItems.Count > 0);

        OpenQuickSettingsCommand = new RelayCommand(OpenQuickSettings, () => !IsEncoding);
        OpenTrimCropCommand = new RelayCommand(OpenTrimCrop, () => !IsEncoding && SelectedQueueItem is not null);
        RunSampleTestCommand = new AsyncRelayCommand(RunSampleTestAsync, () => !IsEncoding && SelectedQueueItem is not null);

        StartEncodeCommand = new AsyncRelayCommand(StartEncodeAsync, () => !IsEncoding && QueueItems.Any(i => i.Status is QueueItemStatus.Pending or QueueItemStatus.Failed or QueueItemStatus.Cancelled));
        PauseResumeCommand = new RelayCommand(TogglePauseResume, () => IsEncoding);
        StopEncodeCommand = new RelayCommand(StopEncode, () => IsEncoding);

        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        BrowseOutputFolderCommand = new RelayCommand(BrowseOutputFolder, () => !IsEncoding);
        OpenOutputFolderCommand = new RelayCommand(OpenCurrentOutputFolder);

        OpenSelectedSourceFolderCommand = new RelayCommand(() => PostEncodeActionHelper.RevealFileInExplorer(SelectedQueueItem?.SourcePath), () => SelectedQueueItem is not null);
        OpenSelectedOutputFolderCommand = new RelayCommand(() => PostEncodeActionHelper.RevealFileInExplorer(SelectedQueueItem?.OutputPath), () => SelectedQueueItem is not null && !string.IsNullOrWhiteSpace(SelectedQueueItem.OutputPath));
        ShowMediaInfoCommand = new RelayCommand(ShowMediaInfo, () => SelectedQueueItem?.MediaInfo is not null);
        ResetSelectedStatusCommand = new RelayCommand(ResetSelectedStatus, () => !IsEncoding && _selectedItems.Count > 0);

        CopyCliCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(LiveCliPreview)) Clipboard.SetText(LiveCliPreview);
        });
        CopyLogCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(LogText)) Clipboard.SetText(LogText);
        });
        ClearLogCommand = new RelayCommand(() =>
        {
            lock (_logLock) _logLines.Clear();
            LogText = string.Empty;
        });

        RefreshSummaries();
        _ = DetectCapabilitiesAsync();
    }

    public ObservableCollection<QueueItem> QueueItems { get; } = new();
    public ObservableCollection<PresetItem> Presets { get; } = new();

    public IReadOnlyList<int> ConcurrencyOptions { get; } = [1, 2, 3, 4];

    public IReadOnlyList<KeyValuePair<ConflictPolicy, string>> ConflictPolicyOptions { get; } =
    [
        new(ConflictPolicy.AutoRename, "自动重命名 (_1)"),
        new(ConflictPolicy.Overwrite, "覆盖同名文件"),
        new(ConflictPolicy.Skip, "跳过已存在文件")
    ];

    public IReadOnlyList<KeyValuePair<PostEncodeAction, string>> PostEncodeActionOptions { get; } =
    [
        new(PostEncodeAction.None, "完成后无操作"),
        new(PostEncodeAction.OpenOutputFolder, "打开输出文件夹"),
        new(PostEncodeAction.PlayBeep, "播放完成提示音"),
        new(PostEncodeAction.ShutdownPc, "倒计时后自动关机")
    ];

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ClearQueueCommand { get; }
    public ICommand OpenQuickSettingsCommand { get; }
    public ICommand OpenTrimCropCommand { get; }
    public ICommand RunSampleTestCommand { get; }
    public ICommand StartEncodeCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand StopEncodeCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand BrowseOutputFolderCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand OpenSelectedSourceFolderCommand { get; }
    public ICommand OpenSelectedOutputFolderCommand { get; }
    public ICommand ShowMediaInfoCommand { get; }
    public ICommand ResetSelectedStatusCommand { get; }
    public ICommand CopyCliCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand ClearLogCommand { get; }

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
                // Keep output folder/template preferences when switching encoding preset
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
                RelayCommand.RaiseCanExecuteChanged();
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
        RelayCommand.RaiseCanExecuteChanged();
    }

    public string HardwareBadgeText
    {
        get => _hardwareBadgeText;
        private set => SetProperty(ref _hardwareBadgeText, value);
    }

    public bool HasWorkingHwEncoder
    {
        get => _hasWorkingHwEncoder;
        private set => SetProperty(ref _hasWorkingHwEncoder, value);
    }

    public string ActiveSummaryText
    {
        get => _activeSummaryText;
        private set => SetProperty(ref _activeSummaryText, value);
    }

    public string LiveCliPreview
    {
        get => _liveCliPreview;
        private set => SetProperty(ref _liveCliPreview, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public bool IsBottomPanelExpanded
    {
        get => _isBottomPanelExpanded;
        set
        {
            if (SetProperty(ref _isBottomPanelExpanded, value))
            {
                _settings.IsBottomPanelExpanded = value;
            }
        }
    }

    public int BottomPanelTabIndex
    {
        get => _bottomPanelTabIndex;
        set => SetProperty(ref _bottomPanelTabIndex, value);
    }

    public bool IsEncoding
    {
        get => _isEncoding;
        private set
        {
            if (SetProperty(ref _isEncoding, value))
            {
                OnPropertyChanged(nameof(CanEditSettings));
                RelayCommand.RaiseCanExecuteChanged();
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

    public double OverallProgressPercent
    {
        get => _overallProgressPercent;
        private set => SetProperty(ref _overallProgressPercent, value);
    }

    public string OverallStatusText
    {
        get => _overallStatusText;
        private set => SetProperty(ref _overallStatusText, value);
    }

    public string SpaceSavedText
    {
        get => _spaceSavedText;
        private set => SetProperty(ref _spaceSavedText, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                _settings.IsDarkTheme = value;
                ThemeManager.ApplyTheme(value);
                OnPropertyChanged(nameof(ThemeButtonText));
            }
        }
    }

    public string ThemeButtonText => IsDarkTheme ? "浅色模式" : "深色模式";

    // Output bar bound properties
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

    public ConflictPolicy ConflictPolicy
    {
        get => _activeProfile.ConflictPolicy;
        set
        {
            if (_activeProfile.ConflictPolicy != value)
            {
                _activeProfile.ConflictPolicy = value;
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

    public PostEncodeAction PostEncodeAction
    {
        get => _settings.PostEncodeAction;
        set
        {
            if (_settings.PostEncodeAction != value)
            {
                _settings.PostEncodeAction = value;
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
        OnPropertyChanged(nameof(ConflictPolicy));
    }

    public async Task DetectCapabilitiesAsync()
    {
        HardwareBadgeText = "正在检测 FFmpeg 与 GPU 硬件加速...";
        _capabilities = await HardwareDetector.DetectAsync(_settings.CustomFfmpegPath, _settings.CustomFfprobePath);
        HasWorkingHwEncoder = _capabilities.HasAnyNvenc || _capabilities.HasAnyAmf || _capabilities.HasAnyQsv;
        HardwareBadgeText = _capabilities.BuildBadgeText();

        // If current active profile uses a GPU codec that doesn't work on this machine, fall back cleanly on first run
        if (_capabilities.HasFfmpeg &&
            !_activeProfile.IsVideoDisabled &&
            !_activeProfile.IsVideoCopy &&
            !_activeProfile.IsGifContainer &&
            (_activeProfile.VideoCodec.Contains("nvenc") || _activeProfile.VideoCodec.Contains("amf") || _activeProfile.VideoCodec.Contains("qsv")) &&
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
                catch
                {
                    // Skip inaccessible folders
                }
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
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (item.Status == QueueItemStatus.Pending) item.Status = QueueItemStatus.Probing;
                });

                var info = await _probeService.ProbeAsync(_capabilities.FfprobePath, item.SourcePath);

                await Application.Current.Dispatcher.InvokeAsync(() =>
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
                await Application.Current.Dispatcher.InvokeAsync(() =>
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
        var dlg = new OpenFileDialog
        {
            Title = "选择要压缩的视频 / 音频文件",
            Multiselect = true,
            Filter = "媒体文件|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.flv;*.webm;*.ts;*.m2ts;*.mts;*.m4v;*.mpg;*.mpeg;*.3gp;*.mp3;*.wav;*.flac;*.m4a|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            await AddPathsAsync(dlg.FileNames);
        }
    }

    private async Task AddFolderAsync()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择包含视频文件的文件夹（将自动递归扫描）"
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            await AddPathsAsync([dlg.FolderName]);
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
    }

    private void ClearQueue()
    {
        QueueItems.Clear();
        _selectedItems.Clear();
        SelectedQueueItem = null;
        UpdateOverallStats();
        RefreshSummaries();
    }

    private void ResetSelectedStatus()
    {
        foreach (var item in _selectedItems)
        {
            item.ResetForReEncode();
        }
        UpdateOverallStats();
        RelayCommand.RaiseCanExecuteChanged();
    }

    private void OpenQuickSettings()
    {
        var vm = new QuickSettingsViewModel(
            _activeProfile,
            _capabilities,
            _presetRepo,
            Presets,
            SelectedQueueItem,
            _settings,
            async () => await DetectCapabilitiesAsync());

        var win = new QuickSettingsWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = vm
        };

        if (win.ShowDialog() == true)
        {
            ActiveProfile = vm.WorkingProfile.Clone();
            SaveSettings();
        }
    }

    private void OpenTrimCrop()
    {
        if (SelectedQueueItem is null) return;
        var vm = new TrimCropViewModel(SelectedQueueItem, _activeProfile, _capabilities, _probeService);
        var win = new TrimCropWindow
        {
            Owner = Application.Current.MainWindow,
            DataContext = vm
        };
        if (win.ShowDialog() == true)
        {
            RefreshSummaries();
        }
    }

    private async Task RunSampleTestAsync()
    {
        if (SelectedQueueItem is null) return;
        if (!_capabilities.HasFfmpeg)
        {
            MessageBox.Show("未检测到 ffmpeg.exe，请先在快速设置中配置路径。", "ShanaPress", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                OverallProgressPercent = p.Percent;
                OverallStatusText = $"试压中 ({item.FileName}): {p.Percent:0.0}% · {p.Speed:0.00}x";
            });

            var ok = await runner.RunJobAsync(_capabilities.FfmpegPath, plan, progress, AppendLog, CancellationToken.None);
            sw.Stop();

            if (!ok || !File.Exists(plan.OutputPath))
            {
                MessageBox.Show("试压失败，请展开底部日志查看 FFmpeg 输出。", "试压失败", MessageBoxButton.OK, MessageBoxImage.Error);
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

            var sampleWin = new SamplePreviewWindow(result, OpenQuickSettings)
            {
                Owner = Application.Current.MainWindow
            };
            sampleWin.ShowDialog();
        }
        catch (Exception ex)
        {
            AppendLog($"[试压异常] {ex.Message}");
            MessageBox.Show($"试压过程中出错：{ex.Message}", "试压失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            OverallProgressPercent = 0;
            UpdateOverallStats();
        }
    }

    private async Task StartEncodeAsync()
    {
        if (!_capabilities.HasFfmpeg)
        {
            MessageBox.Show("未检测到 ffmpeg.exe，请在快速设置中指定路径。", "ShanaPress", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targets = QueueItems
            .Where(i => i.Status is QueueItemStatus.Pending or QueueItemStatus.Failed or QueueItemStatus.Cancelled)
            .ToList();

        if (targets.Count == 0)
        {
            MessageBox.Show("队列中没有待处理的任务。如需重新编码已完成的文件，请右键选择“重置状态为等待”。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        foreach (var t in targets)
        {
            t.ResetForReEncode();
        }

        _encodeCts = new CancellationTokenSource();
        IsEncoding = true;
        IsPaused = false;
        SaveSettings();

        var ct = _encodeCts.Token;
        using var gate = new SemaphoreSlim(Math.Clamp(MaxConcurrency, 1, 4));
        var completedOk = 0;
        var failedCount = 0;
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
                        await Application.Current.Dispatcher.InvokeAsync(() => item.Status = QueueItemStatus.Probing);
                        try
                        {
                            var info = await _probeService.ProbeAsync(_capabilities.FfprobePath, item.SourcePath, ct);
                            await Application.Current.Dispatcher.InvokeAsync(() => item.MediaInfo = info);
                        }
                        catch { }
                    }

                    var plan = FfmpegCommandBuilder.BuildJobPlan(_activeProfile, item, isSampleMode: false);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        item.OutputPath = plan.OutputPath;
                        item.TotalPasses = plan.IsTwoPass ? 2 : 1;
                    });

                    if (plan.ShouldSkip)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            item.Status = QueueItemStatus.Skipped;
                            item.ProgressPercent = 100;
                            UpdateOverallStats();
                        });
                        AppendLog($"[跳过已存在] {plan.OutputPath}");
                        return;
                    }

                    lastOutputDir = Path.GetDirectoryName(plan.OutputPath);

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        item.Status = QueueItemStatus.Encoding;
                        UpdateOverallStats();
                    });

                    AppendLog($"[启动编码] {item.FileName} -> {Path.GetFileName(plan.OutputPath)}");

                    var progress = new Progress<EncodingProgress>(p =>
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

                    var success = await runner.RunJobAsync(_capabilities.FfmpegPath, plan, progress, line =>
                    {
                        AppendLog($"[{item.FileName}] {line}");
                    }, ct);

                    if (success && File.Exists(plan.OutputPath))
                    {
                        var outLen = new FileInfo(plan.OutputPath).Length;
                        await Application.Current.Dispatcher.InvokeAsync(() =>
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
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        item.Status = QueueItemStatus.Cancelled;
                        UpdateOverallStats();
                    });
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedCount);
                    await Application.Current.Dispatcher.InvokeAsync(() =>
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
        }

        if (!cancelled && PostEncodeAction != PostEncodeAction.None && completedOk > 0)
        {
            var folder = lastOutputDir ?? (_activeProfile.OutputDirectoryMode == OutputDirectoryMode.CustomFolder
                ? _activeProfile.CustomOutputDirectory
                : Path.GetDirectoryName(targets[0].SourcePath));

            PostEncodeActionHelper.Execute(PostEncodeAction, folder, Application.Current.MainWindow);
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

    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    private void BrowseOutputFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择输出文件夹"
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            CustomOutputDirectory = dlg.FolderName;
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

        PostEncodeActionHelper.OpenFolder(dir);
    }

    private void ShowMediaInfo()
    {
        if (SelectedQueueItem?.MediaInfo is null) return;
        var win = new MediaInfoWindow(SelectedQueueItem.MediaInfo)
        {
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
    }

    public void RefreshSummaries()
    {
        ActiveSummaryText = _activeProfile.BuildSummary();

        var sampleItem = SelectedQueueItem ?? QueueItems.FirstOrDefault() ?? new QueueItem(@"C:\Videos\sample_input.mp4");
        var plan = FfmpegCommandBuilder.BuildJobPlan(_activeProfile, sampleItem, isSampleMode: false);
        LiveCliPreview = plan.ToDisplayCommandLine(_capabilities.HasFfmpeg ? _capabilities.FfmpegPath : "ffmpeg.exe");
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

        Application.Current.Dispatcher.BeginInvoke(() => LogText = snapshot);
    }

    public void SaveSettings()
    {
        _settings.LastProfile = _activeProfile.Clone();
        _settingsService.Save(_settings);
    }

    private static bool IsSupportedOrExplicitFile(string path) =>
        SupportedMediaExtensions.Contains(Path.GetExtension(path)) || File.Exists(path);
}
