using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ShanaPress.Desktop.Views;

public partial class InputPromptWindow : Window
{
    public string ResponseText { get; private set; } = string.Empty;

    public InputPromptWindow()
    {
        InitializeComponent();
    }

    public InputPromptWindow(string title, string message, string defaultValue = "") : this()
    {
        Title = title;
        MessageTextBlock.Text = message;
        InputTextBox.Text = defaultValue;
        Opened += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        ResponseText = InputTextBox.Text ?? string.Empty;
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
