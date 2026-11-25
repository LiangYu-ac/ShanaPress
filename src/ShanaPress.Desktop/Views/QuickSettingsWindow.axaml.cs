using Avalonia.Controls;
using Avalonia.Interactivity;
using ShanaPress.Desktop.ViewModels;

namespace ShanaPress.Desktop.Views;

public partial class QuickSettingsWindow : Window
{
    public QuickSettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is QuickSettingsViewModel vm)
            {
                vm.DialogOwner = this;
            }
        };
    }

    private void Apply_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
