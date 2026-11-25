using System.Windows;
using System.Windows.Controls;
using ShanaPress.App.ViewModels;

namespace ShanaPress.App.Views;

public partial class TrimCropWindow : Window
{
    public TrimCropWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TrimCropViewModel vm)
            {
                vm.CropVisualChanged += UpdateCropOverlay;
                UpdateCropOverlay();
            }
        };
    }

    private void PreviewImageControl_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateCropOverlay();

    private void UpdateCropOverlay()
    {
        if (DataContext is not TrimCropViewModel vm) return;

        var imgW = PreviewImageControl.ActualWidth;
        var imgH = PreviewImageControl.ActualHeight;
        CropOverlayCanvas.Width = imgW;
        CropOverlayCanvas.Height = imgH;

        if (!vm.CropEnabled || imgW <= 1 || imgH <= 1 || vm.SourceWidth <= 0 || vm.SourceHeight <= 0)
        {
            MaskTop.Visibility = Visibility.Collapsed;
            MaskBottom.Visibility = Visibility.Collapsed;
            MaskLeft.Visibility = Visibility.Collapsed;
            MaskRight.Visibility = Visibility.Collapsed;
            CropFrameBorder.Visibility = Visibility.Collapsed;
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

        MaskTop.Visibility = Visibility.Visible;
        MaskBottom.Visibility = Visibility.Visible;
        MaskLeft.Visibility = Visibility.Visible;
        MaskRight.Visibility = Visibility.Visible;
        CropFrameBorder.Visibility = Visibility.Visible;

        // Top mask across full width
        Canvas.SetLeft(MaskTop, 0);
        Canvas.SetTop(MaskTop, 0);
        MaskTop.Width = imgW;
        MaskTop.Height = topPx;

        // Bottom mask across full width
        Canvas.SetLeft(MaskBottom, 0);
        Canvas.SetTop(MaskBottom, topPx + centerH);
        MaskBottom.Width = imgW;
        MaskBottom.Height = bottomPx;

        // Left mask between top and bottom
        Canvas.SetLeft(MaskLeft, 0);
        Canvas.SetTop(MaskLeft, topPx);
        MaskLeft.Width = leftPx;
        MaskLeft.Height = centerH;

        // Right mask between top and bottom
        Canvas.SetLeft(MaskRight, leftPx + centerW);
        Canvas.SetTop(MaskRight, topPx);
        MaskRight.Width = rightPx;
        MaskRight.Height = centerH;

        // Active crop border
        Canvas.SetLeft(CropFrameBorder, leftPx);
        Canvas.SetTop(CropFrameBorder, topPx);
        CropFrameBorder.Width = centerW;
        CropFrameBorder.Height = centerH;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is TrimCropViewModel vm)
        {
            vm.ApplyToItem();
        }
        DialogResult = true;
    }
}
