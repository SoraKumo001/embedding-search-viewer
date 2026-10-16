using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using EmbeddingSearchViewer_App.ViewModels;

namespace EmbeddingSearchViewer_App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
        Loaded += async (_, _) => await ViewModel.CheckHealthCommand.ExecuteAsync(null);
    }
}
