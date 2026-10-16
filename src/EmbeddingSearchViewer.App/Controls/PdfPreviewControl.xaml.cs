using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer_App.Controls;

public sealed partial class PdfPreviewControl : UserControl
{
    private SearchResultItem? _currentItem;
    private PdfDocument? _pdfDoc;
    private int _currentPage = 1;
    private int _totalPages = 1;

    public PdfPreviewControl()
    {
        InitializeComponent();
    }

    public async Task LoadItemAsync(SearchResultItem item)
    {
        _currentItem = item;
        SnippetTextBox.Text = item.Chunk.SnippetText ?? Loc.T("PdfPreview.NoText");

        if (!File.Exists(item.Asset.FilePath))
        {
            PageInfoTextBlock.Text = Loc.T("Common.FileNotFound");
            PageImageControl.Source = null;
            return;
        }

        _currentPage = item.Chunk.PageNumber ?? 1;
        _totalPages = item.Asset.DurationOrPages ?? 1;

        // If preview image is already cached on disk, display it directly
        if (!string.IsNullOrEmpty(item.Chunk.PreviewImagePath) && File.Exists(item.Chunk.PreviewImagePath))
        {
            PageImageControl.Source = new BitmapImage(new Uri(item.Chunk.PreviewImagePath));
            UpdatePageInfo();
            return;
        }

        // Otherwise render via Windows.Data.Pdf
        await RenderCurrentPageAsync();
    }

    private async Task RenderCurrentPageAsync()
    {
        if (_currentItem == null || !File.Exists(_currentItem.Asset.FilePath)) return;

        LoadingRing.IsActive = true;
        try
        {
            if (_pdfDoc == null)
            {
                var file = await StorageFile.GetFileFromPathAsync(_currentItem.Asset.FilePath);
                _pdfDoc = await PdfDocument.LoadFromFileAsync(file);
                _totalPages = (int)_pdfDoc.PageCount;
            }

            if (_currentPage < 1) _currentPage = 1;
            if (_currentPage > _totalPages) _currentPage = _totalPages;

            uint pageIndex = (uint)(_currentPage - 1);
            using var page = _pdfDoc.GetPage(pageIndex);
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream);

            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            PageImageControl.Source = bitmap;

            UpdatePageInfo();
        }
        catch (Exception ex)
        {
            PageInfoTextBlock.Text = Loc.T("PdfPreview.RenderFailed", ex.Message);
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    private void UpdatePageInfo()
    {
        PageInfoTextBlock.Text = Loc.T("PdfPreview.Page", _currentPage, _totalPages);
        PrevPageButton.IsEnabled = _currentPage > 1;
        NextPageButton.IsEnabled = _currentPage < _totalPages;
    }

    private async void PrevPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage > 1)
        {
            _currentPage--;
            await RenderCurrentPageAsync();
        }
    }

    private async void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPage < _totalPages)
        {
            _currentPage++;
            await RenderCurrentPageAsync();
        }
    }

    private void OpenPdfExternalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentItem != null && File.Exists(_currentItem.Asset.FilePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _currentItem.Asset.FilePath,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
