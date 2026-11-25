using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ShanaPress.Core.Services;

namespace ShanaPress.Core.Models;

public sealed class QueueItem : INotifyPropertyChanged
{
    private MediaInfo? _mediaInfo;
    private string _outputPath = string.Empty;
    private long _outputSizeBytes;
    private QueueItemStatus _status = QueueItemStatus.Pending;
    private double _progressPercent;
    private int _currentPass = 1;
    private int _totalPasses = 1;
    private double _currentFps;
    private double _currentSpeed;
    private string _currentBitrate = string.Empty;
    private TimeSpan? _eta;
    private string _errorMessage = string.Empty;

    // Per-file overrides
    private bool _perFileTrimEnabled;
    private double _trimStartSeconds;
    private double _trimEndSeconds;
    private bool _perFileCropEnabled;
    private int _cropLeft;
    private int _cropTop;
    private int _cropRight;
    private int _cropBottom;
    private string? _perFileSubtitlePath;
    private SubtitleMode? _perFileSubtitleMode;

    public QueueItem(string sourcePath, long sourceSizeBytes = 0)
    {
        Id = Guid.NewGuid();
        SourcePath = sourcePath;
        FileName = Path.GetFileName(sourcePath);
        SourceSizeBytes = sourceSizeBytes;
    }

    public Guid Id { get; }
    public string SourcePath { get; }
    public string FileName { get; }
    public long SourceSizeBytes { get; set; }

    public MediaInfo? MediaInfo
    {
        get => _mediaInfo;
        set
        {
            if (SetProperty(ref _mediaInfo, value))
            {
                OnPropertyChanged(nameof(VideoSummaryText));
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        set => SetProperty(ref _outputPath, value);
    }

    public long OutputSizeBytes
    {
        get => _outputSizeBytes;
        set
        {
            if (SetProperty(ref _outputSizeBytes, value))
            {
                OnPropertyChanged(nameof(SizeComparisonText));
                OnPropertyChanged(nameof(CompressionRatioText));
                OnPropertyChanged(nameof(IsCompressionSaved));
                OnPropertyChanged(nameof(IsCompressionLarger));
            }
        }
    }

    public QueueItemStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(SpeedEtaText));
                OnPropertyChanged(nameof(SizeComparisonText));
                OnPropertyChanged(nameof(CompressionRatioText));
            }
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set
        {
            if (SetProperty(ref _progressPercent, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public int CurrentPass
    {
        get => _currentPass;
        set
        {
            if (SetProperty(ref _currentPass, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public int TotalPasses
    {
        get => _totalPasses;
        set
        {
            if (SetProperty(ref _totalPasses, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public double CurrentFps
    {
        get => _currentFps;
        set
        {
            if (SetProperty(ref _currentFps, value))
            {
                OnPropertyChanged(nameof(SpeedEtaText));
            }
        }
    }

    public double CurrentSpeed
    {
        get => _currentSpeed;
        set
        {
            if (SetProperty(ref _currentSpeed, value))
            {
                OnPropertyChanged(nameof(SpeedEtaText));
            }
        }
    }

    public string CurrentBitrate
    {
        get => _currentBitrate;
        set => SetProperty(ref _currentBitrate, value);
    }

    public TimeSpan? Eta
    {
        get => _eta;
        set
        {
            if (SetProperty(ref _eta, value))
            {
                OnPropertyChanged(nameof(SpeedEtaText));
            }
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(SpeedEtaText));
            }
        }
    }

    // Per-file override properties
    public bool PerFileTrimEnabled
    {
        get => _perFileTrimEnabled;
        set
        {
            if (SetProperty(ref _perFileTrimEnabled, value))
            {
                OnPropertyChanged(nameof(HasCustomEditsBadge));
                OnPropertyChanged(nameof(HasCustomEdits));
            }
        }
    }

    public double TrimStartSeconds
    {
        get => _trimStartSeconds;
        set => SetProperty(ref _trimStartSeconds, value);
    }

    public double TrimEndSeconds
    {
        get => _trimEndSeconds;
        set => SetProperty(ref _trimEndSeconds, value);
    }

    public bool PerFileCropEnabled
    {
        get => _perFileCropEnabled;
        set
        {
            if (SetProperty(ref _perFileCropEnabled, value))
            {
                OnPropertyChanged(nameof(HasCustomEditsBadge));
                OnPropertyChanged(nameof(HasCustomEdits));
            }
        }
    }

    public int CropLeft
    {
        get => _cropLeft;
        set => SetProperty(ref _cropLeft, value);
    }

    public int CropTop
    {
        get => _cropTop;
        set => SetProperty(ref _cropTop, value);
    }

    public int CropRight
    {
        get => _cropRight;
        set => SetProperty(ref _cropRight, value);
    }

    public int CropBottom
    {
        get => _cropBottom;
        set => SetProperty(ref _cropBottom, value);
    }

    public string? PerFileSubtitlePath
    {
        get => _perFileSubtitlePath;
        set
        {
            if (SetProperty(ref _perFileSubtitlePath, value))
            {
                OnPropertyChanged(nameof(HasCustomEditsBadge));
                OnPropertyChanged(nameof(HasCustomEdits));
            }
        }
    }

    public SubtitleMode? PerFileSubtitleMode
    {
        get => _perFileSubtitleMode;
        set
        {
            if (SetProperty(ref _perFileSubtitleMode, value))
            {
                OnPropertyChanged(nameof(HasCustomEditsBadge));
                OnPropertyChanged(nameof(HasCustomEdits));
            }
        }
    }

    public bool HasCustomEdits =>
        PerFileTrimEnabled ||
        PerFileCropEnabled ||
        !string.IsNullOrWhiteSpace(PerFileSubtitlePath) ||
        PerFileSubtitleMode.HasValue;

    public string HasCustomEditsBadge
    {
        get
        {
            var tags = new List<string>();
            if (PerFileTrimEnabled) tags.Add("片段");
            if (PerFileCropEnabled) tags.Add("裁切");
            if (!string.IsNullOrWhiteSpace(PerFileSubtitlePath) || PerFileSubtitleMode == SubtitleMode.BurnIn) tags.Add("字幕");
            return tags.Count > 0 ? $"[{string.Join("/", tags)}]" : string.Empty;
        }
    }

    public string StatusText => Status switch
    {
        QueueItemStatus.Pending => "等待中",
        QueueItemStatus.Probing => "分析中",
        QueueItemStatus.Encoding => TotalPasses > 1 ? $"编码中 P{CurrentPass}/{TotalPasses}" : "编码中",
        QueueItemStatus.Paused => "已暂停",
        QueueItemStatus.Completed => "已完成",
        QueueItemStatus.Failed => "失败",
        QueueItemStatus.Skipped => "已跳过",
        QueueItemStatus.Cancelled => "已取消",
        _ => Status.ToString()
    };

    public string ProgressText => Status switch
    {
        QueueItemStatus.Completed => "100%",
        QueueItemStatus.Encoding or QueueItemStatus.Paused =>
            TotalPasses > 1 ? $"P{CurrentPass}/{TotalPasses} {ProgressPercent:0.0}%" : $"{ProgressPercent:0.0}%",
        _ => "0%"
    };

    public string SizeComparisonText
    {
        get
        {
            var src = BitrateCalculator.FormatFileSize(SourceSizeBytes);
            if (Status == QueueItemStatus.Completed && OutputSizeBytes > 0)
            {
                return $"{src} → {BitrateCalculator.FormatFileSize(OutputSizeBytes)}";
            }
            return src;
        }
    }

    public string CompressionRatioText
    {
        get
        {
            if (Status != QueueItemStatus.Completed || OutputSizeBytes <= 0 || SourceSizeBytes <= 0)
            {
                return string.Empty;
            }
            return BitrateCalculator.FormatCompressionPercent(SourceSizeBytes, OutputSizeBytes);
        }
    }

    public bool IsCompressionSaved => Status == QueueItemStatus.Completed && OutputSizeBytes > 0 && OutputSizeBytes < SourceSizeBytes;
    public bool IsCompressionLarger => Status == QueueItemStatus.Completed && OutputSizeBytes > SourceSizeBytes;

    public string VideoSummaryText => MediaInfo?.BuildVideoSummary() ?? "待解析...";
    public string DurationText => MediaInfo is not null ? BitrateCalculator.FormatDuration(MediaInfo.Duration) : "--:--";

    public string SpeedEtaText => Status switch
    {
        QueueItemStatus.Encoding => $"{(CurrentSpeed > 0 ? $"{CurrentSpeed:0.00}x" : "--")} · {(CurrentFps > 0 ? $"{CurrentFps:0}fps" : "")} {(Eta.HasValue ? $"剩余 {BitrateCalculator.FormatEta(Eta.Value)}" : "")}".Trim(),
        QueueItemStatus.Paused => "已挂起暂停",
        QueueItemStatus.Failed => string.IsNullOrWhiteSpace(ErrorMessage) ? "编码失败" : ErrorMessage,
        QueueItemStatus.Completed => "完成",
        _ => string.Empty
    };

    public void ResetForReEncode()
    {
        Status = QueueItemStatus.Pending;
        ProgressPercent = 0;
        CurrentPass = 1;
        TotalPasses = 1;
        CurrentFps = 0;
        CurrentSpeed = 0;
        CurrentBitrate = string.Empty;
        Eta = null;
        ErrorMessage = string.Empty;
        OutputSizeBytes = 0;
    }

    public void ClearPerFileOverrides()
    {
        PerFileTrimEnabled = false;
        TrimStartSeconds = 0;
        TrimEndSeconds = 0;
        PerFileCropEnabled = false;
        CropLeft = 0;
        CropTop = 0;
        CropRight = 0;
        CropBottom = 0;
        PerFileSubtitlePath = null;
        PerFileSubtitleMode = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
