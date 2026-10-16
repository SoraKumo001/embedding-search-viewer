using Microsoft.UI.Xaml.Controls;

namespace EmbeddingSearchViewer_App.Services;

public interface INavigationService
{
    bool CanGoBack { get; }
    void NavigateTo<TPage>() where TPage : Page;
    void NavigateTo<TPage>(object parameter) where TPage : Page;
    void GoBack();
    void SetFrame(Frame frame);
}
