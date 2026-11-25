using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using ShanaPress.Core.Services;
using ShanaPress.Desktop.ViewModels;
using ShanaPress.Desktop.Views;

namespace ShanaPress.Desktop;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsService = new AppSettingsService();
            var settings = settingsService.Load();

            RequestedThemeVariant = settings.IsDarkTheme ? ThemeVariant.Dark : ThemeVariant.Light;

            _mainViewModel = new MainViewModel(settingsService, settings);
            var mainWindow = new MainWindow { DataContext = _mainViewModel };
            desktop.MainWindow = mainWindow;

            desktop.ShutdownRequested += (_, _) =>
            {
                _mainViewModel?.SaveSettings();
                FfmpegProcessRunner.KillAllActive();
            };

            if (desktop.Args is { Length: > 0 })
            {
                _ = _mainViewModel.AddPathsAsync(desktop.Args);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
