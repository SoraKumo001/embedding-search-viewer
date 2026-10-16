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
using EmbeddingSearchViewer.Infrastructure.Sidecar;

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

    [ObservableProperty]
    private string _sidecarStatus = Loc.T("Library.Engine.Name");

    [ObservableProperty]
    private bool _isSidecarConnected = true;

    [ObservableProperty]
    private bool _isIndexing;

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
    private bool _hasFolders;

    [ObservableProperty]
    private bool _hasIndexedAssets;

    public ObservableCollection<string> TargetFolders { get; } = new();

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
        var saved = _store.Load();
        if (assetCount == 0)
        {
            // An empty index belongs to whatever model is installed now.
            AdoptCurrentModel(saved);
            IndexModelWarning = string.Empty;
        }
        else
        {
            IndexModelWarning = saved.IndexModelTag == CurrentModelTag
                ? string.Empty
                : Loc.T("Library.IndexModelMismatch");
        }
    }

    private void AdoptCurrentModel(LibrarySettings settings)
    {
        if (settings.IndexModelTag == CurrentModelTag) return;
        settings.IndexModelTag = CurrentModelTag;
        try { _store.Save(settings); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
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
        IndexExclusionStore exclusions)
    {
        _exclusions = exclusions;
        _store = store;
        _watcher = watcher;
        _worker = worker;
        _embeddingService = embeddingService;
        _repository = repository;

        _worker.ProgressChanged += OnProgressChanged;

        // While an index is being built, keep the statistics cards current (only when the data has changed).
        _statsRefresher = new LibraryStatsRefresher(repository, () => OnUiThreadAsync(RefreshLibraryStatsAsync), TimeSpan.FromSeconds(3));
    }

    public async Task InitializeAsync()
    {
        bool ready = await _embeddingService.IsReadyAsync();
        IsSidecarConnected = ready;

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
            _loadingSettings = false;
            HasFolders = TargetFolders.Count > 0;
        }
        SidecarStatus = ready
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
    public async Task RefreshLibraryStatsAsync()
    {
        try
        {
            var assets = await _repository.GetAllAssetsAsync();
            TotalIndexedAssets = assets.Count;
            IndexedImageCount = assets.Count(a => a.MediaType == MediaType.Image);
            IndexedPdfCount = assets.Count(a => a.MediaType == MediaType.Pdf);
            IndexedVideoCount = assets.Count(a => a.MediaType == MediaType.Video);
            HasIndexedAssets = assets.Count > 0;
            CheckIndexModel(assets.Count);

            // Automatically restore folders from previously indexed assets if target folders is empty
            if (TargetFolders.Count == 0 && assets.Count > 0 && !_store.Exists)
            {
                var dirs = assets
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
    private Task OnUiThreadAsync(Func<Task> work)
    {
        var dq = _dispatcherQueue ?? App.MainWindowDispatcherQueue;
        if (dq == null || dq.HasThreadAccess) return work();

        var done = new TaskCompletionSource();
        if (!dq.TryEnqueue(async () =>
            {
                try { await work(); done.SetResult(); }
                catch (Exception ex) { done.SetException(ex); }
            }))
        {
            done.SetCanceled();
        }
        return done.Task;
    }

    private Microsoft.UI.Dispatching.DispatcherQueue? _dispatcherQueue;

    public void SetDispatcherQueue(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;
    }

    private void OnProgressChanged(IngestionProgress progress)
    {
        void UpdateState()
        {
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

        var dq = _dispatcherQueue ?? App.MainWindowDispatcherQueue;
        if (dq != null && !dq.HasThreadAccess)
        {
            dq.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, UpdateState);
        }
        else
        {
            UpdateState();
        }
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

        _worker.Start();

        foreach (var folder in TargetFolders)
        {
            await _worker.QueueDirectoryAsync(folder, recursive: true);
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
            foreach (var asset in missing)
            {
                await _repository.DeleteAssetAsync(asset.Id);
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
            foreach (var asset in blank)
            {
                await _repository.DeleteAssetAsync(asset.Id);
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
    public async Task ClearIndexAsync()
    {
        if (IsIndexing) return;
        try
        {
            var assets = await _repository.GetAllAssetsAsync();
            foreach (var asset in assets)
            {
                await _repository.DeleteAssetAsync(asset.Id);
            }

            CurrentStatusMessage = Loc.T("Library.Cleared", assets.Count);
            ProgressPercentage = 0;
            await RefreshLibraryStatsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Loc.T("Library.Clear.Error", ex.Message);
        }
    }
}
