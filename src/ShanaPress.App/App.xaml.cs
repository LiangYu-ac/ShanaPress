using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ShanaPress.App.Infrastructure;
using ShanaPress.App.ViewModels;
using ShanaPress.App.Views;
using ShanaPress.Core.Services;

namespace ShanaPress.App;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("SHANAPRESS_SOFTWARE_RENDER") == "1")
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        base.OnStartup(e);

        var settingsService = new AppSettingsService();
        var settings = settingsService.Load();
        ThemeManager.ApplyTheme(settings.IsDarkTheme);

        _mainViewModel = new MainViewModel(settingsService, settings);
        var mainWindow = new MainWindow { DataContext = _mainViewModel };
        MainWindow = mainWindow;
        mainWindow.Show();

        if (e.Args.Length > 0)
        {
            _ = _mainViewModel.AddPathsAsync(e.Args);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.SaveSettings();
        FfmpegProcessRunner.KillAllActive();
        base.OnExit(e);
    }
}
