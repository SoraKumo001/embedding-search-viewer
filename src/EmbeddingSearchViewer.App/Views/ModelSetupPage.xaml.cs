using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using EmbeddingSearchViewer_App.Services;
using EmbeddingSearchViewer_App.ViewModels;

namespace EmbeddingSearchViewer_App.Views;

public sealed partial class ModelSetupPage : Page
{
    // Singleton, so a running download keeps its progress while the user looks at other pages.
    public ModelSetupViewModel ViewModel { get; }

    public ModelSetupPage()
    {
        InitializeComponent();
        ViewModel = App.Services.GetRequiredService<ModelSetupViewModel>();
        Loaded += (_, _) => ViewModel.Refresh();
    }

    private void GoToSearch_Click(object sender, RoutedEventArgs e)
    {
        App.Services.GetRequiredService<INavigationService>().NavigateTo<SearchPage>();
    }
}
