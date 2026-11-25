using System.Windows;

namespace ShanaPress.App.Infrastructure;

public static class ThemeManager
{
    private const string DarkThemeUri = "Themes/DarkTheme.xaml";
    private const string LightThemeUri = "Themes/LightTheme.xaml";

    public static bool IsDark { get; private set; } = true;

    public static void ApplyTheme(bool isDark)
    {
        IsDark = isDark;

        var app = Application.Current;
        if (app is null) return;

        var dicts = app.Resources.MergedDictionaries;
        var existingTheme = dicts.FirstOrDefault(d =>
            d.Source is not null &&
            (d.Source.OriginalString.Contains("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase) ||
             d.Source.OriginalString.Contains("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)));

        var newSource = new Uri(isDark ? DarkThemeUri : LightThemeUri, UriKind.Relative);
        var newDict = new ResourceDictionary { Source = newSource };

        if (existingTheme is not null)
        {
            var idx = dicts.IndexOf(existingTheme);
            dicts[idx] = newDict;
        }
        else
        {
            dicts.Insert(0, newDict);
        }

        foreach (Window win in app.Windows)
        {
            WindowThemeHelper.ApplyTitleBarTheme(win, isDark);
        }
    }
}
