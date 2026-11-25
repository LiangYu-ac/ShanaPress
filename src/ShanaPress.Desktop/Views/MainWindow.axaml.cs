using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ShanaPress.Core.Models;
using ShanaPress.Desktop.ViewModels;

namespace ShanaPress.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        LogTextBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
            {
                LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
            }
        };
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var paths = files
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Cast<string>()
            .ToList();

        if (paths.Count > 0)
        {
            await vm.AddPathsAsync(paths);
        }
    }

    private void QueueGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            var selected = QueueGrid.SelectedItems.OfType<QueueItem>();
            vm.UpdateSelectedItems(selected);
        }
    }

    private void QueueGrid_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SelectedQueueItem is not null && vm.CanEditSettings)
        {
            vm.OpenTrimCropCommand.Execute(null);
        }
    }
}
