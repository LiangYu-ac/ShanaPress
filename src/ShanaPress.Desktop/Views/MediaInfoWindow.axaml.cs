using Avalonia.Controls;
using Avalonia.Interactivity;
using ShanaPress.Core.Models;

namespace ShanaPress.Desktop.Views;

public partial class MediaInfoWindow : Window
{
    public MediaInfoWindow()
    {
        InitializeComponent();
    }

    public MediaInfoWindow(MediaInfo mediaInfo) : this()
    {
        TitleText.Text = $"文件媒体分析: {Path.GetFileName(mediaInfo.FilePath)}";
        ReportTextBox.Text = mediaInfo.BuildDetailedReport();
    }

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not null && !string.IsNullOrEmpty(ReportTextBox.Text))
        {
            await Clipboard.SetTextAsync(ReportTextBox.Text);
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
