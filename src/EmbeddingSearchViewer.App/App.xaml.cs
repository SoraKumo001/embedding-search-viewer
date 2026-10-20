using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
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

    /// <summary>True when started with --auto-index: indexing starts automatically once the library page is ready.</summary>
    public static bool AutoStartIndexing { get; private set; }

    /// <summary>True when started with --no-ffmpeg: videos are decoded with the Windows media stack only, whatever the setting says (headless checks).</summary>
    public static bool DisableFfmpegDecoder { get; private set; }

    /// <summary>The store the settings come from; the file is read once and then kept in memory.</summary>
    private readonly AppSettingsStore _appSettingsStore;

    /// <summary>The settings that were read when the app started; the view models get them through the DI container.</summary>
    private readonly AppSettings _settings;

    public App()
    {
        // The language comes first: every text the app shows is chosen by it.
        // The settings are read once here; everything else gets them from the DI container.
        _appSettingsStore = new AppSettingsStore();
        _settings = _appSettingsStore.Load();
        StartupLanguageSetting = _settings.Language;
        Loc.SetLanguage(StartupLanguageSetting);
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
        {
            if (string.Equals(arg, "--auto-index", StringComparison.OrdinalIgnoreCase)) AutoStartIndexing = true;
            if (string.Equals(arg, "--no-ffmpeg", StringComparison.OrdinalIgnoreCase)) DisableFfmpegDecoder = true;
        }

        InitializeComponent();

        CrashLog.Install();
        EmbeddingSearchViewer.Infrastructure.Library.AtomicJsonFile.SaveFailed = (path, ex) => CrashLog.Write($"Could not save {path}", ex);
        // The ingestion trace writes to the same file (opened per line, so a hard crash keeps the last lines).
        EmbeddingSearchViewer.Infrastructure.Core.IngestTrace.Sink = (line, ex) => CrashLog.Write(line, ex);
        // An error on the UI thread (for example while a progress update is drawn) is recorded and the app keeps running.
        UnhandledException += (_, e) =>
        {
            CrashLog.Write("UI thread", e.Exception);
            e.Handled = true;
        };

        var services = new ServiceCollection();

        // 1. Core Services & Infrastructure
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IUiDispatcher, WinUiDispatcher>();
        services.AddSingleton(_appSettingsStore);
        services.AddSingleton(_settings);
        services.AddSingleton<ProfileManager>(_ => new ProfileManager());
        
        services.AddSingleton<IMediaRepository>(sp => new ProfileScopedRepository(sp.GetRequiredService<ProfileManager>()));

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
            var settingsStore = sp.GetRequiredService<LibrarySettingsStore>();
            // Videos: FFmpeg first (measured faster and covers more formats), the Windows media stack as the fallback.
            // The decoder choice is read per run (like the media types), so a change applies to videos indexed from then on.
            var ffmpegSource = new FFmpegFrameSource();
            var mediaFoundationSource = new MediaFoundationFrameSource();
            var videoExtractor = new VideoSegmentExtractor(() =>
                ShouldUseFfmpegDecoder(settingsStore)
                    ? new IVideoFrameSource[] { ffmpegSource, mediaFoundationSource }
                    : new IVideoFrameSource[] { mediaFoundationSource });
            var profiles = sp.GetRequiredService<ProfileManager>();
            return new IngestionPipelineWorker(repo, embeddingService, videoExtractor: videoExtractor, cacheDirectoryProvider: () => profiles.CacheDirectory,
                videoSegmentSeconds: () => sp.GetRequiredService<LibrarySettingsStore>().Load().VideoSegmentSeconds,
                videoAutoSegment: () => sp.GetRequiredService<LibrarySettingsStore>().Load().VideoAutoSegment,
                mediaTypes: () =>
                {
                    var s = sp.GetRequiredService<LibrarySettingsStore>().Load();
                    return new MediaTypeSelection(s.IndexImages, s.IndexPdfs, s.IndexVideos, s.IndexDocuments);
                },
                exclusions: sp.GetRequiredService<IndexExclusionStore>(),
                emptyEmbeddingSkipsStore: sp.GetRequiredService<EmptyEmbeddingSkipStore>());
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
        services.AddSingleton<EmptyEmbeddingSkipStore>(sp =>
        {
            var profiles = sp.GetRequiredService<ProfileManager>();
            return new EmptyEmbeddingSkipStore(() => profiles.EmptySkipsPath);
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
        services.AddSingleton<SettingsViewModel>(sp => new SettingsViewModel(
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<SearchViewModel>(),
            sp.GetRequiredService<AppSettingsStore>(),
            StartupLanguageSetting));
        services.AddSingleton<ModelSetupViewModel>();

        Services = services.BuildServiceProvider();
    }

    /// <summary>
    /// Whether videos are decoded with FFmpeg first. The --no-ffmpeg flag wins over the setting; a change applies to the
    /// videos indexed from then on (the extractor calls this per run).
    /// </summary>
    private static bool ShouldUseFfmpegDecoder(LibrarySettingsStore settingsStore)
        => !DisableFfmpegDecoder && settingsStore.Load().UseFfmpegDecoder;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CurrentWindow = new MainWindow();
        MainWindowDispatcherQueue = CurrentWindow.DispatcherQueue;
        (Services.GetRequiredService<IUiDispatcher>() as WinUiDispatcher)?.Initialize(CurrentWindow.DispatcherQueue);
        CurrentWindow.Activate();

        // The watchers are set up after the window is up: the folders may sit on a slow drive, and the start must not
        // wait for them. The watchers themselves do not care about the thread they are created on.
        var watcher = Services.GetRequiredService<FolderWatcher>();
        Task.Run(() =>
        {
            try
            {
                watcher.Refresh();
            }
            catch (Exception ex)
            {
                CrashLog.Write("FolderWatcher.Refresh", ex);
            }
        });
    }
}
