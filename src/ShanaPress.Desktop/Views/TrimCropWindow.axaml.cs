using Avalonia.Controls;
using Avalonia.Interactivity;
using ShanaPress.Desktop.ViewModels;

namespace ShanaPress.Desktop.Views;

public partial class TrimCropWindow : Window
{
    public TrimCropWindow()
    {
        InitializeComponent();
        PreviewImageControl.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name is nameof(Bounds) or nameof(Image.Source))
            {
                UpdateCropOverlay();
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TrimCropViewModel vm)
            {
                vm.DialogOwner = this;
                vm.CropVisualChanged += UpdateCropOverlay;
                UpdateCropOverlay();
            }
        };
    }

    private void UpdateCropOverlay()
    {
        if (DataContext is not TrimCropViewModel vm) return;

        var imgW = PreviewImageControl.Bounds.Width;
        var imgH = PreviewImageControl.Bounds.Height;
        CropOverlayCanvas.Width = imgW;
        CropOverlayCanvas.Height = imgH;

        if (!vm.CropEnabled || imgW <= 1 || imgH <= 1 || vm.SourceWidth <= 0 || vm.SourceHeight <= 0)
        {
            MaskTop.IsVisible = false;
            MaskBottom.IsVisible = false;
            MaskLeft.IsVisible = false;
            MaskRight.IsVisible = false;
            CropFrameBorder.IsVisible = false;
            return;
        }

        var scaleX = imgW / vm.SourceWidth;
        var scaleY = imgH / vm.SourceHeight;

        var leftPx = Math.Clamp(vm.CropLeft * scaleX, 0, imgW);
        var topPx = Math.Clamp(vm.CropTop * scaleY, 0, imgH);
        var rightPx = Math.Clamp(vm.CropRight * scaleX, 0, imgW - leftPx);
        var bottomPx = Math.Clamp(vm.CropBottom * scaleY, 0, imgH - topPx);

        var centerW = Math.Max(0, imgW - leftPx - rightPx);
        var centerH = Math.Max(0, imgH - topPx - bottomPx);

        MaskTop.IsVisible = true;
        MaskBottom.IsVisible = true;
        MaskLeft.IsVisible = true;
        MaskRight.IsVisible = true;
        CropFrameBorder.IsVisible = true;

        Canvas.SetLeft(MaskTop, 0);
        Canvas.SetTop(MaskTop, 0);
        MaskTop.Width = imgW;
        MaskTop.Height = topPx;

        Canvas.SetLeft(MaskBottom, 0);
        Canvas.SetTop(MaskBottom, topPx + centerH);
        MaskBottom.Width = imgW;
        MaskBottom.Height = bottomPx;

        Canvas.SetLeft(MaskLeft, 0);
        Canvas.SetTop(MaskLeft, topPx);
        MaskLeft.Width = leftPx;
        MaskLeft.Height = centerH;

        Canvas.SetLeft(MaskRight, leftPx + centerW);
        Canvas.SetTop(MaskRight, topPx);
        MaskRight.Width = rightPx;
        MaskRight.Height = centerH;

        Canvas.SetLeft(CropFrameBorder, leftPx);
        Canvas.SetTop(CropFrameBorder, topPx);
        CropFrameBorder.Width = centerW;
        CropFrameBorder.Height = centerH;
    }

    private void Apply_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TrimCropViewModel vm)
        {
            vm.ApplyToItem();
        }
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
