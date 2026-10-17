using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer_App.Controls;

public sealed partial class ImagePreviewControl : UserControl
{
    private SearchResultItem? _currentItem;
    private int _loadVersion;

    public ImagePreviewControl()
    {
        InitializeComponent();
    }

    public async void LoadItem(SearchResultItem item)
    {
        await LoadItemAsync(item);
    }

    public async Task LoadItemAsync(SearchResultItem item)
    {
        _currentItem = item;
        int version = ++_loadVersion;
        ResetZoom();
        string? targetPath = item.Chunk.PreviewImagePath ?? item.Asset.FilePath;

        if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
        {
            ImageTitleTextBlock.Text = item.Asset.FileName ?? Loc.T("ImagePreview.FileNotFoundTitle");
            ImageViewerControl.Source = null;
            MetadataTextBlock.Text = Loc.T("Common.FileNotFound");
            return;
        }

        ImageTitleTextBlock.Text = item.Asset.FileName;

        try
        {
            LoadingRing.IsActive = true;

            // Load file via Windows Storage API for rock-solid WinUI 3 rendering
            var file = await StorageFile.GetFileFromPathAsync(targetPath);
            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream);
            if (version != _loadVersion) return; // a newer selection superseded this load
            ImageViewerControl.Source = bmp;

            var fi = new FileInfo(targetPath);
            MetadataTextBlock.Text = Describe(targetPath, bmp, fi);
        }
        catch (Exception ex) when (version == _loadVersion)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load image via StorageFile: {ex.Message}");
            // Fallback via memory stream copy
            try
            {
                using var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                await fs.CopyToAsync(ms);
                ms.Position = 0;
                var ras = ms.AsRandomAccessStream();
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ras);
                if (version != _loadVersion) return;
                ImageViewerControl.Source = bmp;

                var fi = new FileInfo(targetPath);
                MetadataTextBlock.Text = Describe(targetPath, bmp, fi);
            }
            catch (Exception exFallback)
            {
                System.Diagnostics.Debug.WriteLine($"Fallback load failed: {exFallback.Message}");
                ImageViewerControl.Source = null;
                MetadataTextBlock.Text = Loc.T("ImagePreview.LoadError", targetPath);
            }
        }
        finally
        {
            if (version == _loadVersion) LoadingRing.IsActive = false;
        }
    }

    /// <summary>"path (1,024 × 768 px, 23 KB) - last modified ..." for the footer that shows on hover.</summary>
    private static string Describe(string path, BitmapImage bitmap, FileInfo info)
        => Loc.T("ImagePreview.Describe", path, bitmap.PixelWidth, bitmap.PixelHeight, info.Length / 1024, info.LastWriteTime);

    // The header and footer are only shown while the pointer is over the preview or one of the buttons has the focus.
    private bool _pointerOver;
    private bool _overlayFocused;

    private void UpdateOverlays()
    {
        bool show = _pointerOver || _overlayFocused;
        HeaderOverlay.Opacity = show ? 1 : 0;
        HeaderOverlay.IsHitTestVisible = show; // hidden, the header must not catch clicks meant for the image
        FooterOverlay.Opacity = show ? 1 : 0;
    }

    private void RootGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        UpdateOverlays();
    }

    private void RootGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _pointerOver = false;
        UpdateOverlays();
    }

    private void Overlay_GotFocus(object sender, RoutedEventArgs e)
    {
        _overlayFocused = true;
        UpdateOverlays();
    }

    private void Overlay_LostFocus(object sender, RoutedEventArgs e)
    {
        _overlayFocused = false;
        UpdateOverlays();
    }

    private const float WheelZoomStep = 1.2f;
    private const float DoubleTapZoom = 2.5f;
    private bool _isPanning;
    private Windows.Foundation.Point _lastPanPoint;

    private void ImageScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ImageHost.Width = ImageScroller.ActualWidth;
        ImageHost.Height = ImageScroller.ActualHeight;
    }

    private void ResetZoom() => ImageScroller.ChangeView(0, 0, 1f, true);

    private void ImageHost_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ImageScroller);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        float oldZoom = ImageScroller.ZoomFactor;
        float newZoom = Math.Clamp(delta > 0 ? oldZoom * WheelZoomStep : oldZoom / WheelZoomStep,
            ImageScroller.MinZoomFactor, ImageScroller.MaxZoomFactor);
        double ratio = newZoom / oldZoom;

        // Keep the point under the cursor fixed while zooming
        double offsetX = (ImageScroller.HorizontalOffset + point.Position.X) * ratio - point.Position.X;
        double offsetY = (ImageScroller.VerticalOffset + point.Position.Y) * ratio - point.Position.Y;
        ImageScroller.ChangeView(offsetX, offsetY, newZoom, true);
        e.Handled = true;
    }

    private void ImageHost_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (ImageScroller.ZoomFactor <= 1.001f) return;
        var point = e.GetCurrentPoint(ImageScroller);
        if (!point.Properties.IsLeftButtonPressed) return;
        _isPanning = true;
        _lastPanPoint = point.Position;
        ImageHost.CapturePointer(e.Pointer);
    }

    private void ImageHost_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetCurrentPoint(ImageScroller).Position;
        ImageScroller.ChangeView(
            ImageScroller.HorizontalOffset - (pos.X - _lastPanPoint.X),
            ImageScroller.VerticalOffset - (pos.Y - _lastPanPoint.Y),
            null, true);
        _lastPanPoint = pos;
    }

    private void ImageHost_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isPanning) return;
        _isPanning = false;
        ImageHost.ReleasePointerCaptures();
    }

    private void ImageHost_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (ImageScroller.ZoomFactor > 1.001f)
        {
            ResetZoom();
            return;
        }

        var pos = e.GetPosition(ImageScroller);
        ImageScroller.ChangeView(
            pos.X * DoubleTapZoom - pos.X,
            pos.Y * DoubleTapZoom - pos.Y,
            DoubleTapZoom, true);
    }

    /// <summary>Opens Explorer with the file selected (the folder alone when the file is gone).</summary>
    private void OpenInExplorerButton_Click(object sender, RoutedEventArgs e)
    {
        string? path = _currentItem?.Asset.FilePath;
        if (string.IsNullOrEmpty(path)) path = _currentItem?.Chunk.PreviewImagePath;
        if (string.IsNullOrEmpty(path)) return;

        EmbeddingSearchViewer_App.Services.ExplorerLauncher.Reveal(path);
    }

    private void OpenImageExternalButton_Click(object sender, RoutedEventArgs e)
    {
        string? targetPath = _currentItem?.Chunk.PreviewImagePath ?? _currentItem?.Asset.FilePath;
        if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
