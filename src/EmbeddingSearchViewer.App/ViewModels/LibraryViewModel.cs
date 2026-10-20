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

    // Library Statistics
    [ObservableProperty]
    private int _totalIndexedAssets;

    [ObservableProperty]
    private int _indexedImageCount;

    [ObservableProperty]
    private int _indexedPdfCount;

    [ObservableProperty]
    private int _indexedVideoCount;

    [ObservableProperty]
    private int _indexedDocumentCount;

    [ObservableProperty]
    private bool _hasFolders;

    [ObservableProperty]
    private bool _hasIndexedAssets;

    /// <summary>The folders to index, in the order they were added (this is what is saved and indexed).</summary>
    public ObservableCollection<string> TargetFolders { get; } = new();

    /// <summary>The same folders as shown on screen: in the chosen order, each with the number of files indexed under it.</summary>
    public ObservableCollection<FolderEntry> FolderItems { get; } = new();

    /// <summary>0: order added, 1: name, 2: number of indexed files.</summary>
    [ObservableProperty]
    private int _folderSortIndex;

    partial void OnFolderSortIndexChanged(int value)
    {
        SyncFolderItems(recount: false);
        if (!_loadingSettings) SaveSettings();
    }

    private IReadOnlyList<string> _indexedPaths = Array.Empty<string>();
    private Dictionary<string, int> _folderCounts = new();

    /// <summary>The settings of the current profile; they are read once per transition and reused when saving.</summary>
    private LibrarySettings? _settings;

    /// <summary>True once the one-time work of the start-up (the folders and the heavy refresh) is done for this profile.</summary>
    private bool _initialized;

    /// <summary>The data version the numbers on screen come from; the same version means they are still right.</summary>
    private long _statsVersion = -1;

    /// <summary>The data version the cached paths and the files per folder come from.</summary>
    private long _statsDataVersion = -1;

    /// <summary>When the numbers on screen were read.</summary>
    private DateTime _statsAt = DateTime.MinValue;

    /// <summary>True while the folders are changed in bulk, so the files per folder are not recounted for every single one.</summary>
    private bool _suspendSync;

    /// <summary>Cancels the delayed heavy refresh when another one is scheduled.</summary>
    private CancellationTokenSource? _deferredRefreshCts;

    /// <summary>Counts the requests for the excluded files, so an older answer is dropped when a newer one is on the way.</summary>
    private int _excludedRequestId;

    /// <summary>The shortest time between two reads of the statistics; a read that just happened is not repeated.</summary>
    private static readonly TimeSpan MinimumStatsInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Brings <see cref="FolderItems"/> in line with <see cref="TargetFolders"/>, the counts and the sort order.
    /// Items are updated and moved in place, so the list does not jump back to the top while counts are refreshed.
    /// </summary>
    private void SyncFolderItems(bool recount = true)
    {
        foreach (var gone in FolderItems.Where(i => !TargetFolders.Contains(i.Path)).ToList()) FolderItems.Remove(gone);
        foreach (var folder in TargetFolders.Where(f => FolderItems.All(i => i.Path != f))) FolderItems.Add(new FolderEntry(folder));

        // Counting walks every indexed path for every folder, so it is only redone when the folders or the index changed.
        if (recount) _folderCounts = FolderListing.CountFiles(TargetFolders, _indexedPaths);
        var counts = _folderCounts;
        foreach (var item in FolderItems) item.SetCount(counts.TryGetValue(item.Path, out int n) ? n : 0);

        var mode = Enum.IsDefined(typeof(FolderSortMode), FolderSortIndex) ? (FolderSortMode)FolderSortIndex : FolderSortMode.Added;
        var order = FolderListing.Order(TargetFolders, counts, mode);
        for (int i = 0; i < order.Count; i++)
        {
            int from = IndexOfItem(order[i]);
            if (from >= 0 && from != i) FolderItems.Move(from, i);
        }
    }

    /// <summary>The place of a folder in the list on screen, or -1 when it is not on it.</summary>
    private int IndexOfItem(string path)
    {
        for (int i = 0; i < FolderItems.Count; i++)
        {
            if (FolderItems[i].Path == path) return i;
        }
        return -1;
    }

    /// <summary>
    /// Adds several folders at once. The list on screen is brought in line once, at the end, so the files per folder
    /// are not counted again for every single folder that is added.
    /// </summary>
    private void AddFolders(IEnumerable<string> folders)
    {
        _suspendSync = true;
        try
        {
            foreach (var folder in folders) TargetFolders.Add(folder);
        }
        finally
        {
            _suspendSync = false;
        }
        SyncFolderItems(recount: false);
    }

    /// <summary>Keeps the list on screen in line with the folders to index.</summary>
    private void OnTargetFoldersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The folders are added in bulk at the start: the list is brought in line once, at the end of the batch.
        if (_suspendSync) return;
        SyncFolderItems();
    }

    [ObservableProperty]
    private bool _autoIndex;

    /// <summary>The choices for the length of a video segment, in seconds; the first is the standard.</summary>
    public static IReadOnlyList<int> VideoSegmentChoices { get; } = new[] { 15, 30, 60, 120 };

    /// <summary>The same choices as a list, so that a lookup does not build a new list every time.</summary>
    private static readonly List<int> VideoSegmentChoiceList = VideoSegmentChoices.ToList();

    public static IReadOnlyList<string> VideoSegmentLabels { get; } =
        new[] { Loc.T("Library.SegmentLabel.Standard", 15), Loc.T("Library.SegmentLabel", 30), Loc.T("Library.SegmentLabel", 60), Loc.T("Library.SegmentLabel", 120) };

    public IReadOnlyList<string> VideoSegmentOptions => VideoSegmentLabels;

    [ObservableProperty]
    private int _videoSegmentIndex;

    partial void OnVideoSegmentIndexChanged(int value)
    {
        if (!_loadingSettings && value >= 0) SaveSettings();
    }

    [ObservableProperty]
    private bool _indexImages = true;

    [ObservableProperty]
    private bool _indexPdfs = true;

    [ObservableProperty]
    private bool _indexVideos = true;

    [ObservableProperty]
    private bool _indexDocuments = true;

    partial void OnIndexImagesChanged(bool value) { if (!_loadingSettings) SaveSettings(); }
    partial void OnIndexPdfsChanged(bool value) { if (!_loadingSettings) SaveSettings(); }
    partial void OnIndexVideosChanged(bool value) { if (!_loadingSettings) SaveSettings(); }
    partial void OnIndexDocumentsChanged(bool value) { if (!_loadingSettings) SaveSettings(); }

    [ObservableProperty]
    private bool _videoAutoSegment = true;

    partial void OnVideoAutoSegmentChanged(bool value)
    {
        if (!_loadingSettings) SaveSettings();
    }

    /// <summary>When on, videos are decoded with FFmpeg first (the Windows media stack as the fallback); off uses the Windows media stack alone.</summary>
    [ObservableProperty]
    private bool _useFfmpegDecoder = true;

    partial void OnUseFfmpegDecoderChanged(bool value)
    {
        if (!_loadingSettings) SaveSettings();
    }

    // ---- diagnostics switches for video ingestion (this session only) ----
    //
    // The five values live in RunDiagnosticsOptions, one process-wide store that the worker reads (the two counts when
    // a run starts, the three switches per video and per frame). Every change here is written straight to it, so the
    // next run uses it, and none of them goes into SaveSettings: library.json keeps only the real settings, and the
    // values start again from the store (environment variables) at the next start.

    /// <summary>How many videos are indexed at once (1-3). Read by the worker when a run starts.</summary>
    [ObservableProperty]
    private int _videoParallelism = RunDiagnosticsOptions.MaxVideoParallelism;

    partial void OnVideoParallelismChanged(int value)
    {
        _runDiagnostics.VideoParallelism = value;
        // The store clamps: the screen shows the value the worker will actually use.
        if (_runDiagnostics.VideoParallelism != value) VideoParallelism = _runDiagnostics.VideoParallelism;
    }

    /// <summary>How many FFmpeg decodes may run at once (1-2). Read by the worker when a run starts.</summary>
    [ObservableProperty]
    private int _ffmpegPermit = RunDiagnosticsOptions.MinFfmpegPermit;

    partial void OnFfmpegPermitChanged(int value)
    {
        _runDiagnostics.FfmpegPermit = value;
        if (_runDiagnostics.FfmpegPermit != value) FfmpegPermit = _runDiagnostics.FfmpegPermit;
    }

    /// <summary>When on, a leftover decoder marker does not keep a video on the Windows media stack. Read per video.</summary>
    [ObservableProperty]
    private bool _ignoreDecoderMarkers;

    partial void OnIgnoreDecoderMarkersChanged(bool value) => _runDiagnostics.IgnoreDecoderMarkers = value;

    /// <summary>When on, every frame is written to the ingestion trace. Read per frame.</summary>
    [ObservableProperty]
    private bool _frameTracing;

    partial void OnFrameTracingChanged(bool value) => _runDiagnostics.FrameTracing = value;

    /// <summary>When on, FFmpeg runs in a child process (one per video) instead of in the app. Read per video.</summary>
    [ObservableProperty]
    private bool _useFfmpegWorker;

    partial void OnUseFfmpegWorkerChanged(bool value) => _runDiagnostics.UseFfmpegWorker = value;

    // ---- the numbers of the run, shown while it runs ----

    /// <summary>
    /// Per-decoder milliseconds per frame and the video and stage totals of the running job; empty when nothing runs.
    /// It is polled (about every 750 ms) rather than rebuilt on every progress update: the numbers move slowly, while
    /// progress events arrive far more often than the screen can show them.
    /// </summary>
    [ObservableProperty]
    private string _diagnosticsText = string.Empty;

    private static readonly TimeSpan DiagnosticsInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>The decoder totals at the moment the run started, so only this run's frames are counted.</summary>
    private Dictionary<string, (double Seconds, long Frames)> _decoderBaseline = new(StringComparer.Ordinal);

    private readonly System.Threading.Timer _diagnosticsTimer;

    /// <summary>1 while a poll is being built, so a slow tick cannot stack up behind the next one.</summary>
    private int _diagnosticsBusy;

    private void StartDiagnosticsPolling()
    {
        _decoderBaseline = new Dictionary<string, (double Seconds, long Frames)>(VideoDecoderStats.Shared.GetRaw());
        PollDiagnostics(); // the first line right away
        _diagnosticsTimer.Change(DiagnosticsInterval, DiagnosticsInterval);
    }

    private void StopDiagnosticsPolling()
    {
        _diagnosticsTimer.Change(Timeout.Infinite, Timeout.Infinite);
        DiagnosticsText = string.Empty;
    }

    private void PollDiagnostics()
    {
        if (Interlocked.Exchange(ref _diagnosticsBusy, 1) == 1) return;
        try
        {
            string text = BuildDiagnosticsText();
            _dispatcher.Enqueue(() =>
            {
                // The run may have stopped while this line was being built.
                if (IsIndexing) DiagnosticsText = text;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading run diagnostics: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _diagnosticsBusy, 0);
        }
    }

    /// <summary>
    /// "FFmpeg 41.2 ms/枚 (120 枚) · MediaFoundation 88.0 ms/枚 (30 枚)" over the phases of the run on the next line.
    /// The decoder totals come from the same counters the video extractor writes (the store the worker's extractor
    /// uses), and are the raw seconds and frames: the milliseconds per frame are worked out here.
    /// </summary>
    private string BuildDiagnosticsText()
    {
        var parts = new List<string>();
        foreach (var (decoder, totals) in VideoDecoderStats.Shared.GetRaw().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var before = _decoderBaseline.TryGetValue(decoder, out var seen) ? seen : (Seconds: 0.0, Frames: 0L);
            double seconds = totals.Seconds - before.Seconds;
            long frames = totals.Frames - before.Frames;
            if (frames <= 0 || seconds < 0) continue; // this decoder has not run yet, or only before this run
            parts.Add(Loc.T("Library.Diagnostics.Decoder", decoder, seconds * 1000.0 / frames, frames));
        }

        string decoders = parts.Count > 0
            ? string.Join(Loc.T("Library.Diagnostics.Separator"), parts)
            : Loc.T("Library.Diagnostics.NoDecoder");

        var video = _worker.VideoStats;
        if (video.Videos == 0) return decoders;

        string line = Loc.T("Library.Diagnostics.Videos", video.Videos, video.Segments, video.ExtractSeconds, video.EmbedSeconds);
        if (_worker.StageStats.HasSamples) line += Loc.T("Library.Diagnostics.SaveShare", _worker.StageStats.SaveSharePercent);
        return decoders + "\n" + line;
    }

    // ---- files left out of the index because reading them failed repeatedly ----

    public ObservableCollection<ExcludedFile> ExcludedFiles { get; } = new();

    [ObservableProperty]
    private bool _hasExcluded;

    [ObservableProperty]
    private string _excludedHeaderText = string.Empty;

    /// <summary>Re-reads the list of excluded files (they are added by the indexing worker, on its own thread).</summary>
    public void RefreshExcluded()
    {
        // Checking whether the files still exist touches the disk, so it does not run on the UI thread.
        int requestId = Interlocked.Increment(ref _excludedRequestId);
        _ = Task.Run(() =>
        {
            // A file deleted after it failed must not stay in the list as an error: drop it (and persist).
            _exclusions.PruneMissing();
            var items = _exclusions.List();
            _dispatcher.Enqueue(() =>
            {
                // A newer list is already on its way: this answer is dropped.
                if (requestId != Volatile.Read(ref _excludedRequestId)) return;

                ExcludedFiles.Clear();
                foreach (var item in items) ExcludedFiles.Add(item);
                HasExcluded = items.Count > 0;
                ExcludedHeaderText = items.Count > 0 ? Loc.T("Library.ExcludedHeader", items.Count) : string.Empty;
            });
        });
    }

    /// <summary>Lets one file be tried again by the next indexing run.</summary>
    public void RemoveExclusion(ExcludedFile file)
    {
        if (_exclusions.Remove(file.Path)) CurrentStatusMessage = Loc.T("Library.Excluded.Restored", file.FileName);
        RefreshExcluded();
    }

    [RelayCommand]
    public void ClearExclusions()
    {
        int count = _exclusions.Clear();
        CurrentStatusMessage = count == 0 ? Loc.T("Library.Excluded.NoneToRestore") : Loc.T("Library.Excluded.RestoredAll", count);
        RefreshExcluded();
    }

    /// <summary>One line per video being processed ("動画 a.mp4: 420 / 1,050 場面 (40%)"); empty otherwise.</summary>
    [ObservableProperty]
    private string _videoProgressText = string.Empty;

    private static readonly string CurrentModelTag = EmbeddingModelLocation.ModelTag(EmbeddingModelLocation.DefaultPrecision);

    /// <summary>Non-empty when the index was built with a different embedding model and must be rebuilt.</summary>
    [ObservableProperty]
    private string _indexModelWarning = string.Empty;

    private void CheckIndexModel(int assetCount)
    {
        // The settings of this profile were already read: the file is not read again for this check.
        var settings = _settings ??= _store.Load();
        bool compatible = _store.CheckIndexModelCompatibility(CurrentModelTag, assetCount, settings, out _);
        IndexModelWarning = compatible ? string.Empty : Loc.T("Library.IndexModelMismatch");
    }

    partial void OnAutoIndexChanged(bool value)
    {
        if (!_loadingSettings) SaveSettings();
    }

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

    /// <summary>
    /// Schedules the heavy part of the refresh (every indexed path, the files per folder, the folders of an index that
    /// was built elsewhere) to run when the screen is idle. It runs once per profile: a later visit of the page only
    /// refreshes the light numbers, which are on screen at once.
    /// </summary>
    public void ScheduleFullStatsRefresh()
    {
        if (_initialized) return; // the heavy work was already done for this profile
        _initialized = true;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _deferredRefreshCts, cts);
        previous?.Cancel(); // not disposed: the refresh that uses it may still be running

        var token = cts.Token;
        var queue = _dispatcherQueue;
        if (queue != null && queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = RunDeferredFullRefreshAsync(token)))
        {
            return; // it waits for a moment when the screen is free
        }

        // Without a dispatcher queue there is no idle moment to wait for: the refresh runs right away.
        _ = RunDeferredFullRefreshAsync(token);
    }

    /// <summary>The heavy refresh, in the background: it can be cancelled when another one is scheduled.</summary>
    private async Task RunDeferredFullRefreshAsync(CancellationToken token)
    {
        try
        {
            await RefreshLibraryStatsCoreAsync(full: true, token);
        }
        catch (OperationCanceledException)
        {
            // Another refresh took over.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error refreshing library stats: {ex.Message}");
        }
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
        var pending = Interlocked.Exchange(ref _deferredRefreshCts, null);
        pending?.Cancel(); // not disposed: the refresh that uses it may still be running

        await InitializeAsync();
    }

    [RelayCommand]
    public Task RefreshLibraryStatsAsync() => RefreshLibraryStatsCoreAsync(full: true);

    /// <summary>
    /// Loads the statistics cards. <paramref name="full"/> false is the light path used while an index is being built: it
    /// runs only the counts query, and leaves the per-file work (reading every indexed hash, counting the files of each
    /// folder, restoring folders) to a full refresh. That keeps the periodic UI refresh from loading every indexed path
    /// and recounting every folder while ingestion is writing, at the cost of the cards lagging a little. Completion,
    /// starting up and the manual actions always use the full path.
    /// The data version of the repository says whether the numbers on screen are still right: when it has not changed,
    /// neither the database nor the files per folder are read again.
    /// </summary>
    private async Task RefreshLibraryStatsCoreAsync(bool full, CancellationToken cancellationToken = default)
    {
        try
        {
            // The data version is read before the queries: a write that happens while they run leaves the cached
            // version behind the data, so the next refresh reads again instead of keeping a number that is too old.
            long version = _repository.DataVersion;
            bool countsAreCurrent = version >= 0 && version == _statsVersion;

            // A read that happened a moment ago is not repeated either: the numbers on screen are at most a moment
            // old, and a write in that window is picked up by the next refresh.
            if (!countsAreCurrent && _statsAt != DateTime.MinValue && DateTime.UtcNow - _statsAt < MinimumStatsInterval)
            {
                countsAreCurrent = true;
            }

            if (!countsAreCurrent)
            {
                // Counts and paths come from two light queries; the files themselves are only loaded when folders must be restored.
                var stats = await _repository.GetAssetStatsAsync(cancellationToken);
                TotalIndexedAssets = stats.Total;
                IndexedImageCount = stats.Images;
                IndexedPdfCount = stats.Pdfs;
                IndexedVideoCount = stats.Videos;
                IndexedDocumentCount = stats.Documents;
                HasIndexedAssets = stats.Total > 0;
                CheckIndexModel(stats.Total);
                _statsVersion = version;
                _statsAt = DateTime.UtcNow;
            }

            // While indexing, stop here: the hashes, per-folder counts and folder restoration are deferred to the full refresh.
            if (!full) return;

            // The paths and the files per folder are only read again when the indexed data has changed.
            if (version < 0 || version != _statsDataVersion)
            {
                var hashes = await _repository.GetAssetHashesAsync(cancellationToken);
                var folders = TargetFolders.ToList();
                // Turning the paths into a list and counting them walks every indexed file: that is done off the UI thread.
                var counted = await Task.Run(() =>
                {
                    var paths = hashes.Keys.ToList();
                    return (Paths: paths, Counts: FolderListing.CountFiles(folders, paths));
                }, cancellationToken);
                _indexedPaths = counted.Paths;
                _folderCounts = counted.Counts;
                _statsDataVersion = version;
            }
            SyncFolderItems(recount: false); // the number of files per folder

            // Automatically restore folders from previously indexed assets if target folders is empty
            if (TargetFolders.Count == 0 && TotalIndexedAssets > 0 && !_store.Exists)
            {
                var dirs = (await _repository.GetAllAssetsAsync(cancellationToken))
                    .Select(a => Path.GetDirectoryName(a.FilePath))
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Select(d => d!)
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                AddFolders(dirs);
            }

            HasFolders = TargetFolders.Count > 0;
        }
        catch (OperationCanceledException)
        {
            // The refresh was replaced by a newer one.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error refreshing library stats: {ex.Message}");
        }
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

    [RelayCommand]
    public void AddFolder(string folderPath)
    {
        if (!string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath) && !TargetFolders.Contains(folderPath))
        {
            TargetFolders.Add(folderPath);
            HasFolders = TargetFolders.Count > 0;
            SaveSettings();
        }
    }

    [RelayCommand]
    public void RemoveFolder(string folderPath)
    {
        TargetFolders.Remove(folderPath);
        HasFolders = TargetFolders.Count > 0;
        SaveSettings();
    }

    private void SaveSettings()
    {
        try
        {
            // The settings of this profile were already read: they are not read again for every change.
            var settings = _settings ??= _store.Load();
            settings.Folders = TargetFolders.ToList();
            settings.AutoIndex = AutoIndex;
            settings.VideoAutoSegment = VideoAutoSegment;
            settings.UseFfmpegDecoder = UseFfmpegDecoder;
            settings.IndexImages = IndexImages;
            settings.IndexPdfs = IndexPdfs;
            settings.IndexVideos = IndexVideos;
            settings.IndexDocuments = IndexDocuments;
            settings.FolderSortIndex = FolderSortIndex;
            if (VideoSegmentIndex >= 0 && VideoSegmentIndex < VideoSegmentChoices.Count)
            {
                settings.VideoSegmentSeconds = VideoSegmentChoices[VideoSegmentIndex];
            }
            _store.Save(settings);
            _watcher.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = Loc.T("Library.SettingsSaveFailed", ex.Message);
        }
    }

    [RelayCommand]
    public async Task StartIndexingAsync()
    {
        if (IsIndexing)
        {
            IngestTrace.Write("app:indexing-start skipped (already running)");
            return;
        }
        if (TargetFolders.Count == 0)
        {
            IngestTrace.Write("app:indexing-start skipped (no folders)");
            return;
        }
        IngestTrace.Write($"app:indexing-start folders={TargetFolders.Count}");

        // The engine is created in the background, so the run waits for it to be there (without the model nothing can
        // be embedded). An engine that is really not available is reported with its reason.
        bool engineReady = await _embeddingService.IsReadyAsync();
        if (!engineReady)
        {
            ErrorMessage = UnavailableEmbeddingService.ReasonOf(_embeddingService) ?? Loc.T("Library.Engine.Initializing");
            return;
        }

        if (!string.IsNullOrEmpty(IndexModelWarning))
        {
            ErrorMessage = IndexModelWarning;
            return;
        }

        IsIndexing = true;
        ErrorMessage = null;
        ProcessedFiles = 0;
        ProgressPercentage = 0;
        CurrentStatusMessage = Loc.T("Library.Scanning");

        try
        {
            _worker.Start();

            foreach (var folder in TargetFolders.ToList())
            {
                await _worker.QueueDirectoryAsync(folder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            // An exception here would come out of the command on the UI thread and end the app.
            EmbeddingSearchViewer_App.Services.CrashLog.Write("StartIndexing", ex);
            ErrorMessage = Loc.T("Library.Error", ex.Message);
        }
    }

    [RelayCommand]
    public async Task StopIndexingAsync()
    {
        await _worker.StopAsync();
        IsIndexing = false;
        CurrentStatusMessage = Loc.T("Library.Stopped");
        _ = RefreshLibraryStatsAsync(); // what was stored before the stop
    }

    [RelayCommand]
    public async Task RemoveMissingFilesAsync()
    {
        if (IsIndexing) return;
        try
        {
            var assets = await _repository.GetAllAssetsAsync();
            var missing = assets.Where(a => !File.Exists(a.FilePath)).ToList();
            if (missing.Count > 0)
            {
                await _repository.DeleteAssetsAsync(missing.Select(a => a.Id));
            }

            CurrentStatusMessage = missing.Count == 0
                ? Loc.T("Library.Missing.None")
                : Loc.T("Library.Missing.Removed", missing.Count);
            await RefreshLibraryStatsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.T("Library.Missing.Error", ex.Message);
        }
    }

    [RelayCommand]
    public async Task RemoveBlankImagesAsync()
    {
        if (IsIndexing) return;
        try
        {
            CurrentStatusMessage = Loc.T("Library.Blank.Checking");
            var assets = await _repository.GetAllAssetsAsync();
            var blank = await BlankImageCleanup.FindBlankImagesAsync(assets);
            if (blank.Count > 0)
            {
                await _repository.DeleteAssetsAsync(blank.Select(a => a.Id));
            }

            CurrentStatusMessage = blank.Count == 0
                ? Loc.T("Library.Blank.None")
                : Loc.T("Library.Blank.Removed", blank.Count);
            await RefreshLibraryStatsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.T("Library.Blank.Error", ex.Message);
        }
    }

    [RelayCommand]
    public async Task ClearIndexAsync(CancellationToken cancellationToken = default)
    {
        if (IsIndexing || IsClearing) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _clearCts = cts;
        IsClearing = true;
        try
        {
            ErrorMessage = null;
            CurrentStatusMessage = Loc.T("Library.Scanning");
            // COUNT only: GetAllAssetsAsync の全行実体化を避ける。重いDB処理はプールスレッドに逃がす。
            int count = (await Task.Run(async () => await _repository.GetAssetStatsAsync(cts.Token), cts.Token)).Total;
            cts.Token.ThrowIfCancellationRequested();
            await Task.Run(async () => await _repository.ClearAllAsync(cts.Token), cts.Token);

            CurrentStatusMessage = Loc.T("Library.Cleared", count);
            ProgressPercentage = 0;
            ApplyClearedStats();
        }
        catch (OperationCanceledException)
        {
            CurrentStatusMessage = Loc.T("Library.Status.Ready");
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.T("Library.Clear.Error", ex.Message);
        }
        finally
        {
            IsClearing = false;
            if (_clearCts == cts) _clearCts = null;
        }
    }

    /// <summary>クリア直後の軽量リフレッシュ: 空表示を確定させるだけでDB走査・CountFilesを回さない。</summary>
    private void ApplyClearedStats()
    {
        TotalIndexedAssets = 0;
        IndexedImageCount = 0;
        IndexedPdfCount = 0;
        IndexedVideoCount = 0;
        IndexedDocumentCount = 0;
        HasIndexedAssets = false;
        CheckIndexModel(0);

        _indexedPaths = Array.Empty<string>();
        _folderCounts = TargetFolders.Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f, _ => 0, StringComparer.OrdinalIgnoreCase);

        // The index is gone: the cached numbers and paths do not belong to it any more.
        _statsVersion = -1;
        _statsDataVersion = -1;
        _statsAt = DateTime.MinValue;

        SyncFolderItems(recount: false);
        HasFolders = TargetFolders.Count > 0;
    }
}
