using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer_App.ViewModels;

namespace EmbeddingSearchViewer_App.Views;

public sealed partial class LibraryPage : Page
{
    public LibraryViewModel ViewModel { get; }

    /// <summary>True while the page is starting up, so a second Loaded does not start the same work again.</summary>
    private bool _initializing;

    public LibraryPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<LibraryViewModel>();
        ViewModel.SetDispatcherQueue(DispatcherQueue);
        Loaded += OnLoaded;
    }

    /// <summary>
    /// The page is kept when the user goes to another page (NavigationCacheMode="Required"), so it is loaded again on
    /// every visit. The view model knows what is already done: the first visit does the whole start-up, a later one
    /// only refreshes the light numbers.
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initializing) return; // the start-up of the previous visit is still running
        _initializing = true;
        try
        {
            // --auto-index: start as soon as folders and the engine are known; the full statistics
            // refresh (slow on huge libraries) follows while indexing is already running.
            if (App.AutoStartIndexing)
            {
                await ViewModel.InitializeCoreAsync();
                _ = ViewModel.StartIndexingAsync();
                ViewModel.ScheduleFullStatsRefresh();
            }
            else await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            // An exception here would end the app; the page is still usable without the numbers.
            EmbeddingSearchViewer_App.Services.CrashLog.Write("LibraryPage.Loaded", ex);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void RestoreExcluded_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmbeddingSearchViewer.Infrastructure.Library.ExcludedFile file)
        {
            ViewModel.RemoveExclusion(file);
        }
    }

    // The view model outlives the page; without clearing it, a dismissed error would reappear on return.
    private void ErrorInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel.ErrorMessage = null;
    }

    private void RevealExcluded_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EmbeddingSearchViewer.Infrastructure.Library.ExcludedFile file)
        {
            EmbeddingSearchViewer_App.Services.ExplorerLauncher.Reveal(file.Path);
        }
    }

    private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            ViewModel.AddFolder(folder.Path);
        }
    }

    private void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is EmbeddingSearchViewer_App.ViewModels.FolderEntry folder)
        {
            ViewModel.RemoveFolder(folder.Path);
        }
    }

    private void Folders_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.T("Library.Drop.AddFolder");
        }
    }

    private async void Folders_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        foreach (var folder in items.OfType<Windows.Storage.StorageFolder>())
        {
            ViewModel.AddFolder(folder.Path);
        }
    }

    private async void ClearIndexButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Library.ClearDialog.Title"),
            Content = Loc.T("Library.ClearDialog.Body"),
            PrimaryButtonText = Loc.T("Common.Clear"),
            CloseButtonText = Loc.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.ClearIndexCommand.ExecuteAsync(null);
        }
    }
}
