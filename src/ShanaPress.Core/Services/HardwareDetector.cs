using System.Diagnostics;
using System.Text;
using Microsoft.Win32;
using ShanaPress.Core.Models;

namespace ShanaPress.Core.Services;

public static class HardwareDetector
{
    private static readonly string[] WindowsHardwareEncoders =
    [
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_amf", "hevc_amf", "av1_amf",
        "h264_qsv", "hevc_qsv", "av1_qsv"
    ];

    private static readonly string[] MacHardwareEncoders =
    [
        "h264_videotoolbox", "hevc_videotoolbox"
    ];

    private static readonly string[] LinuxHardwareEncoders =
    [
        "h264_nvenc", "hevc_nvenc", "av1_nvenc",
        "h264_vaapi", "hevc_vaapi", "av1_vaapi",
        "h264_qsv", "hevc_qsv", "av1_qsv"
    ];

    public static async Task<HardwareCapabilities> DetectAsync(
        string? customFfmpegPath = null,
        string? customFfprobePath = null,
        CancellationToken ct = default)
    {
        var caps = new HardwareCapabilities();
        caps.GpuNames = await ReadGpuNamesAsync(ct);

        var ffmpegPath = FfmpegLocator.ResolveFfmpeg(customFfmpegPath);
        var ffprobePath = FfmpegLocator.ResolveFfprobe(customFfprobePath, ffmpegPath);

        caps.FfmpegPath = ffmpegPath ?? string.Empty;
        caps.FfprobePath = ffprobePath ?? string.Empty;

        if (!caps.HasFfmpeg)
        {
            return caps;
        }

        caps.FfmpegVersion = await ReadFfmpegVersionAsync(caps.FfmpegPath, ct);
        caps.AvailableEncoders = await ReadAvailableEncodersAsync(caps.FfmpegPath, ct);

        var candidates = OperatingSystem.IsWindows()
            ? WindowsHardwareEncoders
            : OperatingSystem.IsMacOS()
                ? MacHardwareEncoders
                : LinuxHardwareEncoders;

        var probeTasks = candidates
            .Where(e => caps.AvailableEncoders.Contains(e))
            .Select(async encoder =>
            {
                var ok = await ProbeHardwareEncoderAsync(caps.FfmpegPath, encoder, ct);
                return (encoder, ok);
            });

        var results = await Task.WhenAll(probeTasks);
        foreach (var (encoder, ok) in results)
        {
            if (ok)
            {
                caps.WorkingEncoders.Add(encoder);
            }
        }

        return caps;
    }

    private static async Task<List<string>> ReadGpuNamesAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            return ReadWindowsGpuNames();
        }
        if (OperatingSystem.IsMacOS())
        {
            return await ReadMacGpuNamesAsync(ct);
        }
        if (OperatingSystem.IsLinux())
        {
            return await ReadLinuxGpuNamesAsync(ct);
        }
        return new List<string>();
    }

    private static List<string> ReadWindowsGpuNames()
    {
        var list = new List<string>();
        if (!OperatingSystem.IsWindows()) return list;

        try
        {
            const string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using var baseKey = Registry.LocalMachine.OpenSubKey(classPath);
            if (baseKey is null) return list;

            foreach (var subName in baseKey.GetSubKeyNames())
            {
                if (!subName.StartsWith("0", StringComparison.Ordinal)) continue;
                using var subKey = baseKey.OpenSubKey(subName);
                var desc = subKey?.GetValue("DriverDesc")?.ToString();
                if (string.IsNullOrWhiteSpace(desc)) continue;
                if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!list.Contains(desc, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(desc);
                }
            }
        }
        catch
        {
            // Registry read is best-effort only
        }

        return list;
    }

    private static async Task<List<string>> ReadMacGpuNamesAsync(CancellationToken ct)
    {
        var list = new List<string>();
        try
        {
            var cpuBrand = (await RunSimpleProcessAsync("sysctl", "-n machdep.cpu.brand_string", 2500, ct)).Trim();
            if (!string.IsNullOrWhiteSpace(cpuBrand) && cpuBrand.Contains("Apple", StringComparison.OrdinalIgnoreCase))
            {
                list.Add(cpuBrand);
                return list;
            }

            var output = await RunSimpleProcessAsync("system_profiler", "SPDisplaysDataType", 4000, ct);
            foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Chipset Model:", StringComparison.OrdinalIgnoreCase))
                {
                    var name = line["Chipset Model:".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(name);
                    }
                }
            }
        }
        catch
        {
            // Ignore macOS probe failures
        }
        return list;
    }

    private static async Task<List<string>> ReadLinuxGpuNamesAsync(CancellationToken ct)
    {
        var list = new List<string>();
        try
        {
            var output = await RunSimpleProcessAsync("lspci", string.Empty, 2500, ct);
            foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!rawLine.Contains("VGA", StringComparison.OrdinalIgnoreCase) &&
                    !rawLine.Contains("3D controller", StringComparison.OrdinalIgnoreCase) &&
                    !rawLine.Contains("Display controller", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var colon = rawLine.IndexOf(": ", StringComparison.Ordinal);
                if (colon > 0)
                {
                    var secondColon = rawLine.IndexOf(": ", colon + 2, StringComparison.Ordinal);
                    var gpuName = (secondColon > 0 ? rawLine[(secondColon + 2)..] : rawLine[(colon + 2)..]).Trim();
                    if (!string.IsNullOrWhiteSpace(gpuName) && !list.Contains(gpuName, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(gpuName);
                    }
                }
            }
        }
        catch
        {
            // lspci might not be installed on minimal Linux distros
        }
        return list;
    }

    private static async Task<string> ReadFfmpegVersionAsync(string ffmpegPath, CancellationToken ct)
    {
        try
        {
            var output = await RunSimpleProcessAsync(ffmpegPath, "-version", 4000, ct);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<HashSet<string>> ReadAvailableEncodersAsync(string ffmpegPath, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var output = await RunSimpleProcessAsync(ffmpegPath, "-hide_banner -encoders", 5000, ct);
            foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length < 8 || line[0] is not ('V' or 'A' or 'S')) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    set.Add(parts[1]);
                }
            }
        }
        catch
        {
            // Ignore probe errors
        }
        return set;
    }

    private static async Task<bool> ProbeHardwareEncoderAsync(string ffmpegPath, string encoder, CancellationToken ct)
    {
        try
        {
            string args;
            if (encoder.EndsWith("_vaapi", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists("/dev/dri/renderD128")) return false;
                args = $"-hide_banner -loglevel error -vaapi_device /dev/dri/renderD128 -f lavfi -i testsrc2=duration=0.1:size=320x240:rate=30 -vf format=nv12,hwupload -c:v {encoder} -f null -";
            }
            else
            {
                args = $"-hide_banner -loglevel error -f lavfi -i testsrc2=duration=0.1:size=320x240:rate=30 -c:v {encoder} -f null -";
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(5000);

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            await proc.WaitForExitAsync(timeoutCts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> RunSimpleProcessAsync(string exePath, string arguments, int timeoutMs, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var proc = Process.Start(psi);
        if (proc is null) return string.Empty;

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        await proc.WaitForExitAsync(timeoutCts.Token);
        return await stdoutTask + "\n" + await stderrTask;
    }
}
