using System;
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

    /// <summary>Starts a new instance of the app (it reads the language setting) and closes this one.</summary>
    private void RestartNow_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true });
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Could not restart: {ex.Message}");
        }
    }
}
