using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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
            await ShowMessageAsync("プロファイルを切り替えられません", _profiles.ErrorMessage);
        }
    }

    private async void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = await AskNameAsync("新しいプロファイル", "プロファイル名（例: 仕事、趣味）", string.Empty, "作成");
        if (name == null) return;

        if (!await _profiles.CreateAsync(name))
        {
            await ShowMessageAsync("プロファイルを作成できません", _profiles.ErrorMessage);
        }
    }

    private async void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = await AskNameAsync("プロファイル名を変更", "新しい名前", _profiles.Current.Name, "変更");
        if (name == null) return;

        if (!_profiles.RenameCurrent(name))
        {
            await ShowMessageAsync("名前を変更できません", _profiles.ErrorMessage);
        }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        var others = _profiles.Profiles.Where(p => p.Id != _profiles.Current.Id).ToList();
        if (others.Count == 0)
        {
            await ShowMessageAsync("削除できるプロファイルがありません", "使用中のプロファイルは削除できません。別のプロファイルを作成するか、切り替えてから削除してください。");
            return;
        }

        var picker = new ComboBox { ItemsSource = others, DisplayMemberPath = "Name", SelectedIndex = 0, MinWidth = 240 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "選んだプロファイルの、インデックスとキャッシュ、フォルダの設定、検索の履歴を削除します。元の画像・PDF・動画は削除しません。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(picker);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "プロファイルを削除",
            Content = panel,
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || picker.SelectedItem is not ProfileInfo target) return;

        if (!_profiles.Delete(target.Id))
        {
            await ShowMessageAsync("プロファイルを削除できません", _profiles.ErrorMessage);
        }
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
            CloseButtonText = "キャンセル",
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
            CloseButtonText = "閉じる"
        }.ShowAsync();
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        ContentFrame.Navigated += ContentFrame_Navigated;
        // Without the model nothing can be searched or indexed, so start on the setup screen.
        var host = App.Services.GetRequiredService<EmbeddingSearchViewer.Infrastructure.Onnx.EmbeddingServiceHost>();
        Navigate(host.ModelFilesMissing ? "ModelSetupPage" : "SearchPage");
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
