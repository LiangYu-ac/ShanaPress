using System.Windows;

namespace ShanaPress.App.Views;

public partial class InputPromptWindow : Window
{
    public string ResponseText { get; private set; } = string.Empty;

    public InputPromptWindow(string title, string message, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        MessageTextBlock.Text = message;
        InputTextBox.Text = defaultValue;
        Loaded += (_, _) =>
        {
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        ResponseText = InputTextBox.Text;
        DialogResult = true;
    }
}
