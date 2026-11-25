using System.IO;
using System.Windows;
using ShanaPress.Core.Models;

namespace ShanaPress.App.Views;

public partial class MediaInfoWindow : Window
{
    public MediaInfoWindow(MediaInfo mediaInfo)
    {
        InitializeComponent();
        TitleText.Text = $"文件媒体分析: {Path.GetFileName(mediaInfo.FilePath)}";
        ReportTextBox.Text = mediaInfo.BuildDetailedReport();
    }

    private void Copy_Click(object sender, RoutedEventArgs e) =>
        Clipboard.SetText(ReportTextBox.Text);
}
