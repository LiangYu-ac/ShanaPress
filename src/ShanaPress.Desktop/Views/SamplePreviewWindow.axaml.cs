using Avalonia.Controls;
using Avalonia.Interactivity;
using ShanaPress.Core.Models;
using ShanaPress.Core.Services;
using ShanaPress.Desktop.Infrastructure;

namespace ShanaPress.Desktop.Views;

public partial class SamplePreviewWindow : Window
{
    private readonly SampleTestResult? _result;
    private readonly Action? _openQuickSettings;

    public SamplePreviewWindow()
    {
        InitializeComponent();
    }

    public SamplePreviewWindow(SampleTestResult result, Action openQuickSettings) : this()
    {
        _result = result;
        _openQuickSettings = openQuickSettings;

        FileNameText.Text = result.SourceFileName;
        ProfileSummaryText.Text = $"测试配置: {result.ProfileSummary}";
        SourceSizeText.Text = BitrateCalculator.FormatFileSize(result.SourceSizeBytes);
        SampleSizeText.Text = $"{BitrateCalculator.FormatFileSize(result.SampleSizeBytes)} ({result.SampleDurationSeconds:0.#} 秒片段)";
        ElapsedText.Text = $"{result.ElapsedEncodeTime.TotalSeconds:0.1} 秒";
        EstimatedFullText.Text = BitrateCalculator.FormatFileSize(result.EstimatedFullSizeBytes);

        var ratio = BitrateCalculator.FormatCompressionPercent(result.SourceSizeBytes, result.EstimatedFullSizeBytes);
        EstimatedRatioText.Text = string.IsNullOrEmpty(ratio)
            ? "N/A"
            : $"{ratio} (完整时长 {BitrateCalculator.FormatDuration(TimeSpan.FromSeconds(result.FullEffectiveDurationSeconds))})";
    }

    private void PlaySample_Click(object? sender, RoutedEventArgs e) =>
        PlatformShellHelper.OpenFileWithDefaultApp(_result?.SampleOutputPath);

    private void OpenSampleFolder_Click(object? sender, RoutedEventArgs e) =>
        PlatformShellHelper.RevealFile(_result?.SampleOutputPath);

    private void AdjustSettings_Click(object? sender, RoutedEventArgs e)
    {
        Close();
        _openQuickSettings?.Invoke();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
