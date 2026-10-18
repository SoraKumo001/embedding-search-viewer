using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
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
        var order = FolderListing.Order(TargetFolders.ToList(), counts, mode);
        for (int i = 0; i < order.Count; i++)
        {
            int from = FolderItems.ToList().FindIndex(x => x.Path == order[i]);
            if (from != i) FolderItems.Move(from, i);
        }
    }

    [ObservableProperty]
    private bool _autoIndex;

    /// <summary>The choices for the length of a video segment, in seconds; the first is the standard.</summary>
    public static IReadOnlyList<int> VideoSegmentChoices { get; } = new[] { 15, 30, 60, 120 };

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

    // ---- files left out of the index because reading them failed repeatedly ----

    public ObservableCollection<ExcludedFile> ExcludedFiles { get; } = new();

    [ObservableProperty]
    private bool _hasExcluded;

    [ObservableProperty]
    private string _excludedHeaderText = string.Empty;

    /// <summary>Re-reads the list of excluded files (they are added by the indexing worker, on its own thread).</summary>
    public void RefreshExcluded()
    {
        var items = _exclusions.List();
        ExcludedFiles.Clear();
        foreach (var item in items) ExcludedFiles.Add(item);
        HasExcluded = items.Count > 0;
        ExcludedHeaderText = items.Count > 0 ? Loc.T("Library.ExcludedHeader", items.Count) : string.Empty;
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
        bool compatible = _store.CheckIndexModelCompatibility(CurrentModelTag, assetCount, out _);
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
        IUiDispatcher? dispatcher = null)
    {
        _exclusions = exclusions;
        _store = store;
        _watcher = watcher;
        _worker = worker;
        _embeddingService = embeddingService;
        _repository = repository;
        _dispatcher = dispatcher ?? EmbeddingSearchViewer.Core.Services.ImmediateDispatcher.Instance;

        _worker.ProgressChanged += OnProgressChanged;
        TargetFolders.CollectionChanged += (_, _) => SyncFolderItems();

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
        bool ready = await _embeddingService.IsReadyAsync();
        IsEngineReady = ready;

        if (TargetFolders.Count == 0)
        {
            _loadingSettings = true;
            var saved = _store.Load();
            foreach (var folder in saved.Folders.Where(Directory.Exists))
            {
                TargetFolders.Add(folder);
            }
            AutoIndex = saved.AutoIndex;
            VideoSegmentIndex = Math.Max(0, VideoSegmentChoices.ToList().IndexOf(saved.VideoSegmentSeconds));
            VideoAutoSegment = saved.VideoAutoSegment;
            IndexImages = saved.IndexImages;
            IndexPdfs = saved.IndexPdfs;
            IndexVideos = saved.IndexVideos;
            IndexDocuments = saved.IndexDocuments;
            FolderSortIndex = saved.FolderSortIndex;
            _loadingSettings = false;
            HasFolders = TargetFolders.Count > 0;
        }
        EngineStatus = ready
            ? Loc.T("Library.Engine.Ready")
            : UnavailableEmbeddingService.ReasonOf(_embeddingService) ?? Loc.T("Library.Engine.Initializing");

        RefreshExcluded();
        await RefreshLibraryStatsAsync();
    }

    /// <summary>Drops everything shown for the previous profile and loads the folders and statistics of the new one.</summary>
    public async Task ReloadForProfileChangeAsync()
    {
        TargetFolders.Clear();
        HasFolders = false;
        TotalFiles = 0;
        ProcessedFiles = 0;
        ProgressPercentage = 0;
        ErrorMessage = null;
        IndexModelWarning = string.Empty;
        CurrentStatusMessage = Loc.T("Library.Status.Ready");
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
    /// </summary>
    private async Task RefreshLibraryStatsCoreAsync(bool full)
    {
        try
        {
            // Counts and paths come from two light queries; the files themselves are only loaded when folders must be restored.
            var stats = await _repository.GetAssetStatsAsync();
            TotalIndexedAssets = stats.Total;
            IndexedImageCount = stats.Images;
            IndexedPdfCount = stats.Pdfs;
            IndexedVideoCount = stats.Videos;
            IndexedDocumentCount = stats.Documents;
            HasIndexedAssets = stats.Total > 0;
            CheckIndexModel(stats.Total);

            // While indexing, stop here: the hashes, per-folder counts and folder restoration are deferred to the full refresh.
            if (!full) return;

            _indexedPaths = (await _repository.GetAssetHashesAsync()).Keys.ToList();
            var folders = TargetFolders.ToList();
            var paths = _indexedPaths;
            _folderCounts = await Task.Run(() => FolderListing.CountFiles(folders, paths)); // off the UI thread
            SyncFolderItems(recount: false); // the number of files per folder

            // Automatically restore folders from previously indexed assets if target folders is empty
            if (TargetFolders.Count == 0 && stats.Total > 0 && !_store.Exists)
            {
                var dirs = (await _repository.GetAllAssetsAsync())
                    .Select(a => Path.GetDirectoryName(a.FilePath))
                    .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var dir in dirs)
                {
                    if (dir != null && !TargetFolders.Contains(dir))
                    {
                        TargetFolders.Add(dir);
                    }
                }
            }

            HasFolders = TargetFolders.Count > 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error refreshing library stats: {ex.Message}");
        }
    }

    partial void OnIsIndexingChanged(bool value)
    {
        if (value) _statsRefresher.Start();
        else _statsRefresher.Stop();
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
            var settings = _store.Load();
            settings.Folders = TargetFolders.ToList();
            settings.AutoIndex = AutoIndex;
            settings.VideoAutoSegment = VideoAutoSegment;
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
        if (TargetFolders.Count == 0) return;

        if (UnavailableEmbeddingService.ReasonOf(_embeddingService) is { } unavailableReason)
        {
            ErrorMessage = unavailableReason;
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
        SyncFolderItems(recount: false);
        HasFolders = TargetFolders.Count > 0;
    }
}
