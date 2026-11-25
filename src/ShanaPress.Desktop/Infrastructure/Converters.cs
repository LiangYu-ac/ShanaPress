using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ShanaPress.Core.Models;

namespace ShanaPress.Desktop.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

public sealed class CountZeroConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int count && count == 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not QueueItemStatus status) return Brushes.Gray;
        var key = status switch
        {
            QueueItemStatus.Completed => "SuccessBrush",
            QueueItemStatus.Encoding => "AccentBrush",
            QueueItemStatus.Paused => "WarningBrush",
            QueueItemStatus.Failed => "DangerBrush",
            QueueItemStatus.Probing => "AccentSoftTextBrush",
            QueueItemStatus.Skipped or QueueItemStatus.Cancelled => "WarningBrush",
            _ => "MutedTextBrush"
        };

        var app = Application.Current;
        if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush brush)
        {
            return brush;
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}
