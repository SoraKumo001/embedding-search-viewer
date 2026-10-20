using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using EmbeddingSearchViewer.Infrastructure.Profiles;
using EmbeddingSearchViewer_App.Services;
using EmbeddingSearchViewer_App.ViewModels;

namespace EmbeddingSearchViewer_App.Views;

public sealed partial class ShellPage : Page
{
    private static readonly Dictionary<string, Type> _pages = new()
    {
        { "SearchPage", typeof(SearchPage) },
        { "LibraryPage", typeof(LibraryPage) },
        { "ModelSetupPage", typeof(ModelSetupPage) },
        { "SettingsPage", typeof(SettingsPage) },
    };

    private readonly ProfileViewModel _profiles;
    private bool _syncingProfiles;

    public ShellPage()
    {
        InitializeComponent();
        var navService = App.Services.GetRequiredService<INavigationService>();
        navService.SetFrame(ContentFrame);

        _profiles = App.Services.GetRequiredService<ProfileViewModel>();
        ProfileCombo.ItemsSource = _profiles.Profiles;
        _profiles.Profiles.CollectionChanged += (_, _) => SyncProfileCombo();
        _profiles.PropertyChanged += (_, _) => SyncProfileCombo();
        SyncProfileCombo();

        // Switching under a running index job would write into the wrong database, so it is locked meanwhile.
        var library = App.Services.GetRequiredService<LibraryViewModel>();
        library.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsIndexing))
            {
                ProfileCombo.IsEnabled = !library.IsIndexing;
                ProfileMenuButton.IsEnabled = !library.IsIndexing;
            }
        };
    }

    private void SyncProfileCombo()
    {
        _syncingProfiles = true;
        try
        {
            ProfileCombo.SelectedItem = _profiles.Profiles.FirstOrDefault(p => p.Id == _profiles.Current.Id);
        }
        finally
        {
            _syncingProfiles = false;
        }
    }

    private async void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingProfiles || ProfileCombo.SelectedItem is not ProfileInfo selected) return;
        if (selected.Id == _profiles.Current.Id) return;

        if (!await _profiles.SwitchAsync(selected.Id))
        {
            SyncProfileCombo();
            await ShowMessageAsync(Loc.T("Shell.SwitchFailed.Title"), _profiles.ErrorMessage);
        }
    }

    private async void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = await AskNameAsync(Loc.T("Shell.New.Title"), Loc.T("Shell.New.Header"), string.Empty, Loc.T("Shell.New.Primary"));
        if (name == null) return;

        if (!await _profiles.CreateAsync(name))
        {
            await ShowMessageAsync(Loc.T("Shell.CreateFailed.Title"), _profiles.ErrorMessage);
        }
    }

    private async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = await AskNameAsync(Loc.T("Shell.Rename.Title"), Loc.T("Shell.Rename.Header"), _profiles.Current.Name, Loc.T("Shell.Rename.Primary"));
        if (name == null) return;

        if (!_profiles.RenameCurrent(name))
        {
            await ShowMessageAsync(Loc.T("Shell.RenameFailed.Title"), _profiles.ErrorMessage);
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var others = _profiles.Profiles.Where(p => p.Id != _profiles.Current.Id).ToList();
        if (others.Count == 0)
        {
            await ShowMessageAsync(Loc.T("Shell.NothingToDelete.Title"), Loc.T("Shell.NothingToDelete.Body"));
            return;
        }

        var picker = new ComboBox { ItemsSource = others, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 240 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Shell.Delete.Body"),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(picker);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Shell.Delete.Title"),
            Content = panel,
            PrimaryButtonText = Loc.T("Shell.Delete.Primary"),
            CloseButtonText = Loc.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || picker.SelectedItem is not ProfileInfo target) return;

        if (!_profiles.Delete(target.Id))
        {
            await ShowMessageAsync(Loc.T("Shell.DeleteFailed.Title"), _profiles.ErrorMessage);
        }
    }

    // ---- backup and restore ----

    private async void BackupCurrentProfile_Click(object sender, RoutedEventArgs e)
    {
        var (confirmed, includeCache) = await AskBackupOptionsAsync(
            Loc.T("Backup.Confirm.CurrentBody", _profiles.Current.Name));
        if (!confirmed) return;

        var destination = await PickBackupDestinationAsync($"{_profiles.Current.Name}.esvbackup.zip");
        if (destination is null) return;

        await RunBackupAsync(destination, includeCache, allProfiles: false);
    }

    private async void BackupAllProfiles_Click(object sender, RoutedEventArgs e)
    {
        var (confirmed, includeCache) = await AskBackupOptionsAsync(Loc.T("Backup.Confirm.AllBody"));
        if (!confirmed) return;

        var destination = await PickBackupDestinationAsync("embedding-search-backup.esvbackup.zip");
        if (destination is null) return;

        await RunBackupAsync(destination, includeCache, allProfiles: true);
    }

    private async void RestoreProfile_Click(object sender, RoutedEventArgs e)
    {
        var zipPath = await PickRestoreArchiveAsync();
        if (zipPath is null) return;

        var preview = await _profiles.PreviewRestoreAsync(zipPath);
        if (preview is null)
        {
            await ShowMessageAsync(Loc.T("Restore.Failed.Title"), TakeErrorMessage());
            return;
        }

        if (!await ConfirmRestoreAsync(preview)) return;

        var status = new TextBlock { Text = Loc.T("Restore.Progress.Title"), TextWrapping = TextWrapping.Wrap };
        await ShowProgressWhileAsync(Loc.T("Restore.Progress.Title"), status,
            () => _profiles.RestoreSingleCommand.ExecuteAsync(new ProfileRestoreRequest(zipPath)));

        if (_profiles.ErrorMessage is null)
        {
            await ShowMessageAsync(Loc.T("Restore.Done.Title"), Loc.T("Restore.Done.Body", preview.ProfileName));
        }
        else
        {
            await ShowMessageAsync(Loc.T("Restore.Failed.Title"), TakeErrorMessage());
        }
    }

    private async void RestoreAllProfiles_Click(object sender, RoutedEventArgs e)
    {
        var zipPath = await PickRestoreArchiveAsync();
        if (zipPath is null) return;

        var preview = await _profiles.PreviewRestoreAllAsync(zipPath);
        if (preview is null)
        {
            await ShowMessageAsync(Loc.T("Restore.Failed.Title"), TakeErrorMessage());
            return;
        }

        if (!await ConfirmRestoreAllAsync(preview)) return;

        var status = new TextBlock { Text = Loc.T("Restore.Progress.Title"), TextWrapping = TextWrapping.Wrap };
        await ShowProgressWhileAsync(Loc.T("Restore.Progress.Title"), status,
            () => _profiles.RestoreAllCommand.ExecuteAsync(new ProfileRestoreRequest(zipPath)));

        if (_profiles.ErrorMessage is null)
        {
            await ShowMessageAsync(Loc.T("Restore.Done.Title"), Loc.T("Restore.Done.Body", ProfileNames(preview)));
        }
        else
        {
            await ShowMessageAsync(Loc.T("Restore.Failed.Title"), TakeErrorMessage());
        }
    }

    private async Task RunBackupAsync(string destination, bool includeCache, bool allProfiles)
    {
        var status = new TextBlock { Text = Loc.T("Backup.Stage.Start"), TextWrapping = TextWrapping.Wrap };
        var progress = new Progress<ProfileBackupProgress>(p => status.Text = BackupStageText(p.Stage));

        await ShowProgressWhileAsync(Loc.T("Backup.Progress.Title"), status, async () =>
        {
            var request = new ProfileBackupRequest(destination, includeCache, progress);
            if (allProfiles) await _profiles.BackupAllCommand.ExecuteAsync(request);
            else await _profiles.BackupCurrentCommand.ExecuteAsync(request);
        });

        if (_profiles.ErrorMessage is null)
        {
            await ShowMessageAsync(Loc.T("Backup.Done.Title"), Loc.T("Backup.Done.Body", destination));
        }
        else
        {
            await ShowMessageAsync(Loc.T("Backup.Failed.Title"), TakeErrorMessage());
        }
    }

    /// <summary>Asks whether to save the profile(s), and whether the cache goes in too; null-free tuple so both are known.</summary>
    private async Task<(bool Confirmed, bool IncludeCache)> AskBackupOptionsAsync(string body)
    {
        var cache = new CheckBox { Content = Loc.T("Backup.IncludeCache") };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(cache);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Backup.Confirm.Title"),
            Content = panel,
            PrimaryButtonText = Loc.T("Backup.Confirm.Primary"),
            CloseButtonText = Loc.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        return (await dialog.ShowAsync() == ContentDialogResult.Primary, cache.IsChecked == true);
    }

    /// <summary>Shows a dialog that cannot be dismissed while <paramref name="work"/> runs, then closes it.</summary>
    private async Task ShowProgressWhileAsync(string title, TextBlock status, Func<Task> work)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32 });
        panel.Children.Add(status);

        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = panel };
        var showing = dialog.ShowAsync();
        try
        {
            await work();
        }
        finally
        {
            dialog.Hide();
        }

        try { await showing; }
        catch (Exception ex) { CrashLog.Write("ProgressDialog", ex); }
    }

    /// <summary>Shows what the archive holds and warns when it was built with another model, before anything is replaced.</summary>
    private async Task<bool> ConfirmRestoreAsync(RestorePreview preview)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Confirm.Body", preview.ProfileName), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Preview.Created", preview.CreatedAtUtc.ToLocalTime().ToString("g", Loc.Culture)), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Preview.AppVersion", preview.AppVersion), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Preview.Assets", preview.AssetCount), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Preview.Size", FormatBytes(preview.TotalBytes)), TextWrapping = TextWrapping.Wrap });

        if (ModelWarningText(preview) is { } warning)
        {
            panel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Message = warning
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Restore.Confirm.Title"),
            Content = panel,
            PrimaryButtonText = Loc.T("Restore.Confirm.Primary"),
            CloseButtonText = Loc.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Shows what the all-profiles archive holds and warns for any profile built with another model.</summary>
    private async Task<bool> ConfirmRestoreAllAsync(AllRestorePreview preview)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Confirm.Body", ProfileNames(preview)), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Preview.Created", preview.CreatedAtUtc?.ToLocalTime().ToString("g", Loc.Culture) ?? string.Empty), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Preview.AppVersion", preview.Profiles.FirstOrDefault()?.AppVersion ?? string.Empty), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Preview.Assets", preview.Profiles.Sum(p => p.AssetCount)), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.T("Restore.Preview.Size", FormatBytes(preview.TotalBytes)), TextWrapping = TextWrapping.Wrap });

        foreach (var warning in preview.Profiles.Select(ModelWarningText).OfType<string>().Distinct())
        {
            panel.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning,
                IsOpen = true,
                IsClosable = false,
                Message = warning
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Loc.T("Restore.Confirm.Title"),
            Content = panel,
            PrimaryButtonText = Loc.T("Restore.Confirm.Primary"),
            CloseButtonText = Loc.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>The archive's profile names, for the confirmation and done dialogs.</summary>
    private static string ProfileNames(AllRestorePreview preview) =>
        string.Join(", ", preview.Profiles.Select(p => p.ProfileName));

    private async Task<string?> PickBackupDestinationAsync(string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName
        };
        picker.FileTypeChoices.Add(Loc.T("Backup.FileType"), new List<string> { ".zip" });

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private async Task<string?> PickRestoreArchiveAsync()
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".zip");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Takes the error once, so a dialog shown later does not repeat it.</summary>
    private string TakeErrorMessage()
    {
        string message = _profiles.ErrorMessage ?? string.Empty;
        _profiles.ErrorMessage = null;
        return message;
    }

    /// <summary>Maps a stage token from <see cref="ProfileBackupService"/> to a short line for the progress dialog.</summary>
    private static string BackupStageText(string stage) => stage switch
    {
        "start" => Loc.T("Backup.Stage.Start"),
        "zip" => Loc.T("Backup.Stage.Zip"),
        "verify" => Loc.T("Backup.Stage.Verify"),
        _ when stage.StartsWith("staged", StringComparison.Ordinal) => Loc.T("Backup.Stage.Staging"),
        _ => Loc.T("Backup.Stage.Working")
    };

    /// <summary>The warning when the archive was built with a model other than the one in use, else null.</summary>
    private static string? ModelWarningText(RestorePreview preview)
    {
        string current = EmbeddingModelLocation.ModelTag(EmbeddingModelLocation.DefaultPrecision);
        if (string.IsNullOrWhiteSpace(preview.ModelTag) ||
            string.Equals(preview.ModelTag, current, StringComparison.Ordinal))
        {
            return null;
        }

        return Loc.T("Restore.ModelWarning", preview.ModelTag, current);
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:N0} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>Asks for a profile name; null when cancelled.</summary>
    private async System.Threading.Tasks.Task<string?> AskNameAsync(string title, string header, string initial, string primary)
    {
        var box = new TextBox { Header = header, Text = initial, MinWidth = 280, MaxLength = 40 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = Loc.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = initial.Trim().Length > 0
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    private async System.Threading.Tasks.Task ShowMessageAsync(string title, string? message)
    {
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message ?? string.Empty, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Loc.T("Common.Close")
        }.ShowAsync();
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigated += ContentFrame_Navigated;
        // Without the model nothing can be searched or indexed, so start on the setup screen. The check is a light look
        // at the model folder: it does not wait for the engine (which is created in the background) and does not depend
        // on a session being there.
        var host = App.Services.GetRequiredService<EmbeddingSearchViewer.Infrastructure.Onnx.EmbeddingServiceHost>();
        // --auto-index: open the library page so indexing starts without a button press.
        Navigate(host.ModelFilesMissing ? "ModelSetupPage" : App.AutoStartIndexing ? "LibraryPage" : "SearchPage");
    }

    private void NavView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.IsSettingsInvoked)
        {
            Navigate("SettingsPage");
            return;
        }

        if (args.InvokedItemContainer?.Tag is string tag)
        {
            Navigate(tag);
        }
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        var pageType = e.SourcePageType;
        if (pageType == typeof(SettingsPage))
        {
            NavView.SelectedItem = NavView.SettingsItem;
            return;
        }

        var tag = _pages.FirstOrDefault(p => p.Value == pageType).Key;
        var allItems = NavView.MenuItems.OfType<NavigationViewItem>();
        NavView.SelectedItem = allItems.FirstOrDefault(i => i.Tag?.ToString() == tag);
    }

    private void Navigate(string tag)
    {
        if (_pages.TryGetValue(tag, out var pageType) &&
            ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }
    }
}
