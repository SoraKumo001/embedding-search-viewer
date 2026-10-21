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

        // Run diagnostics (video parallelism, FFmpeg permit, decoder markers, frame tracing) live in one store. Its
        // environment variables are read here, once, as the initial values; the worker reads the store at each run
        // start and the frame path reads it per frame. The FFmpeg worker choice is a saved setting instead.
        var runDiagnostics = new RunDiagnosticsOptions();
        EmbeddingSearchViewer.Infrastructure.Core.IngestTrace.FrameDetailProvider = () => runDiagnostics.FrameTracing;
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
        services.AddSingleton(runDiagnostics);
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
            // The opt-in split: FFmpeg runs in a child process (one per video), so nothing native is shared between
            // videos decoded at once. Saved with the library settings and read per run, like the decoder choice.
            var ffmpegWorkerSource = new ChildProcessFrameSource(ResolveFfmpegWorkerPath());
            var videoExtractor = new VideoSegmentExtractor(() =>
            {
                // One read per run: the three choices (decoder, worker, missing executable) fall back silently to the
                // in-process FFmpeg chain or to Media Foundation alone, which are the paths that existed before.
                var settings = settingsStore.Load();
                bool useFfmpeg = !DisableFfmpegDecoder && settings.UseFfmpegDecoder;
                if (!useFfmpeg) return new IVideoFrameSource[] { mediaFoundationSource };
                if (settings.UseFfmpegWorker && ffmpegWorkerSource.IsAvailable)
                {
                    return new IVideoFrameSource[] { ffmpegWorkerSource, mediaFoundationSource };
                }
                return new IVideoFrameSource[] { ffmpegSource, mediaFoundationSource };
            });
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
                emptyEmbeddingSkipsStore: sp.GetRequiredService<EmptyEmbeddingSkipStore>(),
                videoParallelismProvider: () => runDiagnostics.VideoParallelism,
                ffmpegPermitProvider: () => runDiagnostics.FfmpegPermit,
                ignoreDecoderMarkers: () => runDiagnostics.IgnoreDecoderMarkers);
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

        // The verification lane: a loopback-only diagnostics API. It is constructed with the services it reads (the
        // options, the worker) and resolves the library view model on demand, so nothing else is created earlier.
        services.AddSingleton<DiagApiServer>(sp => new DiagApiServer(
            sp,
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<RunDiagnosticsOptions>(),
            sp.GetRequiredService<IngestionPipelineWorker>()));

        Services = services.BuildServiceProvider();
    }

    /// <summary>
    /// The FFmpeg worker executable, copied next to the app under <c>FfmpegWorker\</c> at build and publish time.
    /// Null when it is not there: the worker setting then has nothing to offer, and the in-process FFmpeg chain stays
    /// as it is (silent fallback, one line in the crash log).
    /// </summary>
    private static string? ResolveFfmpegWorkerPath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDirectory, "FfmpegWorker", ChildProcessFrameSource.ExecutableFileName),
            Path.Combine(baseDirectory, ChildProcessFrameSource.ExecutableFileName),
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        CrashLog.Write(
            $"The FFmpeg worker is not next to the app ({string.Join(", ", candidates)}); the in-process decoder is used",
            null);
        return null;
    }

    /// <summary>
    /// The diagnostics API server, created the first time the switch turns it on (or when it starts on). With the switch
    /// off, which is the default, it is never constructed, so its worker dependency is not pulled in early either.
    /// </summary>
    private DiagApiServer? _diagApi;

    /// <summary>
    /// Starts or stops the loopback diagnostics API for the current switch value. Called on the UI thread: at launch when
    /// the switch starts on, and from the library page's option while the app runs. A failure is logged and never thrown:
    /// the app runs without the diagnostics lane. Stop is safe when the server was never created or never started.
    /// </summary>
    private void ApplyDiagApiSwitch(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (_diagApi == null)
                {
                    _diagApi = Services.GetRequiredService<DiagApiServer>();
                    AppDomain.CurrentDomain.ProcessExit += (_, _) => _diagApi?.Stop();
                }
                _diagApi.Start();
            }
            else
            {
                _diagApi?.Stop();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(enabled ? "DiagApi.Start" : "DiagApi.Stop", ex);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        CurrentWindow = new MainWindow();
        MainWindowDispatcherQueue = CurrentWindow.DispatcherQueue;
        (Services.GetRequiredService<IUiDispatcher>() as WinUiDispatcher)?.Initialize(CurrentWindow.DispatcherQueue);
        CurrentWindow.Activate();

        // The diagnostics API starts with the window (its port is ephemeral, so it has to be read from the log) and is
        // stopped when the process ends. A failure here must never keep the app from starting.
        //
        // The listener follows the RunDiagnosticsOptions switch: it is started at launch only when EnableDiagApi is on
        // (the default is off, so no port is bound at all), and the same switch turned on the library page starts it
        // while the app runs. A change is handed to the UI thread first: a PUT /v1/options that turns the switch off
        // must still get its answer before the socket closes.
        try
        {
            var runDiagnostics = Services.GetRequiredService<RunDiagnosticsOptions>();
            var dispatcher = Services.GetRequiredService<IUiDispatcher>();
            runDiagnostics.EnableDiagApiChanged += enabled => dispatcher.Enqueue(() => ApplyDiagApiSwitch(enabled));
            if (runDiagnostics.EnableDiagApi) ApplyDiagApiSwitch(true);
        }
        catch (Exception ex)
        {
            CrashLog.Write("DiagApi.Start", ex);
        }

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
