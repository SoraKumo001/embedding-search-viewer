using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Services;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using EmbeddingSearchViewer.Infrastructure.Profiles;
using EmbeddingSearchViewer_App.Services;
using EmbeddingSearchViewer_App.ViewModels;

namespace EmbeddingSearchViewer_App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static Window CurrentWindow { get; private set; } = null!;
    public static Microsoft.UI.Dispatching.DispatcherQueue MainWindowDispatcherQueue { get; private set; } = null!;

    /// <summary>The language setting ("auto", "ja", "en") that the app was started with; a change applies at the next start.</summary>
    public static string StartupLanguageSetting { get; private set; } = Loc.Auto;

    public App()
    {
        // The language comes first: every text the app shows is chosen by it.
        StartupLanguageSetting = new AppSettingsStore().Load().Language;
        Loc.SetLanguage(StartupLanguageSetting);

        InitializeComponent();

        var services = new ServiceCollection();

        // 1. Core Services & Infrastructure
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<ProfileManager>(_ => new ProfileManager());
        
        services.AddSingleton<IMediaRepository>(sp =>
        {
            var repo = new ProfileScopedRepository(sp.GetRequiredService<ProfileManager>());
            repo.InitializeAsync().GetAwaiter().GetResult();
            return repo;
        });

        // In-process ONNX Runtime embedding. The host starts "unavailable" when the model files are missing
        // and switches to the real engine after the model has been downloaded.
        services.AddSingleton<EmbeddingServiceHost>(_ => new EmbeddingServiceHost());
        services.AddSingleton<IEmbeddingService>(sp => sp.GetRequiredService<EmbeddingServiceHost>());
        services.AddSingleton<ModelDownloader>(_ => new ModelDownloader());

        services.AddSingleton<HybridSearchEngine>();

        services.AddSingleton<IngestionPipelineWorker>(sp =>
        {
            var repo = sp.GetRequiredService<IMediaRepository>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            // Videos: FFmpeg first (measured faster and covers more formats), the Windows media stack as the fallback.
            var videoExtractor = new VideoSegmentExtractor(new IVideoFrameSource[] { new FFmpegFrameSource(), new MediaFoundationFrameSource() });
            var profiles = sp.GetRequiredService<ProfileManager>();
            return new IngestionPipelineWorker(repo, embeddingService, videoExtractor: videoExtractor, cacheDirectoryProvider: () => profiles.CacheDirectory,
                videoSegmentSeconds: () => sp.GetRequiredService<LibrarySettingsStore>().Load().VideoSegmentSeconds,
                exclusions: sp.GetRequiredService<IndexExclusionStore>());
        });

        services.AddSingleton<LibrarySettingsStore>(sp =>
        {
            var profiles = sp.GetRequiredService<ProfileManager>();
            return new LibrarySettingsStore(() => profiles.LibraryPath);
        });
        services.AddSingleton<IndexExclusionStore>(sp =>
        {
            var profiles = sp.GetRequiredService<ProfileManager>();
            return new IndexExclusionStore(() => profiles.ExclusionsPath);
        });
        services.AddSingleton<SearchHistoryStore>(sp =>
        {
            var profiles = sp.GetRequiredService<ProfileManager>();
            return new SearchHistoryStore(() => profiles.HistoryPath);
        });
        services.AddSingleton<FolderWatcher>();

        // 2. ViewModels
        services.AddSingleton<SearchViewModel>();
        services.AddSingleton<ProfileViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ModelSetupViewModel>();

        Services = services.BuildServiceProvider();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CurrentWindow = new MainWindow();
        MainWindowDispatcherQueue = CurrentWindow.DispatcherQueue;
        CurrentWindow.Activate();
        Services.GetRequiredService<FolderWatcher>().Refresh();
    }
}
