using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Core;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer_App.ViewModels;

public partial class LibraryViewModel : ObservableObject
{
    private readonly IngestionPipelineWorker _worker;
    private readonly IEmbeddingService _embeddingService;
    private readonly IMediaRepository _repository;
    private readonly LibrarySettingsStore _store;
    private readonly FolderWatcher _watcher;
    private bool _loadingSettings;
    private readonly LibraryStatsRefresher _statsRefresher;
    private readonly IndexExclusionStore _exclusions;
    private readonly IUiDispatcher _dispatcher;
    private readonly RunDiagnosticsOptions _runDiagnostics;

    [ObservableProperty]
    private string _engineStatus = Loc.T("Library.Engine.Name");

    [ObservableProperty]
    private bool _isEngineReady = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isIndexing;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isClearing;

    /// <summary>True while indexing or clearing the index; the UI binds busy indicators to this.</summary>
    public bool IsBusy => IsIndexing || IsClearing;

    private CancellationTokenSource? _clearCts;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private int _processedFiles;

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private string _currentStatusMessage = Loc.T("Library.Status.Ready");

    [ObservableProperty]
    private string? _errorMessage;


    public LibraryViewModel(
        IngestionPipelineWorker worker,
        IEmbeddingService embeddingService,
        IMediaRepository repository,
        LibrarySettingsStore store,
        FolderWatcher watcher,
        IndexExclusionStore exclusions,
        IUiDispatcher? dispatcher = null,
        RunDiagnosticsOptions? runDiagnostics = null)
    {
        _exclusions = exclusions;
        _store = store;
        _watcher = watcher;
        _worker = worker;
        _embeddingService = embeddingService;
        _repository = repository;
        _dispatcher = dispatcher ?? EmbeddingSearchViewer.Core.Services.ImmediateDispatcher.Instance;
        _runDiagnostics = runDiagnostics ?? new RunDiagnosticsOptions();
        _diagnosticsTimer = new System.Threading.Timer(_ => PollDiagnostics(), null, Timeout.Infinite, Timeout.Infinite);

        // The switches start from the store (its environment variables were read once at start-up); from then on the
        // screen is the source and every change is written back to the same store.
        VideoParallelism = _runDiagnostics.VideoParallelism;
        FfmpegPermit = _runDiagnostics.FfmpegPermit;
        IgnoreDecoderMarkers = _runDiagnostics.IgnoreDecoderMarkers;
        FrameTracing = _runDiagnostics.FrameTracing;
        EnableDiagApi = _runDiagnostics.EnableDiagApi;

        _worker.ProgressChanged += OnProgressChanged;
        TargetFolders.CollectionChanged += OnTargetFoldersChanged;

        // The engine is created in the background, so the line on the screen is updated when it is there.
        if (_embeddingService is EmbeddingServiceHost engineHost)
        {
            engineHost.Changed += OnEngineChanged;
        }

        // While an index is being built, keep the statistics cards current (only when the data has changed). The refresh
        // is the light path, and it is thinned to at most once every five seconds, so the UI does not load every indexed
        // path and recount every folder on each tick while ingestion is writing to the database.
        _statsRefresher = new LibraryStatsRefresher(
            repository,
            () => OnUiThreadAsync(() => RefreshLibraryStatsCoreAsync(full: false)),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(5));
    }

    public async Task InitializeAsync()
    {
        await InitializeCoreAsync();
        ScheduleFullStatsRefresh();
    }

    /// <summary>
    /// Fast part of startup: engine state, folders and the light statistics (counts query only, no per-file scan).
    /// After this returns the run can start; the full refresh follows while indexing is already going.
    /// </summary>
    public async Task InitializeCoreAsync()
    {
        // The counters are still zero until they are read, so the cards show a dash instead: on the first start-up
        // (the field starts true) and again after a profile change, but not on a later visit of the page, where the
        // numbers are already known and would only flicker away for the short light refresh.
        if (!_statsShown)
        {
            IsStatsLoading = true;
            _statsLoadPending = true;
        }

        // The engine is created in the background (the app starts right away), so the screen says "initializing" at
        // once and is updated when the engine is there. Nothing here waits for the engine.
        IsEngineReady = false;
        EngineStatus = Loc.T("Library.Engine.Initializing");
        _ = UpdateEngineStateAsync();

        if (TargetFolders.Count == 0)
        {
            // The settings of this profile are read once and kept: a later save reuses them.
            var saved = _settings ??= _store.Load();

            // Whether the folders still exist can be slow on a network drive: it does not block the UI.
            var existing = await Task.Run(() => saved.Folders.Where(Directory.Exists).ToList());

            _loadingSettings = true;
            try
            {
                AddFolders(existing);
                AutoIndex = saved.AutoIndex;
                VideoSegmentIndex = Math.Max(0, VideoSegmentChoiceList.IndexOf(saved.VideoSegmentSeconds));
                VideoAutoSegment = saved.VideoAutoSegment;
                UseFfmpegDecoder = saved.UseFfmpegDecoder;
                UseFfmpegWorker = saved.UseFfmpegWorker;
                IndexImages = saved.IndexImages;
                IndexPdfs = saved.IndexPdfs;
                IndexVideos = saved.IndexVideos;
                IndexDocuments = saved.IndexDocuments;
                FolderSortIndex = saved.FolderSortIndex;
            }
            finally
            {
                _loadingSettings = false;
            }
            HasFolders = TargetFolders.Count > 0;
        }

        RefreshExcluded();
        await RefreshLibraryStatsCoreAsync(full: false);
    }

    /// <summary>
    /// Reads the state of the engine and shows it. It waits for the engine to be created (which happens in the
    /// background), so the line on the screen changes from "initializing" to "ready" as soon as it is there.
    /// </summary>
    private async Task UpdateEngineStateAsync()
    {
        try
        {
            bool ready = await _embeddingService.IsReadyAsync().ConfigureAwait(false);
            string status = ready
                ? Loc.T("Library.Engine.Ready")
                : UnavailableEmbeddingService.ReasonOf(_embeddingService) ?? Loc.T("Library.Engine.Initializing");

            _dispatcher.Enqueue(() =>
            {
                IsEngineReady = ready;
                EngineStatus = status;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading the engine state: {ex.Message}");
        }
    }

    /// <summary>The engine was replaced (it is created in the background, or reloaded after a download).</summary>
    private void OnEngineChanged()
    {
        _ = UpdateEngineStateAsync();
    }

    /// <summary>Drops everything shown for the previous profile and loads the folders and statistics of the new one.</summary>
    public async Task ReloadForProfileChangeAsync()
    {
        _suspendSync = true;
        try
        {
            TargetFolders.Clear();
        }
        finally
        {
            _suspendSync = false;
        }
        SyncFolderItems(recount: false);
        HasFolders = false;
        TotalFiles = 0;
        ProcessedFiles = 0;
        ProgressPercentage = 0;
        ErrorMessage = null;
        IndexModelWarning = string.Empty;
        CurrentStatusMessage = Loc.T("Library.Status.Ready");

        // The new profile has its own settings, its own index and its own folders: everything that was cached is dropped.
        _initialized = false;
        _settings = null;
        _statsVersion = -1;
        _statsDataVersion = -1;
        _statsAt = DateTime.MinValue;
        _indexedPaths = Array.Empty<string>();
        _folderCounts = new();

        // The numbers on the cards belong to the old profile: they are replaced by a dash until the new ones are read.
        _statsShown = false;
        IsStatsLoading = true;
        _statsLoadPending = true;

        var pending = Interlocked.Exchange(ref _deferredRefreshCts, null);
        pending?.Cancel(); // not disposed: the refresh that uses it may still be running

        await InitializeAsync();
    }

    partial void OnIsIndexingChanged(bool value)
    {
        if (value)
        {
            _statsRefresher.Start();
            StartDiagnosticsPolling();
        }
        else
        {
            _statsRefresher.Stop();
            StopDiagnosticsPolling();
        }
    }

    /// <summary>Runs <paramref name="work"/> on the UI thread (the statistics are bound properties) and waits for it.</summary>
    private Task OnUiThreadAsync(Func<Task> work) => _dispatcher.EnqueueAsync(work);

    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    public void SetDispatcherQueue(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
        if (_dispatcher is EmbeddingSearchViewer_App.Services.WinUiDispatcher winUiDispatcher)
        {
            winUiDispatcher.Initialize(dispatcherQueue);
        }
    }

    private readonly IngestionEta _eta = new();

    /// <summary>"About 3 min left"; empty until the speed is known, and when nothing is running.</summary>
    [ObservableProperty]
    private string _etaText = string.Empty;

    private void OnProgressChanged(IngestionProgress progress)
    {
        void UpdateState()
        {
            if (progress.IsRunning && !IsIndexing) _eta.Reset(); // a new run
            EtaText = progress.IsRunning && progress.TotalFiles > 0
                ? _eta.Update(DateTime.UtcNow, progress) is { } left ? IngestionEta.Format(left) : string.Empty
                : string.Empty;

            TotalFiles = progress.TotalFiles;
            ProcessedFiles = progress.ProcessedFiles;
            IsIndexing = progress.IsRunning;
            VideoProgressText = progress.IsRunning ? progress.Detail ?? string.Empty : string.Empty;

            if (progress.IsRunning)
            {
                CurrentStatusMessage = progress.TotalFiles > 0
                    ? Loc.T("Library.Progress.WithTotal", progress.ProcessedFiles, progress.TotalFiles, progress.CurrentFileName)
                    : Loc.T("Library.Progress.NoTotal", progress.CurrentFileName);

                ProgressPercentage = progress.TotalFiles > 0
                    ? Math.Min(100.0, (double)progress.ProcessedFiles / progress.TotalFiles * 100.0)
                    : 0;
            }
            else
            {
                CurrentStatusMessage = progress.ProcessedFiles >= progress.TotalFiles && progress.TotalFiles > 0
                    ? Loc.T("Library.Done", progress.ProcessedFiles, progress.TotalFiles)
                      + (progress.SkippedFiles > 0 ? Loc.T("Library.Done.Skipped", progress.SkippedFiles) : string.Empty)
                      + (progress.ExcludedFiles > 0 ? Loc.T("Library.Done.Excluded", progress.ExcludedFiles) : string.Empty)
                      + (string.IsNullOrEmpty(progress.Summary) ? string.Empty : Loc.T("Library.Done.Summary", progress.Summary))
                    : progress.CurrentFileName;

                if (progress.ProcessedFiles >= progress.TotalFiles && progress.TotalFiles > 0)
                {
                    ProgressPercentage = 100.0;
                    _ = RefreshLibraryStatsAsync();
                }
                RefreshExcluded();
            }

            if (!string.IsNullOrEmpty(progress.ErrorMessage))
            {
                ErrorMessage = progress.ErrorMessage;
                RefreshExcluded(); // a failed file may have just been added to the list of excluded files
            }
        }

        _dispatcher.Enqueue(UpdateState);
    }
}
