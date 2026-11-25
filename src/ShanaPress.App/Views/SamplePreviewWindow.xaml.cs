using System.Diagnostics;
using System.Windows;
using ShanaPress.App.Infrastructure;
using ShanaPress.Core.Models;
using ShanaPress.Core.Services;

namespace ShanaPress.App.Views;

public partial class SamplePreviewWindow : Window
{
    private readonly SampleTestResult _result;
    private readonly Action _openQuickSettings;

    public SamplePreviewWindow(SampleTestResult result, Action openQuickSettings)
    {
        InitializeComponent();
        _result = result;
        _openQuickSettings = openQuickSettings;

        FileNameText.Text = result.SourceFileName;
        ProfileSummaryText.Text = $"测试配置: {result.ProfileSummary}";
        SourceSizeText.Text = BitrateCalculator.FormatFileSize(result.SourceSizeBytes);
        SampleSizeText.Text = $"{BitrateCalculator.FormatFileSize(result.SampleSizeBytes)} ({result.SampleDurationSeconds:0.#} 秒片段)";
        ElapsedText.Text = $"{result.ElapsedEncodeTime.TotalSeconds:0.1} 秒";
        EstimatedFullText.Text = BitrateCalculator.FormatFileSize(result.EstimatedFullSizeBytes);

        var ratio = BitrateCalculator.FormatCompressionPercent(result.SourceSizeBytes, result.EstimatedFullSizeBytes);
        EstimatedRatioText.Text = string.IsNullOrEmpty(ratio) ? "N/A" : $"{ratio} (完整时长 {BitrateCalculator.FormatDuration(TimeSpan.FromSeconds(result.FullEffectiveDurationSeconds))})";
    }

    private void PlaySample_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _result.SampleOutputPath,
            UseShellExecute = true
        });
    }

    private void OpenSampleFolder_Click(object sender, RoutedEventArgs e) =>
        PostEncodeActionHelper.RevealFileInExplorer(_result.SampleOutputPath);

    private void AdjustSettings_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _openQuickSettings();
    }
}
