using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ShanaPress.Core.Models;

namespace ShanaPress.Core.Services;

public sealed class FfmpegProcessRunner
{
    private static readonly ConcurrentDictionary<int, Process> ActiveProcesses = new();

    private Process? _currentProcess;
    private ProgressParser? _currentParser;
    private readonly object _procLock = new();
    private volatile bool _isPaused;

    public bool IsPaused => _isPaused;

    public async Task<bool> RunJobAsync(
        string ffmpegPath,
        FfmpegJobPlan plan,
        IProgress<EncodingProgress>? progress,
        Action<string>? onLogLine,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            throw new FileNotFoundException("找不到 ffmpeg 可执行文件", ffmpegPath);
        }

        var outDir = Path.GetDirectoryName(plan.OutputPath);
        if (!string.IsNullOrWhiteSpace(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        try
        {
            if (plan.IsTwoPass)
            {
                onLogLine?.Invoke($"[Pass 1/2] 开始分析视频码率分布: {Path.GetFileName(plan.SourcePath)}");
                var p1Ok = await RunSingleProcessAsync(
                    ffmpegPath, plan.Pass1Arguments, plan.EffectiveDurationSeconds, 1, 2, progress, onLogLine, ct);
                if (!p1Ok)
                {
                    DeletePartialFile(plan.OutputPath);
                    return false;
                }

                onLogLine?.Invoke($"[Pass 2/2] 开始正式编码输出: {Path.GetFileName(plan.OutputPath)}");
                var p2Ok = await RunSingleProcessAsync(
                    ffmpegPath, plan.Pass2Arguments, plan.EffectiveDurationSeconds, 2, 2, progress, onLogLine, ct);
                if (!p2Ok)
                {
                    DeletePartialFile(plan.OutputPath);
                    return false;
                }
                return true;
            }
            else
            {
                var ok = await RunSingleProcessAsync(
                    ffmpegPath, plan.Pass2Arguments, plan.EffectiveDurationSeconds, 1, 1, progress, onLogLine, ct);
                if (!ok)
                {
                    DeletePartialFile(plan.OutputPath);
                    return false;
                }
                return true;
            }
        }
        catch (OperationCanceledException)
        {
            DeletePartialFile(plan.OutputPath);
            throw;
        }
        finally
        {
            CleanupPassLogFiles(plan.TempPassLogPrefix);
        }
    }

    public void Pause()
    {
        lock (_procLock)
        {
            if (_currentProcess is { HasExited: false } proc && !_isPaused)
            {
                if (OperatingSystem.IsWindows())
                {
                    NtSuspendProcess(proc.Handle);
                }
                else
                {
                    SendUnixSignal(proc.Id, "-STOP");
                }
                _isPaused = true;
                _currentParser?.PauseTimer();
            }
        }
    }

    public void Resume()
    {
        lock (_procLock)
        {
            if (_currentProcess is { HasExited: false } proc && _isPaused)
            {
                if (OperatingSystem.IsWindows())
                {
                    NtResumeProcess(proc.Handle);
                }
                else
                {
                    SendUnixSignal(proc.Id, "-CONT");
                }
                _isPaused = false;
                _currentParser?.ResumeTimer();
            }
        }
    }

    public static void KillAllActive()
    {
        foreach (var kvp in ActiveProcesses)
        {
            try
            {
                if (!kvp.Value.HasExited)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        NtResumeProcess(kvp.Value.Handle);
                    }
                    else
                    {
                        SendUnixSignal(kvp.Value.Id, "-CONT");
                    }
                    kvp.Value.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
        ActiveProcesses.Clear();
    }

    private async Task<bool> RunSingleProcessAsync(
        string ffmpegPath,
        List<string> arguments,
        double effectiveDurationSeconds,
        int currentPass,
        int totalPasses,
        IProgress<EncodingProgress>? progress,
        Action<string>? onLogLine,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 ffmpeg 进程");
        ActiveProcesses[proc.Id] = proc;

        var parser = new ProgressParser(effectiveDurationSeconds, currentPass, totalPasses);
        lock (_procLock)
        {
            _currentProcess = proc;
            _currentParser = parser;
            _isPaused = false;
        }

        using var reg = ct.Register(() => GracefulTerminate(proc));

        try
        {
            var stdoutTask = Task.Run(async () =>
            {
                while (await proc.StandardOutput.ReadLineAsync() is { } line)
                {
                    var report = parser.FeedLine(line);
                    if (report is not null)
                    {
                        progress?.Report(report);
                    }
                }
            }, CancellationToken.None);

            var lastErrorLines = new Queue<string>(12);
            var stderrTask = Task.Run(async () =>
            {
                while (await proc.StandardError.ReadLineAsync() is { } line)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        onLogLine?.Invoke(line);
                        if (lastErrorLines.Count >= 12) lastErrorLines.Dequeue();
                        lastErrorLines.Enqueue(line);
                    }
                }
            }, CancellationToken.None);

            await proc.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdoutTask, stderrTask);

            ct.ThrowIfCancellationRequested();

            if (proc.ExitCode != 0)
            {
                var errTail = string.Join(" | ", lastErrorLines.TakeLast(3));
                throw new InvalidOperationException($"FFmpeg 退出码 {proc.ExitCode}: {errTail}");
            }

            return true;
        }
        finally
        {
            ActiveProcesses.TryRemove(proc.Id, out _);
            lock (_procLock)
            {
                if (ReferenceEquals(_currentProcess, proc))
                {
                    _currentProcess = null;
                    _currentParser = null;
                    _isPaused = false;
                }
            }
        }
    }

    private void GracefulTerminate(Process proc)
    {
        try
        {
            if (proc.HasExited) return;

            if (_isPaused)
            {
                if (OperatingSystem.IsWindows())
                {
                    NtResumeProcess(proc.Handle);
                }
                else
                {
                    SendUnixSignal(proc.Id, "-CONT");
                }
                _isPaused = false;
            }

            try
            {
                proc.StandardInput.WriteLine("q");
                proc.StandardInput.Flush();
            }
            catch
            {
                // Stdin might already be closed
            }

            if (!proc.WaitForExit(1500))
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignore termination race
        }
    }

    private static void SendUnixSignal(int pid, string signalFlag)
    {
        try
        {
            using var killProc = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/kill",
                Arguments = $"{signalFlag} {pid}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            killProc?.WaitForExit(1000);
        }
        catch
        {
            // Ignore if process already exited
        }
    }

    private static void DeletePartialFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore file lock on cleanup
        }
    }

    private static void CleanupPassLogFiles(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return;
        try
        {
            var dir = Path.GetDirectoryName(prefix);
            var name = Path.GetFileName(prefix);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

            foreach (var file in Directory.GetFiles(dir, $"{name}*"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Ignore
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr processHandle);
}
