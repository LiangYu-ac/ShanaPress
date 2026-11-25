using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShanaPress.App.ViewModels;
using ShanaPress.Core.Models;

namespace ShanaPress.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            DataContext is MainViewModel vm)
        {
            await vm.AddPathsAsync(paths);
        }
    }

    private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var selected = QueueGrid.SelectedItems.OfType<QueueItem>();
            vm.UpdateSelectedItems(selected);
        }
    }

    private void QueueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedQueueItem is not null && vm.CanEditSettings)
        {
            vm.OpenTrimCropCommand.Execute(null);
        }
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogTextBox.ScrollToEnd();
    }
}
