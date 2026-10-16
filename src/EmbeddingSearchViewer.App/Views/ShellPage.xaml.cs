using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using EmbeddingSearchViewer_App.Services;

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

    public ShellPage()
    {
        InitializeComponent();
        var navService = App.Services.GetRequiredService<INavigationService>();
        navService.SetFrame(ContentFrame);
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
