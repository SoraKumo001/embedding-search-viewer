using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer_App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IEmbeddingService _embeddingService;

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

    [ObservableProperty]
    private string _healthDetails = "未確認";

    public SettingsViewModel(IEmbeddingService embeddingService, SearchViewModel search)
    {
        _embeddingService = embeddingService;
        Search = search;
    }

    [RelayCommand]
    public async Task CheckHealthAsync()
    {
        HealthDetails = "確認中...";
        try
        {
            IsEngineReady = await _embeddingService.IsReadyAsync();
            HealthDetails = IsEngineReady
                ? "準備完了 (ONNX Runtime / DirectML)"
                : UnavailableEmbeddingService.ReasonOf(_embeddingService) ?? "初期化中、または利用できません (モデルファイルを確認してください)";
        }
        catch (System.Exception ex)
        {
            IsEngineReady = false;
            HealthDetails = $"エラー: {ex.Message}";
        }
    }
}
