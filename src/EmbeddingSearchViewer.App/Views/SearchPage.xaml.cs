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
using EmbeddingSearchViewer.Core.Localization;
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
        else if (e.PropertyName == nameof(ViewModel.SelectedScene))
        {
            ShowScene(ViewModel.SelectedScene);
        }
    }

    // The scene the video player was last pointed at (the one shown when the video was opened, or the one clicked since).
    private SearchResultItem? _playerScene;

    /// <summary>Plays the clicked scene of the selected video from its start.</summary>
    private void ShowScene(SearchResultItem? scene)
    {
        if (scene == null || scene.Asset.MediaType != MediaType.Video) return;
        if (scene.Chunk.Id == _playerScene?.Chunk.Id) return; // the scene that was opened with the video: already there

        _playerScene = scene;
        VideoViewer.JumpTo(scene);
    }

    private async void SceneSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await ViewModel.SearchInVideoCommand.ExecuteAsync(null);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only what the user types gets suggestions; text set by the program (a history entry, a new profile) does not.
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        ViewModel.QueryText = sender.Text;
        sender.ItemsSource = ViewModel.SuggestHistory(sender.Text);
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string text) ViewModel.QueryText = text;
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is string text) ViewModel.QueryText = text;
        await ViewModel.SearchCommand.ExecuteAsync(null);
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
                _playerScene = item;
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
            catch (Exception ex) { ViewModel.ErrorMessage = Loc.T("Search.OpenFailed", ex.Message); }
        }
    }

    private void ResultMenu_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && !EmbeddingSearchViewer_App.Services.ExplorerLauncher.Reveal(item.Asset.FilePath))
        {
            ViewModel.ErrorMessage = Loc.T("Search.OpenFolderFailed", item.Asset.FilePath);
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
            ViewModel.ErrorMessage = Loc.T("Search.NoQueryImage");
        }
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.T("Search.Drop.SearchByImage");
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<Windows.Storage.StorageFile>()
            .FirstOrDefault(f => SupportedMediaTypes.ImageExtensions.Contains(Path.GetExtension(f.Path)));
        if (file == null)
        {
            ViewModel.ErrorMessage = Loc.T("Search.UnsupportedImage");
            return;
        }

        ViewModel.SetQueryImage(file.Path);
        await ViewModel.SearchCommand.ExecuteAsync(null);
    }

    private void HistoryFlyout_Opening(object? sender, object e) => BuildHistoryMenu();

    /// <summary>
    /// Click runs the entry again; right-click offers pin and delete. The menu is built again after each change,
    /// because the pinned entries move to the top.
    /// </summary>
    private void BuildHistoryMenu()
    {
        HistoryFlyout.Items.Clear();
        var history = ViewModel.History;
        if (history.Count == 0)
        {
            HistoryFlyout.Items.Add(new MenuFlyoutItem { Text = Loc.T("Search.History.Empty"), IsEnabled = false });
            return;
        }

        foreach (var entry in history)
        {
            var item = new MenuFlyoutItem
            {
                Text = entry.DisplayLabel,
                Icon = entry.Pinned ? new FontIcon { Glyph = "" } : null
            };
            item.Click += async (_, _) => await ViewModel.ApplyHistoryAsync(entry);

            var context = new MenuFlyout();
            var pin = new MenuFlyoutItem { Text = entry.Pinned ? Loc.T("Search.History.Unpin") : Loc.T("Search.History.Pin") };
            pin.Click += (_, _) =>
            {
                if (!ViewModel.SetHistoryPinned(entry.Id, !entry.Pinned)) ViewModel.ErrorMessage = Loc.T("Search.History.PinLimit", EmbeddingSearchViewer.Infrastructure.Library.SearchHistoryStore.MaxPinned);
                BuildHistoryMenu();
            };
            var delete = new MenuFlyoutItem { Text = Loc.T("Search.History.Delete") };
            delete.Click += (_, _) =>
            {
                ViewModel.RemoveHistory(entry.Id);
                BuildHistoryMenu();
            };
            context.Items.Add(pin);
            context.Items.Add(delete);
            item.ContextFlyout = context;

            HistoryFlyout.Items.Add(item);
        }

        HistoryFlyout.Items.Add(new MenuFlyoutSeparator());
        var clear = new MenuFlyoutItem { Text = Loc.T("Search.History.Clear") };
        clear.Click += (_, _) => ViewModel.ClearHistory();
        HistoryFlyout.Items.Add(clear);

        if (history.Any(h => h.Pinned))
        {
            var clearAll = new MenuFlyoutItem { Text = Loc.T("Search.History.ClearAll") };
            clearAll.Click += (_, _) => ViewModel.ClearHistory(includePinned: true);
            HistoryFlyout.Items.Add(clearAll);
        }
    }
}
