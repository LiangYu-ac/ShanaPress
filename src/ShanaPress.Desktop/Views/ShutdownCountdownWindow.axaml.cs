using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ShanaPress.Desktop.Views;

public partial class ShutdownCountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remainingSeconds = 30;

    public ShutdownCountdownWindow()
    {
        InitializeComponent();
        UpdateText();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _remainingSeconds--;
            if (_remainingSeconds <= 0)
            {
                _timer.Stop();
                Close(true);
            }
            else
            {
                UpdateText();
            }
        };
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateText() =>
        CountdownText.Text = $"系统将在 {_remainingSeconds} 秒后自动关闭计算机。如有未保存的工作，请点击“取消关机”。";

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close(false);
    }

    private void ShutdownNow_Click(object? sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close(true);
    }
}
