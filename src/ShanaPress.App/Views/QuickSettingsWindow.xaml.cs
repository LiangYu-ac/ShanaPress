using System.Windows;

namespace ShanaPress.App.Views;

public partial class QuickSettingsWindow : Window
{
    public QuickSettingsWindow()
    {
        InitializeComponent();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
