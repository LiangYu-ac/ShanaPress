using System.Diagnostics;
using System.Globalization;
using ShanaPress.Core.Models;

namespace ShanaPress.Core.Services;

public sealed class ProgressParser
{
    private readonly double _totalDurationSeconds;
    private readonly int _currentPass;
    private readonly int _totalPasses;
    private readonly Stopwatch _activeTimer = new();

    private TimeSpan _outTime;
    private double _fps;
    private double _speed;
    private string _bitrate = string.Empty;
    private long _totalSizeBytes;

    public ProgressParser(double totalDurationSeconds, int currentPass = 1, int totalPasses = 1)
    {
        _totalDurationSeconds = Math.Max(0.1, totalDurationSeconds);
        _currentPass = Math.Max(1, currentPass);
        _totalPasses = Math.Max(1, totalPasses);
        _activeTimer.Start();
    }

    public void PauseTimer() => _activeTimer.Stop();
    public void ResumeTimer() => _activeTimer.Start();

    public EncodingProgress? FeedLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var eq = line.IndexOf('=');
        if (eq <= 0) return null;

        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();

        switch (key)
        {
            case "out_time_us":
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                {
                    _outTime = TimeSpan.FromTicks(us * 10);
                }
                break;

            case "out_time_ms":
                // Note: ffmpeg -progress out_time_ms is actually microseconds in many builds unless out_time_us already set
                if (_outTime == TimeSpan.Zero &&
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var msOrUs) && msOrUs >= 0)
                {
                    _outTime = TimeSpan.FromTicks(msOrUs * 10);
                }
                break;

            case "fps":
                double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _fps);
                break;

            case "speed":
                if (value.EndsWith("x", StringComparison.OrdinalIgnoreCase))
                {
                    double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out _speed);
                }
                break;

            case "bitrate":
                _bitrate = value == "N/A" ? string.Empty : value;
                break;

            case "total_size":
                long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _totalSizeBytes);
                break;

            case "progress":
                var isEnd = value.Equals("end", StringComparison.OrdinalIgnoreCase);
                var rawRatio = isEnd ? 1.0 : Math.Clamp(_outTime.TotalSeconds / _totalDurationSeconds, 0.0, 1.0);

                double totalPercent;
                if (_totalPasses == 2)
                {
                    totalPercent = _currentPass == 1
                        ? rawRatio * 45.0
                        : 45.0 + rawRatio * 55.0;
                }
                else
                {
                    totalPercent = rawRatio * 100.0;
                }

                TimeSpan? eta = null;
                if (!isEnd && rawRatio > 0.01 && _activeTimer.Elapsed.TotalSeconds > 0.3)
                {
                    if (_speed > 0.01)
                    {
                        var remainingVideoSec = Math.Max(0, _totalDurationSeconds - _outTime.TotalSeconds);
                        var etaPassSec = remainingVideoSec / _speed;
                        if (_totalPasses == 2 && _currentPass == 1)
                        {
                            etaPassSec += _totalDurationSeconds / _speed;
                        }
                        eta = TimeSpan.FromSeconds(etaPassSec);
                    }
                    else
                    {
                        var elapsed = _activeTimer.Elapsed.TotalSeconds;
                        var totalEstimated = elapsed / rawRatio;
                        eta = TimeSpan.FromSeconds(Math.Max(0, totalEstimated - elapsed));
                    }
                }

                return new EncodingProgress
                {
                    Percent = Math.Clamp(totalPercent, 0, 100),
                    CurrentPass = _currentPass,
                    TotalPasses = _totalPasses,
                    OutTime = _outTime,
                    Fps = _fps,
                    Speed = _speed,
                    Bitrate = _bitrate,
                    TotalSizeBytes = _totalSizeBytes,
                    Eta = eta,
                    IsEnd = isEnd
                };
        }

        return null;
    }
}
