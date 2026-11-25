using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ShanaPress.App.Infrastructure;

public static class WindowThemeHelper
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    // Win32 COLORREF is 0x00BBGGRR
    private const int DarkCaptionColorRef = 0x0017100D;  // #0D1017
    private const int LightCaptionColorRef = 0x00F9F5F1; // #F1F5F9

    public static readonly DependencyProperty EnableAutoThemeProperty =
        DependencyProperty.RegisterAttached(
            "EnableAutoTheme",
            typeof(bool),
            typeof(WindowThemeHelper),
            new PropertyMetadata(false, OnEnableAutoThemeChanged));

    public static bool GetEnableAutoTheme(DependencyObject obj) => (bool)obj.GetValue(EnableAutoThemeProperty);
    public static void SetEnableAutoTheme(DependencyObject obj, bool value) => obj.SetValue(EnableAutoThemeProperty, value);

    private static void OnEnableAutoThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;

        TryEnsureWindowIcon(window);

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
        {
            ApplyTitleBarTheme(window, ThemeManager.IsDark);
        }
        else
        {
            window.SourceInitialized += OnWindowSourceInitialized;
        }
    }

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            window.SourceInitialized -= OnWindowSourceInitialized;
            ApplyTitleBarTheme(window, ThemeManager.IsDark);
        }
    }

    public static void ApplyTitleBarTheme(Window window, bool isDark)
    {
        TryEnsureWindowIcon(window);
        if (!OperatingSystem.IsWindows()) return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var useDark = isDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref useDark, Marshal.SizeOf<int>()) != 0)
        {
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, Marshal.SizeOf<int>());
        }

        var captionColor = isDark ? DarkCaptionColorRef : LightCaptionColorRef;
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref captionColor, Marshal.SizeOf<int>());
    }

    private static void TryEnsureWindowIcon(Window window)
    {
        if (window.Icon is not null) return;
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            window.Icon = BitmapFrame.Create(uri);
        }
        catch
        {
            // Ignore in design-time or non-pack contexts
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
