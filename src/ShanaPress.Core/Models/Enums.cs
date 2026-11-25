namespace ShanaPress.Core.Models;

public enum RateControlMode
{
    CrfOrCq,
    AverageBitrate,
    TwoPassBitrate,
    TargetFileSize
}

public enum ResizeMode
{
    Original,
    PresetHeight,
    Custom
}

public enum FrameRateMode
{
    Original,
    Custom
}

public enum RotationMode
{
    None,
    Cw90,
    Ccw90,
    Rotate180
}

public enum DeinterlaceMode
{
    None,
    Bwdif,
    Yadif
}

public enum FilterStrength
{
    None,
    Light,
    Medium,
    Strong
}

public enum SubtitleMode
{
    KeepSoft,
    BurnIn,
    Remove
}

public enum WatermarkPosition
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
    Center
}

public enum OutputDirectoryMode
{
    SameAsSource,
    CustomFolder
}

public enum ConflictPolicy
{
    AutoRename,
    Overwrite,
    Skip
}

public enum QueueItemStatus
{
    Pending,
    Probing,
    Encoding,
    Paused,
    Completed,
    Failed,
    Skipped,
    Cancelled
}

public enum PostEncodeAction
{
    None,
    OpenOutputFolder,
    PlayBeep,
    ShutdownPc
}
