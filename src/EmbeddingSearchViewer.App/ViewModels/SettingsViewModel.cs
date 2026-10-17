using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer_App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IEmbeddingService _embeddingService;
    private readonly AppSettingsStore _appSettings;
    private readonly string _startupLanguage;

    /// <summary>Search options shared with the search page (dimension, similarity threshold, FTS).</summary>
    public SearchViewModel Search { get; }

    /// <summary>The version from version.txt, as stamped into the assembly at build time.</summary>
    public string AppVersionText { get; } = $"EmbeddingSearchViewer v{GetVersion()}";

    private static string GetVersion()
    {
        var info = System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        // "1.2.3+<commit>" -> "1.2.3"
        return string.IsNullOrEmpty(info) ? "?" : info.Split('+')[0];
    }

    [ObservableProperty]
    private bool _isEngineReady;

    // ---- language ----

    private static readonly string[] LanguageSettings = { Loc.Auto, Loc.Japanese, Loc.English };

    /// <summary>0: follow Windows, 1: Japanese, 2: English.</summary>
    [ObservableProperty]
    private int _languageIndex;

    /// <summary>True when the chosen language differs from the one the app was started with.</summary>
    [ObservableProperty]
    private bool _restartRequired;

    partial void OnLanguageIndexChanged(int value)
    {
        if (value < 0 || value >= LanguageSettings.Length) return;

        var settings = _appSettings.Load();
        settings.Language = LanguageSettings[value];
        _appSettings.Save(settings);
        RestartRequired = settings.Language != _startupLanguage;
    }

    [ObservableProperty]
    private string _healthDetails = Loc.T("Settings.Health.Unchecked");

    public SettingsViewModel(IEmbeddingService embeddingService, SearchViewModel search, AppSettingsStore appSettings, string? startupLanguage = null)
    {
        _embeddingService = embeddingService;
        Search = search;
        _appSettings = appSettings;
        _startupLanguage = startupLanguage ?? _appSettings.Load().Language;
        _languageIndex = Math.Max(0, Array.IndexOf(LanguageSettings, _appSettings.Load().Language));
        _restartRequired = LanguageSettings[_languageIndex] != _startupLanguage;
    }

    [RelayCommand]
    public async Task CheckHealthAsync()
    {
        HealthDetails = Loc.T("Settings.Health.Checking");
        try
        {
            IsEngineReady = await _embeddingService.IsReadyAsync();
            HealthDetails = IsEngineReady
                ? Loc.T("Settings.Health.Ready")
                : UnavailableEmbeddingService.ReasonOf(_embeddingService) ?? Loc.T("Settings.Health.Unavailable");
        }
        catch (System.Exception ex)
        {
            IsEngineReady = false;
            HealthDetails = Loc.T("Settings.Health.Error", ex.Message);
        }
    }
}
