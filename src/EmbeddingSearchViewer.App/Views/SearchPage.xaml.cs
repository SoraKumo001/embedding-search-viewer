using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer_App.ViewModels;

namespace EmbeddingSearchViewer_App.Views;

public sealed partial class SearchPage : Page
{
    public SearchViewModel ViewModel { get; }

    public SearchPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<SearchViewModel>();
        // ViewModel is a singleton while pages are recreated per navigation, so subscribe only while loaded.
        Loaded += (_, _) => ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
    }

    private async void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.SelectedResult))
        {
            await DisplayResultAsync(ViewModel.SelectedResult);
        }
    }

    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await ViewModel.SearchCommand.ExecuteAsync(null);
        }
    }

    private async void PickQueryImage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.ViewMode = PickerViewMode.Thumbnail;
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".webp");

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            ViewModel.SetQueryImage(file.Path);
        }
    }

    private async void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SearchResultItem? item = null;

        if (e.AddedItems != null && e.AddedItems.Count > 0 && e.AddedItems[0] is SearchResultItem added)
        {
            item = added;
        }
        else if (sender is ListView lv && lv.SelectedItem is SearchResultItem selected)
        {
            item = selected;
        }

        if (item != null)
        {
            ViewModel.SelectedResult = item;
            await DisplayResultAsync(item);
        }
    }

    private SearchResultItem? _currentlyDisplayedItem;

    private async Task DisplayResultAsync(SearchResultItem? item)
    {
        if (item == _currentlyDisplayedItem && item != null)
        {
            return;
        }
        _currentlyDisplayedItem = item;

        if (item == null)
        {
            PdfViewer.Visibility = Visibility.Collapsed;
            VideoViewer.Visibility = Visibility.Collapsed;
            ImageViewer.Visibility = Visibility.Collapsed;
            EmptyPreviewPlaceholder.Visibility = Visibility.Visible;
            VideoViewer.Cleanup();
            return;
        }

        EmptyPreviewPlaceholder.Visibility = Visibility.Collapsed;

        switch (item.Asset.MediaType)
        {
            case MediaType.Pdf:
                VideoViewer.Visibility = Visibility.Collapsed;
                ImageViewer.Visibility = Visibility.Collapsed;
                PdfViewer.Visibility = Visibility.Visible;
                VideoViewer.Cleanup();
                await PdfViewer.LoadItemAsync(item);
                break;

            case MediaType.Video:
                PdfViewer.Visibility = Visibility.Collapsed;
                ImageViewer.Visibility = Visibility.Collapsed;
                VideoViewer.Visibility = Visibility.Visible;
                VideoViewer.LoadItem(item);
                break;

            case MediaType.Image:
                PdfViewer.Visibility = Visibility.Collapsed;
                VideoViewer.Visibility = Visibility.Collapsed;
                ImageViewer.Visibility = Visibility.Visible;
                VideoViewer.Cleanup();
                await ImageViewer.LoadItemAsync(item);
                break;

            default:
                PdfViewer.Visibility = Visibility.Collapsed;
                VideoViewer.Visibility = Visibility.Collapsed;
                ImageViewer.Visibility = Visibility.Collapsed;
                EmptyPreviewPlaceholder.Visibility = Visibility.Visible;
                break;
        }
    }

    private static SearchResultItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as SearchResultItem;

    private static string? PreviewPathOf(SearchResultItem item)
    {
        var path = item.Chunk.PreviewImagePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
        return item.Asset.MediaType == MediaType.Image && File.Exists(item.Asset.FilePath) ? item.Asset.FilePath : null;
    }

    private void ResultMenu_OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && File.Exists(item.Asset.FilePath))
        {
            try { Process.Start(new ProcessStartInfo { FileName = item.Asset.FilePath, UseShellExecute = true }); }
            catch (Exception ex) { ViewModel.ErrorMessage = $"開けませんでした: {ex.Message}"; }
        }
    }

    private void ResultMenu_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && File.Exists(item.Asset.FilePath))
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Asset.FilePath}\"") { UseShellExecute = true }); }
            catch (Exception ex) { ViewModel.ErrorMessage = $"フォルダを開けませんでした: {ex.Message}"; }
        }
    }

    private void ResultMenu_CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(item.Asset.FilePath);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }
    }

    private async void ResultMenu_SimilarSearch_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && PreviewPathOf(item) is { } path)
        {
            ViewModel.SetQueryImage(path);
            await ViewModel.SearchCommand.ExecuteAsync(null);
        }
        else
        {
            ViewModel.ErrorMessage = "この項目には検索に使える画像がありません";
        }
    }

    private static readonly string[] QueryImageExtensions = { ".jpg", ".jpeg", ".png", ".webp" };

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "画像で検索";
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<Windows.Storage.StorageFile>()
            .FirstOrDefault(f => QueryImageExtensions.Contains(Path.GetExtension(f.Path).ToLowerInvariant()));
        if (file == null)
        {
            ViewModel.ErrorMessage = "対応していない形式です (jpg / png / webp)";
            return;
        }

        ViewModel.SetQueryImage(file.Path);
        await ViewModel.SearchCommand.ExecuteAsync(null);
    }

    private void HistoryFlyout_Opening(object? sender, object e)
    {
        HistoryFlyout.Items.Clear();
        var history = ViewModel.History;
        if (history.Count == 0)
        {
            HistoryFlyout.Items.Add(new MenuFlyoutItem { Text = "履歴はありません", IsEnabled = false });
            return;
        }

        foreach (var query in history)
        {
            var item = new MenuFlyoutItem { Text = query };
            item.Click += async (_, _) =>
            {
                ViewModel.QueryText = query;
                ViewModel.ClearQueryImageCommand.Execute(null);
                await ViewModel.SearchCommand.ExecuteAsync(null);
            };
            HistoryFlyout.Items.Add(item);
        }

        HistoryFlyout.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = "履歴を消去" };
        clear.Click += (_, _) => ViewModel.ClearHistory();
        HistoryFlyout.Items.Add(clear);
    }
}
