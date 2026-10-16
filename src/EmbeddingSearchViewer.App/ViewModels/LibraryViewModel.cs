using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Interfaces;
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

    [ObservableProperty]
    private string _sidecarStatus = "ONNX DirectML エンジン";

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
    private string _currentStatusMessage = "準備完了";

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
                : "このインデックスは別の埋め込みモデルで作成されています。検索結果が正しくならないため、「クリア」してからインデックスを作り直してください。";
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
        FolderWatcher watcher)
    {
        _store = store;
        _watcher = watcher;
        _worker = worker;
        _embeddingService = embeddingService;
        _repository = repository;

        _worker.ProgressChanged += OnProgressChanged;
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
            _loadingSettings = false;
            HasFolders = TargetFolders.Count > 0;
        }
        SidecarStatus = ready
            ? "ONNX DirectML エンジン準備完了"
            : UnavailableEmbeddingService.ReasonOf(_embeddingService) ?? "エンジン初期化中";

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
        CurrentStatusMessage = "準備完了";
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

            if (progress.IsRunning)
            {
                CurrentStatusMessage = progress.TotalFiles > 0
                    ? $"処理中 ({progress.ProcessedFiles}/{progress.TotalFiles}): {progress.CurrentFileName}"
                    : $"処理中: {progress.CurrentFileName}";

                ProgressPercentage = progress.TotalFiles > 0
                    ? Math.Min(100.0, (double)progress.ProcessedFiles / progress.TotalFiles * 100.0)
                    : 0;
            }
            else
            {
                CurrentStatusMessage = progress.ProcessedFiles >= progress.TotalFiles && progress.TotalFiles > 0
                    ? $"すべてのインデックス作成が完了しました ({progress.ProcessedFiles}/{progress.TotalFiles} 件)"
                      + (progress.SkippedFiles > 0 ? $"。うち {progress.SkippedFiles} 件は白紙(ほぼ一色)のため対象外にしました" : string.Empty)
                    : progress.CurrentFileName;

                if (progress.ProcessedFiles >= progress.TotalFiles && progress.TotalFiles > 0)
                {
                    ProgressPercentage = 100.0;
                    _ = RefreshLibraryStatsAsync();
                }
            }

            if (!string.IsNullOrEmpty(progress.ErrorMessage))
            {
                ErrorMessage = progress.ErrorMessage;
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
            _store.Save(new LibrarySettings { Folders = TargetFolders.ToList(), AutoIndex = AutoIndex, IndexModelTag = _store.Load().IndexModelTag });
            _watcher.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = $"設定を保存できませんでした: {ex.Message}";
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
        CurrentStatusMessage = "対象ファイルをスキャン中...";

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
        CurrentStatusMessage = "中断されました";
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
                ? "存在しないファイルはありませんでした"
                : $"存在しないファイル {missing.Count} 件をインデックスから除去しました";
            await RefreshLibraryStatsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"除去中にエラーが発生しました: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RemoveBlankImagesAsync()
    {
        if (IsIndexing) return;
        try
        {
            CurrentStatusMessage = "白紙(ほぼ一色)の画像を調べています...";
            var assets = await _repository.GetAllAssetsAsync();
            var blank = await BlankImageCleanup.FindBlankImagesAsync(assets);
            foreach (var asset in blank)
            {
                await _repository.DeleteAssetAsync(asset.Id);
            }

            CurrentStatusMessage = blank.Count == 0
                ? "白紙の画像はありませんでした"
                : $"白紙(ほぼ一色)の画像 {blank.Count} 件をインデックスから除去しました (ファイルは削除していません)";
            await RefreshLibraryStatsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"白紙画像の除去中にエラーが発生しました: {ex.Message}";
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

            CurrentStatusMessage = $"インデックスをクリアしました ({assets.Count} 件)";
            ProgressPercentage = 0;
            await RefreshLibraryStatsAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"クリア中にエラーが発生しました: {ex.Message}";
        }
    }
}
