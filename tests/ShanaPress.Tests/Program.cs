using System.Diagnostics;
using System.Text;
using ShanaPress.Core.Models;
using ShanaPress.Core.Services;

namespace ShanaPress.Tests;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var unitOnly = args.Any(a => a.Equals("--unit-only", StringComparison.OrdinalIgnoreCase));
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ShanaPress_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            Console.WriteLine("=== ShanaPress Automated Test Suite ===");
            Console.WriteLine($"Temp Workspace: {tempRoot}\n");

            // Unit Tests
            Run("Unit: BitrateCalculator target size, clamp & formatters", TestBitrateCalculator);
            Run("Unit: FfmpegFilterBuilder escaping, ordering, watermark & GIF", () => TestFilterBuilder(tempRoot));
            Run("Unit: FfmpegCommandBuilder codecs, 2-pass, NVENC, AMF, Trim/Sub & safety", () => TestCommandBuilder(tempRoot));
            Run("Unit: ProgressParser 1-pass and 2-pass progress & ETA", TestProgressParser);
            Run("Unit: PresetRepository built-ins & JSON round-trip", () => TestPresetRepository(tempRoot));
            Run("Unit: Cross-platform VideoToolbox, VAAPI, capabilities & presets", () => TestCrossPlatformEncodersAndPresets(tempRoot));

            if (unitOnly)
            {
                Console.WriteLine("\n[--unit-only] Skipping FFmpeg integration tests.");
            }
            else
            {
                // Real FFmpeg Integration Tests
                var caps = await RunAsync("Integration: HardwareDetector locates FFmpeg & RTX NVENC", TestHardwareDetector);
                if (caps is { HasFfmpeg: true, HasFfprobe: true })
                {
                    var fixtures = await PrepareMediaFixturesAsync(caps.FfmpegPath, tempRoot);

                    await RunAsync("Integration: MediaProbeService Probe, Frame PNG & DetectCrop", () =>
                        TestMediaProbeAsync(caps, fixtures));

                    await RunAsync("Integration: Real NVENC H.264 / HEVC / AV1 encodes", () =>
                        TestNvencEncodesAsync(caps, fixtures, tempRoot));

                    await RunAsync("Integration: Real libx264 2-Pass Target File Size & temp cleanup", () =>
                        TestTwoPassTargetSizeAsync(caps, fixtures, tempRoot));

                    await RunAsync("Integration: Complex Filter (Trim+Crop+Scale+Rotate+Hardsub+Text+ImageWM)", () =>
                        TestComplexFiltersAsync(caps, fixtures, tempRoot));

                    await RunAsync("Integration: HDR-to-SDR zscale+tonemap filter chain", () =>
                        TestHdrToSdrAsync(caps, fixtures, tempRoot));

                    await RunAsync("Integration: GIF palettegen & MP3 audio extraction", () =>
                        TestGifAndAudioAsync(caps, fixtures, tempRoot));

                    await RunAsync("Integration: 10s Sample Preview & full-size extrapolation", () =>
                        TestSamplePreviewAsync(caps, fixtures));

                    await RunAsync("Integration: Real Pause/Resume & Graceful Cancel cleanup", () =>
                        TestPauseResumeAndCancelAsync(caps, tempRoot));
                }
                else
                {
                    Fail("Integration suite skipped", "ffmpeg.exe or ffprobe.exe not detected");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"=== Summary: {_passed} PASSED, {_failed} FAILED ===");
            return _failed == 0 ? 0 : 1;
        }
        finally
        {
            FfmpegProcessRunner.KillAllActive();
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore transient file locks during temp cleanup
            }
        }
    }

    private sealed record TestFixtures(
        string SourceMp4,
        string LetterboxMp4,
        string ChineseSrtPath,
        string WatermarkPngPath);

    private static async Task<TestFixtures> PrepareMediaFixturesAsync(string ffmpegPath, string tempRoot)
    {
        var sourceMp4 = Path.Combine(tempRoot, "sample_3s.mp4");
        var letterboxMp4 = Path.Combine(tempRoot, "letterbox_3s.mp4");
        var subDir = Path.Combine(tempRoot, "字幕目录 [中文]");
        Directory.CreateDirectory(subDir);
        var srtPath = Path.Combine(subDir, "测试'字幕'.srt");
        var pngPath = Path.Combine(tempRoot, "logo_wm.png");

        // 1. 3-second 640x360 MP4 with sine audio
        await RunFfmpegDirectAsync(ffmpegPath,
            "-y -hide_banner -loglevel error -f lavfi -i testsrc2=duration=3:size=640x360:rate=30 -f lavfi -i sine=frequency=880:duration=3 -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 96k",
            sourceMp4);

        // 2. 3-second 640x360 MP4 with 40px top and 40px bottom pure black bars for cropdetect
        await RunFfmpegDirectAsync(ffmpegPath,
            "-y -hide_banner -loglevel error -f lavfi -i testsrc2=duration=3:size=640x280:rate=30 -vf pad=640:360:0:40:black -c:v libx264 -preset ultrafast -pix_fmt yuv420p",
            letterboxMp4);

        // 3. SRT with Chinese text & special filename
        await File.WriteAllTextAsync(srtPath,
            "1\r\n00:00:00,000 --> 00:00:03,000\r\n你好 ShanaPress 字幕测试\r\n",
            new UTF8Encoding(false));

        // 4. Transparent RGBA PNG watermark
        await RunFfmpegDirectAsync(ffmpegPath,
            "-y -hide_banner -loglevel error -f lavfi -i color=c=blue@0.8:s=120x40,format=rgba -frames:v 1",
            pngPath);

        return new TestFixtures(sourceMp4, letterboxMp4, srtPath, pngPath);
    }

    private static async Task RunFfmpegDirectAsync(string ffmpegPath, string argsPrefix, string outputPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"{argsPrefix} \"{outputPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg fixture generator");
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture ffmpeg failed ({p.ExitCode}): {err}");
        }
    }

    private static void TestBitrateCalculator()
    {
        // 25 MB, 120 seconds, 128k audio
        var kbps = BitrateCalculator.CalculateTargetVideoBitrateKbps(25.0, 120.0, 128, true);
        // 25 * 1024 * 1024 * 8 * 0.975 / 120000 = 1703.936 - 128 = 1575
        Assert(kbps is >= 1570 and <= 1580, $"Expected ~1575 kbps, got {kbps}");

        // Floor clamp at 100 kbps
        var clamped = BitrateCalculator.CalculateTargetVideoBitrateKbps(0.2, 600.0, 128, true);
        Assert(clamped == 100, $"Expected 100 kbps floor, got {clamped}");

        // No audio adds full budget to video
        var noAudio = BitrateCalculator.CalculateTargetVideoBitrateKbps(25.0, 120.0, 128, false);
        Assert(noAudio > kbps + 100, $"No-audio bitrate ({noAudio}) should exceed audio bitrate ({kbps})");

        // Sample extrapolation
        var est = BitrateCalculator.EstimateFullSizeBytes(1_000_000, 10.0, 100.0);
        Assert(est == 10_000_000, $"Expected 10_000_000, got {est}");

        // Formatters
        Assert(BitrateCalculator.FormatFileSize(1536) == "1.5 KB", "FormatFileSize KB");
        Assert(BitrateCalculator.FormatDuration(TimeSpan.FromSeconds(3661)) == "01:01:01", "FormatDuration >1h");
        Assert(BitrateCalculator.FormatCompressionPercent(1000, 400) == "-60.0%", "FormatCompressionPercent -60%");
        Assert(BitrateCalculator.TryParseTimestamp("00:01:30.500", out var sec) && Math.Abs(sec - 90.5) < 0.001, "TryParseTimestamp");
    }

    private static void TestFilterBuilder(string tempRoot)
    {
        var srtFile = Path.Combine(tempRoot, "测试 [1].srt");
        File.WriteAllText(srtFile, "1\n00:00:00,000 --> 00:00:01,000\nHi\n");
        var wmFile = Path.Combine(tempRoot, "wm_unit.png");
        File.WriteAllBytes(wmFile, [1, 2, 3]);

        // Path escaping
        var esc = FfmpegFilterBuilder.EscapeFilterPath(@"C:\视频 [2026]\sub'1'.srt");
        Assert(esc.StartsWith(@"C\:/视频 [2026]/", StringComparison.Ordinal), $"Drive colon escape mismatch: {esc}");
        Assert(esc.Contains(@"'\\\''"), $"Single quote escape mismatch: {esc}");

        // Drawtext escaping
        var dt = FfmpegFilterBuilder.EscapeDrawtextText("100%: It's OK");
        Assert(dt.Contains(@"\:") && dt.Contains(@"'\''"), $"Drawtext escape mismatch: {dt}");

        // Ordered filter chain
        var profile = new EncodingProfile
        {
            DeinterlaceMode = DeinterlaceMode.Bwdif,
            Rotation = RotationMode.Cw90,
            FlipHorizontal = true,
            ResizeMode = ResizeMode.PresetHeight,
            ResizeHeight = 720,
            FrameRateMode = FrameRateMode.Custom,
            TargetFps = 30,
            HdrToSdr = true,
            DenoiseStrength = FilterStrength.Light,
            SharpenStrength = FilterStrength.Light,
            TextWatermarkEnabled = true,
            TextWatermarkContent = "测试:100%"
        };
        var res = FfmpegFilterBuilder.BuildVideoFilterGraph(
            profile, null, true, 10, 20, 10, 20, SubtitleMode.BurnIn, srtFile);

        Assert(!res.UseFilterComplex && !string.IsNullOrEmpty(res.VideoFilter), "Expected simple -vf");
        var vf = res.VideoFilter!;
        var iBwdif = vf.IndexOf("bwdif", StringComparison.Ordinal);
        var iCrop = vf.IndexOf("crop=", StringComparison.Ordinal);
        var iRot = vf.IndexOf("transpose=1", StringComparison.Ordinal);
        var iScale = vf.IndexOf("scale=-2:720", StringComparison.Ordinal);
        var iFps = vf.IndexOf("fps=30", StringComparison.Ordinal);
        var iZscale = vf.IndexOf("zscale=pin=bt2020:tin=smpte2084", StringComparison.Ordinal);
        var iDenoise = vf.IndexOf("hqdn3d", StringComparison.Ordinal);
        var iSharp = vf.IndexOf("unsharp", StringComparison.Ordinal);
        var iSub = vf.IndexOf("subtitles=", StringComparison.Ordinal);
        var iDraw = vf.IndexOf("drawtext=", StringComparison.Ordinal);
        Assert(iBwdif >= 0 && iBwdif < iCrop && iCrop < iRot && iRot < iScale && iScale < iFps &&
               iFps < iZscale && iZscale < iDenoise && iDenoise < iSharp && iSharp < iSub && iSub < iDraw,
               $"Filter chain ordering wrong: {vf}");
        Assert(vf.Contains("expansion=none"), "drawtext must include expansion=none");

        // Image watermark -> -filter_complex
        profile.ImageWatermarkEnabled = true;
        profile.ImageWatermarkPath = wmFile;
        var wmRes = FfmpegFilterBuilder.BuildVideoFilterGraph(profile, null, false, 0, 0, 0, 0, SubtitleMode.KeepSoft, null);
        Assert(wmRes.UseFilterComplex && wmRes.FilterComplex!.Contains("scale2ref") && wmRes.FilterComplex.Contains("overlay="),
            "Image watermark must generate filter_complex with scale2ref + overlay");

        // GIF -> palettegen + paletteuse
        var gifProfile = new EncodingProfile { ContainerFormat = "gif", VideoCodec = "gif", AudioCodec = "none" };
        var gifRes = FfmpegFilterBuilder.BuildVideoFilterGraph(gifProfile, null, false, 0, 0, 0, 0, SubtitleMode.Remove, null);
        Assert(gifRes.UseFilterComplex && gifRes.FilterComplex!.Contains("palettegen") && gifRes.FilterComplex.Contains("paletteuse"),
            "GIF must generate palettegen/paletteuse graph");
    }

    private static void TestCommandBuilder(string tempRoot)
    {
        var input = Path.Combine(tempRoot, "demo.mp4");
        File.WriteAllBytes(input, [0]);
        var item = new QueueItem(input, 100_000_000)
        {
            MediaInfo = new MediaInfo
            {
                FilePath = input,
                Duration = TimeSpan.FromSeconds(120),
                VideoStream = new VideoStreamInfo { Width = 1920, Height = 1080, CodecName = "h264" },
                AudioStreams = [new AudioStreamInfo { CodecName = "aac", Channels = 2 }]
            }
        };

        // 1. x265 2-Pass Target Size
        var p2 = new EncodingProfile
        {
            VideoCodec = "libx265",
            RateControlMode = RateControlMode.TargetFileSize,
            TargetFileSizeMb = 25,
            TargetSizeUseTwoPass = true,
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot
        };
        var plan2 = FfmpegCommandBuilder.BuildJobPlan(p2, item);
        var nullSink = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        Assert(plan2.IsTwoPass, "x265 target size should use 2-pass");
        Assert(plan2.Pass1Arguments.Contains("-pass") && plan2.Pass1Arguments.Contains(nullSink) && plan2.Pass1Arguments.Contains("-an"),
            "Pass 1 args invalid");
        Assert(plan2.Pass2Arguments.Contains("-pass") && plan2.Pass2Arguments.Contains("-passlogfile"),
            "Pass 2 args invalid");

        // 2. NVENC AV1 CQ clamp & multipass
        var pNv = new EncodingProfile
        {
            VideoCodec = "av1_nvenc",
            RateControlMode = RateControlMode.CrfOrCq,
            QualityValue = 60,
            NvencSpatialAq = true
        };
        var planNv = FfmpegCommandBuilder.BuildJobPlan(pNv, item);
        Assert(!planNv.IsTwoPass && !planNv.Pass2Arguments.Contains("-pass"), "NVENC must never use -pass");
        Assert(planNv.Pass2Arguments.Contains("60") && planNv.Pass2Arguments.Contains("-cq"), "AV1 NVENC must allow CQ 60");

        var pNv2Pass = new EncodingProfile
        {
            VideoCodec = "hevc_nvenc",
            RateControlMode = RateControlMode.TwoPassBitrate,
            VideoBitrateKbps = 3000
        };
        var planNv2 = FfmpegCommandBuilder.BuildJobPlan(pNv2Pass, item);
        Assert(!planNv2.IsTwoPass && planNv2.Pass2Arguments.Contains("-multipass") && planNv2.Pass2Arguments.Contains("fullres"),
            "NVENC 2-pass must emit single-invocation -multipass fullres");

        // 3. AMF CQP
        var pAmf = new EncodingProfile { VideoCodec = "h264_amf", RateControlMode = RateControlMode.CrfOrCq, QualityValue = 25 };
        var planAmf = FfmpegCommandBuilder.BuildJobPlan(pAmf, item);
        Assert(planAmf.Pass2Arguments.Contains("cqp") && planAmf.Pass2Arguments.Contains("-qp_i"), "AMF CQP args invalid");

        // 4. Stream Copy omits -vf / -filter_complex
        var pCopy = new EncodingProfile { VideoCodec = "copy", AudioCodec = "copy", ResizeMode = ResizeMode.PresetHeight, ResizeHeight = 720 };
        var planCopy = FfmpegCommandBuilder.BuildJobPlan(pCopy, item);
        Assert(!planCopy.Pass2Arguments.Contains("-vf") && !planCopy.Pass2Arguments.Contains("-filter_complex"),
            "Copy codec must omit video filters");

        // 5. Audio-only MP3 enforces -vn & libmp3lame
        var pMp3 = new EncodingProfile { ContainerFormat = "mp3", VideoCodec = "libx264", AudioCodec = "aac" };
        var planMp3 = FfmpegCommandBuilder.BuildJobPlan(pMp3, item);
        Assert(planMp3.Pass2Arguments.Contains("-vn") && planMp3.Pass2Arguments.Contains("libmp3lame"),
            "MP3 container must enforce -vn and libmp3lame");

        // 6. Hardsub + Trim places -ss AFTER -i; Fast Trim places -ss BEFORE -i
        var srtDummy = Path.Combine(tempRoot, "sub.srt");
        File.WriteAllText(srtDummy, "1\n00:00:00,000 --> 00:00:01,000\na\n");

        var pFastTrim = new EncodingProfile { TrimEnabled = true, TrimStartSeconds = 10, TrimEndSeconds = 20 };
        var argsFast = FfmpegCommandBuilder.BuildJobPlan(pFastTrim, item).Pass2Arguments;
        Assert(argsFast.IndexOf("-ss") < argsFast.IndexOf("-i"), "Fast trim without hardsub should place -ss before -i");

        var pHardTrim = new EncodingProfile
        {
            TrimEnabled = true,
            TrimStartSeconds = 10,
            TrimEndSeconds = 20,
            SubtitleMode = SubtitleMode.BurnIn,
            SubtitleFilePath = srtDummy
        };
        var argsHard = FfmpegCommandBuilder.BuildJobPlan(pHardTrim, item).Pass2Arguments;
        Assert(argsHard.IndexOf("-ss") > argsHard.IndexOf("-i"), "Trim with hardsub should place -ss after -i");

        // 7. Input == Output safety guard
        var pSame = new EncodingProfile
        {
            ContainerFormat = "mp4",
            FileNameTemplate = "{name}",
            OutputDirectoryMode = OutputDirectoryMode.SameAsSource,
            ConflictPolicy = ConflictPolicy.Overwrite
        };
        var (resolvedOut, _) = FfmpegCommandBuilder.ResolveOutputPath(input, pSame);
        Assert(!string.Equals(Path.GetFullPath(resolvedOut), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase) &&
               resolvedOut.EndsWith("_compressed.mp4", StringComparison.OrdinalIgnoreCase),
               $"Input==Output safety guard failed: {resolvedOut}");
    }

    private static void TestProgressParser()
    {
        var p1 = new ProgressParser(100.0, currentPass: 1, totalPasses: 2);
        p1.FeedLine("fps=60.0");
        p1.FeedLine("speed=2.0x");
        p1.FeedLine("bitrate=1500.0kbits/s");
        p1.FeedLine("out_time_us=50000000"); // 50s of 100s => 50% of pass 1 => 22.5% total
        var r1 = p1.FeedLine("progress=continue");
        Assert(r1 is not null && Math.Abs(r1.Percent - 22.5) < 0.5, $"Pass 1 50% should be ~22.5% total, got {r1?.Percent}");
        Assert(r1!.Fps == 60.0 && r1.Speed == 2.0, "FPS/Speed mismatch");

        var p2 = new ProgressParser(100.0, currentPass: 2, totalPasses: 2);
        p2.FeedLine("out_time_us=50000000"); // 50% of pass 2 => 45 + 27.5 = 72.5% total
        var r2 = p2.FeedLine("progress=continue");
        Assert(r2 is not null && Math.Abs(r2.Percent - 72.5) < 0.5, $"Pass 2 50% should be ~72.5% total, got {r2?.Percent}");

        var rEnd = p2.FeedLine("progress=end");
        Assert(rEnd is not null && rEnd.IsEnd && Math.Abs(rEnd.Percent - 100.0) < 0.01, "End must report 100%");
    }

    private static void TestPresetRepository(string tempRoot)
    {
        var builtIns = PresetRepository.GetBuiltInPresets();
        Assert(builtIns.Count >= 9, $"Expected >=9 built-in presets, got {builtIns.Count}");
        Assert(builtIns.All(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Profile.BuildSummary())),
            "All built-in presets must have valid names and summaries");

        var storeDir = Path.Combine(tempRoot, "PresetStore");
        var repo = new PresetRepository(storeDir);
        var custom = new PresetItem
        {
            Name = "自定义测试预设",
            IsBuiltIn = false,
            Profile = new EncodingProfile { VideoCodec = "libx265", QualityValue = 28 }
        };
        var all = repo.LoadAll();
        all.Add(custom);
        repo.SaveCustomPresets(all);

        var reloaded = repo.LoadAll();
        Assert(reloaded.Any(p => !p.IsBuiltIn && p.Name == "自定义测试预设" && p.Profile.QualityValue == 28),
            "Custom preset reload mismatch");

        var exportPath = Path.Combine(tempRoot, "exported.json");
        PresetRepository.ExportPreset(custom, exportPath);
        var imported = PresetRepository.ImportPreset(exportPath);
        Assert(imported is not null && imported.Name == "自定义测试预设" && imported.Profile.VideoCodec == "libx265",
            "Preset export/import round-trip failed");
    }

    private static async Task<HardwareCapabilities> TestHardwareDetector()
    {
        var caps = await HardwareDetector.DetectAsync();
        Assert(caps.HasFfmpeg, "ffmpeg.exe not detected");
        Assert(caps.HasFfprobe, "ffprobe.exe not detected");
        Assert(caps.HasNvencH264 && caps.HasNvencHevc && caps.HasNvencAv1,
            $"Expected working NVENC H264/HEVC/AV1, badge: {caps.BuildBadgeText()}");
        Console.WriteLine($"    Detected: {caps.BuildBadgeText()}");
        return caps;
    }

    private static async Task TestMediaProbeAsync(HardwareCapabilities caps, TestFixtures fx)
    {
        var svc = new MediaProbeService();
        var info = await svc.ProbeAsync(caps.FfprobePath, fx.SourceMp4);
        Assert(info.VideoStream is { Width: 640, Height: 360 }, $"Unexpected dimensions: {info.VideoStream?.Width}x{info.VideoStream?.Height}");
        Assert(Math.Abs(info.Duration.TotalSeconds - 3.0) < 0.3, $"Unexpected duration: {info.Duration.TotalSeconds}");
        Assert(info.AudioStreams.Count == 1, "Expected 1 audio stream");

        var png = await svc.ExtractFramePngAsync(caps.FfmpegPath, fx.SourceMp4, TimeSpan.FromSeconds(1.2), 480);
        Assert(png.Length > 100 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
            "ExtractFramePngAsync did not return a valid PNG");

        var crop = await svc.DetectCropAsync(caps.FfmpegPath, fx.LetterboxMp4, TimeSpan.FromSeconds(0.5), 640, 360);
        Assert(crop is not null && crop.Value.Top >= 36 && crop.Value.Bottom >= 36,
            $"DetectCropAsync failed on letterbox clip: {crop}");
    }

    private static async Task TestNvencEncodesAsync(HardwareCapabilities caps, TestFixtures fx, string tempRoot)
    {
        var probe = new MediaProbeService();
        var info = await probe.ProbeAsync(caps.FfprobePath, fx.SourceMp4);
        var runner = new FfmpegProcessRunner();

        foreach (var (codec, expectedProbeName, cq) in new[]
        {
            ("h264_nvenc", "h264", 24),
            ("hevc_nvenc", "hevc", 26),
            ("av1_nvenc", "av1", 30)
        })
        {
            var item = new QueueItem(fx.SourceMp4, info.FileSizeBytes) { MediaInfo = info };
            var profile = new EncodingProfile
            {
                ContainerFormat = "mp4",
                VideoCodec = codec,
                RateControlMode = RateControlMode.CrfOrCq,
                QualityValue = cq,
                Preset = "p5",
                NvencSpatialAq = true,
                OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
                CustomOutputDirectory = tempRoot,
                FileNameTemplate = $"out_{codec}"
            };

            var plan = FfmpegCommandBuilder.BuildJobPlan(profile, item);
            var ok = await runner.RunJobAsync(caps.FfmpegPath, plan, null, null, CancellationToken.None);
            Assert(ok && File.Exists(plan.OutputPath) && new FileInfo(plan.OutputPath).Length > 0, $"{codec} encode failed");

            var outInfo = await probe.ProbeAsync(caps.FfprobePath, plan.OutputPath);
            Assert(outInfo.VideoStream?.CodecName.Equals(expectedProbeName, StringComparison.OrdinalIgnoreCase) == true,
                $"Expected codec {expectedProbeName}, probed {outInfo.VideoStream?.CodecName}");
        }
    }

    private static async Task TestTwoPassTargetSizeAsync(HardwareCapabilities caps, TestFixtures fx, string tempRoot)
    {
        var probe = new MediaProbeService();
        var info = await probe.ProbeAsync(caps.FfprobePath, fx.SourceMp4);
        var item = new QueueItem(fx.SourceMp4, info.FileSizeBytes) { MediaInfo = info };

        var profile = new EncodingProfile
        {
            VideoCodec = "libx264",
            Preset = "veryfast",
            RateControlMode = RateControlMode.TargetFileSize,
            TargetFileSizeMb = 1.0,
            TargetSizeUseTwoPass = true,
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot,
            FileNameTemplate = "out_2pass"
        };

        var plan = FfmpegCommandBuilder.BuildJobPlan(profile, item);
        var lastPct = 0.0;
        var progress = new SyncProgress(p => lastPct = p.Percent);
        var runner = new FfmpegProcessRunner();
        var ok = await runner.RunJobAsync(caps.FfmpegPath, plan, progress, null, CancellationToken.None);
        Assert(ok && File.Exists(plan.OutputPath) && new FileInfo(plan.OutputPath).Length > 0, "2-pass encode failed");
        Assert(lastPct >= 99.0, $"Expected progress near 100%, got {lastPct}");

        if (!string.IsNullOrEmpty(plan.TempPassLogPrefix))
        {
            var leftover = Directory.GetFiles(Path.GetDirectoryName(plan.TempPassLogPrefix)!, Path.GetFileName(plan.TempPassLogPrefix) + "*");
            Assert(leftover.Length == 0, "Temp 2-pass logfiles were not cleaned up");
        }
    }

    private static async Task TestComplexFiltersAsync(HardwareCapabilities caps, TestFixtures fx, string tempRoot)
    {
        var probe = new MediaProbeService();
        var info = await probe.ProbeAsync(caps.FfprobePath, fx.SourceMp4);
        var item = new QueueItem(fx.SourceMp4, info.FileSizeBytes)
        {
            MediaInfo = info,
            PerFileTrimEnabled = true,
            TrimStartSeconds = 0.5,
            TrimEndSeconds = 2.5,
            PerFileCropEnabled = true,
            CropLeft = 20,
            CropTop = 10,
            CropRight = 20,
            CropBottom = 10,
            PerFileSubtitleMode = SubtitleMode.BurnIn,
            PerFileSubtitlePath = fx.ChineseSrtPath
        };

        var profile = new EncodingProfile
        {
            VideoCodec = "libx264",
            Preset = "veryfast",
            Rotation = RotationMode.Cw90,
            ResizeMode = ResizeMode.PresetHeight,
            ResizeHeight = 480,
            TextWatermarkEnabled = true,
            TextWatermarkContent = "水印 100%: Shana'Press",
            ImageWatermarkEnabled = true,
            ImageWatermarkPath = fx.WatermarkPngPath,
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot,
            FileNameTemplate = "out_complex"
        };

        var plan = FfmpegCommandBuilder.BuildJobPlan(profile, item);
        var runner = new FfmpegProcessRunner();
        var ok = await runner.RunJobAsync(caps.FfmpegPath, plan, null, null, CancellationToken.None);
        Assert(ok && File.Exists(plan.OutputPath), "Complex filter encode failed");

        var outInfo = await probe.ProbeAsync(caps.FfprobePath, plan.OutputPath);
        Assert(Math.Abs(outInfo.Duration.TotalSeconds - 2.0) < 0.35, $"Expected ~2s trimmed duration, got {outInfo.Duration.TotalSeconds}");
        Assert(outInfo.VideoStream?.Height == 480, $"Expected scaled height 480 after rotation, got {outInfo.VideoStream?.Height}");
    }

    private static async Task TestHdrToSdrAsync(HardwareCapabilities caps, TestFixtures fx, string tempRoot)
    {
        var probe = new MediaProbeService();
        var info = await probe.ProbeAsync(caps.FfprobePath, fx.SourceMp4);
        var item = new QueueItem(fx.SourceMp4, info.FileSizeBytes) { MediaInfo = info };

        var profile = new EncodingProfile
        {
            VideoCodec = "libx264",
            Preset = "ultrafast",
            HdrToSdr = true,
            HdrTonemapAlgorithm = "hable",
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot,
            FileNameTemplate = "out_hdr2sdr"
        };

        var plan = FfmpegCommandBuilder.BuildJobPlan(profile, item);
        var runner = new FfmpegProcessRunner();
        var ok = await runner.RunJobAsync(caps.FfmpegPath, plan, null, null, CancellationToken.None);
        Assert(ok && File.Exists(plan.OutputPath) && new FileInfo(plan.OutputPath).Length > 0, "HDR-to-SDR encode failed");
    }

    private static async Task TestGifAndAudioAsync(HardwareCapabilities caps, TestFixtures fx, string tempRoot)
    {
        var probe = new MediaProbeService();
        var info = await probe.ProbeAsync(caps.FfprobePath, fx.SourceMp4);
        var item = new QueueItem(fx.SourceMp4, info.FileSizeBytes) { MediaInfo = info };
        var runner = new FfmpegProcessRunner();

        // GIF
        var gifProfile = new EncodingProfile
        {
            ContainerFormat = "gif",
            VideoCodec = "gif",
            AudioCodec = "none",
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot,
            FileNameTemplate = "out_anim"
        };
        var gifPlan = FfmpegCommandBuilder.BuildJobPlan(gifProfile, item);
        var gifOk = await runner.RunJobAsync(caps.FfmpegPath, gifPlan, null, null, CancellationToken.None);
        Assert(gifOk && File.Exists(gifPlan.OutputPath) && new FileInfo(gifPlan.OutputPath).Length > 0, "GIF encode failed");

        // MP3
        var mp3Profile = new EncodingProfile
        {
            ContainerFormat = "mp3",
            VideoCodec = "none",
            AudioCodec = "libmp3lame",
            AudioBitrateKbps = 192,
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot,
            FileNameTemplate = "out_audio"
        };
        var mp3Plan = FfmpegCommandBuilder.BuildJobPlan(mp3Profile, item);
        var mp3Ok = await runner.RunJobAsync(caps.FfmpegPath, mp3Plan, null, null, CancellationToken.None);
        Assert(mp3Ok && File.Exists(mp3Plan.OutputPath) && new FileInfo(mp3Plan.OutputPath).Length > 0, "MP3 encode failed");
    }

    private static async Task TestSamplePreviewAsync(HardwareCapabilities caps, TestFixtures fx)
    {
        var probe = new MediaProbeService();
        var info = await probe.ProbeAsync(caps.FfprobePath, fx.SourceMp4);
        var item = new QueueItem(fx.SourceMp4, info.FileSizeBytes) { MediaInfo = info };

        var profile = new EncodingProfile { VideoCodec = "hevc_nvenc", QualityValue = 26 };
        var plan = FfmpegCommandBuilder.BuildJobPlan(profile, item, isSampleMode: true);
        var runner = new FfmpegProcessRunner();
        try
        {
            var ok = await runner.RunJobAsync(caps.FfmpegPath, plan, null, null, CancellationToken.None);
            Assert(ok && File.Exists(plan.OutputPath), "Sample encode failed");
            var bytes = new FileInfo(plan.OutputPath).Length;
            var est = BitrateCalculator.EstimateFullSizeBytes(bytes, plan.EffectiveDurationSeconds, info.Duration.TotalSeconds);
            Assert(bytes > 0 && est > 0, "Sample size estimate must be > 0");
        }
        finally
        {
            if (File.Exists(plan.OutputPath)) File.Delete(plan.OutputPath);
        }
    }

    private static async Task TestPauseResumeAndCancelAsync(HardwareCapabilities caps, string tempRoot)
    {
        // Generate a slightly heavier 10s 1080p synthetic clip so encode lasts >2s during pause/cancel test
        var heavyInput = Path.Combine(tempRoot, "heavy_10s.mp4");
        await RunFfmpegDirectAsync(caps.FfmpegPath,
            "-y -hide_banner -loglevel error -f lavfi -i testsrc2=duration=10:size=1280x720:rate=30 -c:v libx264 -preset ultrafast -pix_fmt yuv420p",
            heavyInput);

        var probe = new MediaProbeService();
        var info = await probe.ProbeAsync(caps.FfprobePath, heavyInput);
        var item = new QueueItem(heavyInput, info.FileSizeBytes) { MediaInfo = info };
        var profile = new EncodingProfile
        {
            VideoCodec = "libx264",
            Preset = "veryslow", // slow enough to pause/cancel mid-flight
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot,
            FileNameTemplate = "out_cancelled"
        };

        var plan = FfmpegCommandBuilder.BuildJobPlan(profile, item);
        var runner = new FfmpegProcessRunner();
        using var cts = new CancellationTokenSource();

        var encodeTask = runner.RunJobAsync(caps.FfmpegPath, plan, null, null, cts.Token);

        await Task.Delay(350);
        runner.Pause();
        Assert(runner.IsPaused, "Runner should be paused");
        await Task.Delay(300);
        runner.Resume();
        Assert(!runner.IsPaused, "Runner should be resumed");
        await Task.Delay(200);
        cts.Cancel();

        var cancelled = false;
        try
        {
            await encodeTask;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "Expected OperationCanceledException on cancel");
        Assert(!File.Exists(plan.OutputPath), "Partial output file must be deleted on cancel");
    }

    private sealed class SyncProgress(Action<EncodingProgress> callback) : IProgress<EncodingProgress>
    {
        public void Report(EncodingProgress value) => callback(value);
    }

    private static void Run(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        }
    }

    private static async Task<T?> RunAsync<T>(string name, Func<Task<T>> action)
    {
        try
        {
            var result = await action();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
            return result;
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
            return default;
        }
    }

    private static async Task RunAsync(string name, Func<Task> action)
    {
        try
        {
            await action();
            _passed++;
            Console.WriteLine($"[PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"[FAIL] {name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void TestCrossPlatformEncodersAndPresets(string tempRoot)
    {
        var input = Path.Combine(tempRoot, "cross_demo.mp4");
        File.WriteAllBytes(input, [0]);
        var wmFile = Path.Combine(tempRoot, "wm_vaapi.png");
        File.WriteAllBytes(wmFile, [1, 2, 3]);
        var item = new QueueItem(input, 50_000_000)
        {
            MediaInfo = new MediaInfo
            {
                FilePath = input,
                Duration = TimeSpan.FromSeconds(60),
                VideoStream = new VideoStreamInfo { Width = 1920, Height = 1080, CodecName = "h264" },
                AudioStreams = [new AudioStreamInfo { CodecName = "aac", Channels = 2 }]
            }
        };

        // 1. Apple VideoToolbox HEVC & H.264 (QualityValue uses CRF/CQ scale 1..51 -> mapped to VideoToolbox 15..95)
        var pVtHevc = new EncodingProfile
        {
            ContainerFormat = "mp4",
            VideoCodec = "hevc_videotoolbox",
            RateControlMode = RateControlMode.CrfOrCq,
            QualityValue = 25,
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot
        };
        var planVtHevc = FfmpegCommandBuilder.BuildJobPlan(pVtHevc, item);
        var vtHevcCmd = string.Join(' ', planVtHevc.Pass2Arguments);
        Assert(vtHevcCmd.Contains("-q:v 59", StringComparison.Ordinal),
            $"VideoToolbox HEVC should emit -q:v 59: {vtHevcCmd}");
        Assert(vtHevcCmd.Contains("-allow_sw 1", StringComparison.Ordinal) && vtHevcCmd.Contains("-tag:v hvc1", StringComparison.Ordinal),
            $"VideoToolbox HEVC should emit -allow_sw 1 and -tag:v hvc1: {vtHevcCmd}");

        var pVtH264 = new EncodingProfile
        {
            ContainerFormat = "mp4",
            VideoCodec = "h264_videotoolbox",
            RateControlMode = RateControlMode.CrfOrCq,
            QualityValue = 23,
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot
        };
        var planVtH264 = FfmpegCommandBuilder.BuildJobPlan(pVtH264, item);
        var vtH264Cmd = string.Join(' ', planVtH264.Pass2Arguments);
        Assert(vtH264Cmd.Contains("-q:v 62", StringComparison.Ordinal) && vtH264Cmd.Contains("-allow_sw 1", StringComparison.Ordinal) &&
               !vtH264Cmd.Contains("hvc1", StringComparison.Ordinal),
            $"VideoToolbox H.264 flags mismatch: {vtH264Cmd}");

        // 2. Linux VAAPI HEVC simple filter + no -pix_fmt
        var pVaapi = new EncodingProfile
        {
            ContainerFormat = "mp4",
            VideoCodec = "hevc_vaapi",
            RateControlMode = RateControlMode.CrfOrCq,
            QualityValue = 25,
            OutputDirectoryMode = OutputDirectoryMode.CustomFolder,
            CustomOutputDirectory = tempRoot
        };
        var planVaapi = FfmpegCommandBuilder.BuildJobPlan(pVaapi, item);
        var vaapiCmd = string.Join(' ', planVaapi.Pass2Arguments);
        Assert(vaapiCmd.Contains("-vaapi_device /dev/dri/renderD128", StringComparison.Ordinal),
            $"VAAPI should include -vaapi_device: {vaapiCmd}");
        Assert(vaapiCmd.Contains("format=nv12,hwupload", StringComparison.Ordinal),
            $"VAAPI filter should end with format=nv12,hwupload: {vaapiCmd}");
        Assert(vaapiCmd.Contains("-qp 25", StringComparison.Ordinal) && !vaapiCmd.Contains("-pix_fmt", StringComparison.Ordinal),
            $"VAAPI should use -qp and omit -pix_fmt: {vaapiCmd}");

        // 3. Linux VAAPI with image watermark -> filter_complex ends with format=nv12,hwupload
        var pVaapiWm = new EncodingProfile
        {
            ContainerFormat = "mp4",
            VideoCodec = "hevc_vaapi",
            RateControlMode = RateControlMode.CrfOrCq,
            QualityValue = 24,
            ImageWatermarkEnabled = true,
            ImageWatermarkPath = wmFile
        };
        var vaapiGraph = FfmpegFilterBuilder.BuildVideoFilterGraph(pVaapiWm, null, false, 0, 0, 0, 0, SubtitleMode.KeepSoft, null);
        Assert(vaapiGraph.UseFilterComplex && vaapiGraph.FilterComplex is not null &&
               vaapiGraph.FilterComplex.Contains("overlay=") && vaapiGraph.FilterComplex.Contains("format=nv12,hwupload"),
            $"VAAPI image watermark filter_complex mismatch: {vaapiGraph.FilterComplex}");

        // 4. HardwareCapabilities badge & default preset recommendation across OS encoders
        var fakeExe = Environment.ProcessPath ?? input;
        var macCaps = new HardwareCapabilities
        {
            FfmpegPath = fakeExe,
            FfprobePath = fakeExe,
            GpuNames = ["Apple M3 Pro"],
            WorkingEncoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264_videotoolbox", "hevc_videotoolbox" }
        };
        Assert(macCaps.BuildBadgeText().Contains("VideoToolbox") && PresetRepository.RecommendDefaultPresetId(macCaps) == "builtin-vt-hevc",
            $"macOS badge/preset mismatch: {macCaps.BuildBadgeText()}");

        var nvCaps = new HardwareCapabilities
        {
            FfmpegPath = fakeExe,
            FfprobePath = fakeExe,
            GpuNames = ["NVIDIA GeForce RTX 4070"],
            WorkingEncoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264_nvenc", "hevc_nvenc" }
        };
        Assert(nvCaps.BuildBadgeText().Contains("NVENC") && PresetRepository.RecommendDefaultPresetId(nvCaps) == "builtin-nvenc-hevc",
            $"NVENC badge/preset mismatch: {nvCaps.BuildBadgeText()}");

        var vaapiCaps = new HardwareCapabilities
        {
            FfmpegPath = fakeExe,
            FfprobePath = fakeExe,
            GpuNames = ["AMD Radeon Graphics"],
            WorkingEncoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "h264_vaapi", "hevc_vaapi" }
        };
        Assert(vaapiCaps.BuildBadgeText().Contains("VAAPI") && PresetRepository.RecommendDefaultPresetId(vaapiCaps) == "builtin-vaapi-hevc",
            $"VAAPI badge/preset mismatch: {vaapiCaps.BuildBadgeText()}");

        var cpuCaps = new HardwareCapabilities
        {
            FfmpegPath = fakeExe,
            FfprobePath = fakeExe
        };
        Assert(PresetRepository.RecommendDefaultPresetId(cpuCaps) == "builtin-cpu-x264",
            "CPU fallback default preset should be builtin-cpu-x264");
    }

    private static void Fail(string name, string message)
    {
        _failed++;
        Console.WriteLine($"[FAIL] {name}: {message}");
    }
}
