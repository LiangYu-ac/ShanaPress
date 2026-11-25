using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ShanaPress.Desktop.Views;

public partial class MessageDialogWindow : Window
{
    public MessageDialogWindow()
    {
        InitializeComponent();
    }

    public static async Task ShowAsync(Window? owner, string title, string message)
    {
        var dlg = new MessageDialogWindow { Title = title };
        dlg.MessageText.Text = message;
        if (owner is not null) await dlg.ShowDialog(owner);
        else dlg.Show();
    }

    public static async Task<bool> ConfirmAsync(Window? owner, string title, string message)
    {
        var dlg = new MessageDialogWindow { Title = title };
        dlg.MessageText.Text = message;
        dlg.NoButton.IsVisible = true;
        dlg.OkButton.Content = "确定";
        return owner is not null && await dlg.ShowDialog<bool>(owner);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e) => Close(true);
    private void No_Click(object? sender, RoutedEventArgs e) => Close(false);
}
