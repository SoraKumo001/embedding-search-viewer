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

public partial class LibraryViewModel
{
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

    /// <summary>
    /// True until the first numbers are read. The counts start at zero, so an empty library and a screen that is still
    /// loading would look the same; the cards show a dash while this is set. It starts true so even the first frame,
    /// before the page has run its start-up, does not show a zero that is not real.
    /// </summary>
    [ObservableProperty]
    private bool _isStatsLoading = true;

    /// <summary>True while the numbers of the start-up are still on their way; a later periodic refresh leaves the flag alone.</summary>
    private bool _statsLoadPending;

    /// <summary>True once the first read of the numbers has come back; a later visit of the page keeps them on screen.</summary>
    private bool _statsShown;

    /// <summary>True once the one-time work of the start-up (the folders and the heavy refresh) is done for this profile.</summary>
    private bool _initialized;

    /// <summary>The data version the numbers on screen come from; the same version means they are still right.</summary>
    private long _statsVersion = -1;

    /// <summary>The data version the cached paths and the files per folder come from.</summary>
    private long _statsDataVersion = -1;

    /// <summary>When the numbers on screen were read.</summary>
    private DateTime _statsAt = DateTime.MinValue;

    /// <summary>Cancels the delayed heavy refresh when another one is scheduled.</summary>
    private CancellationTokenSource? _deferredRefreshCts;

    /// <summary>The shortest time between two reads of the statistics; a read that just happened is not repeated.</summary>
    private static readonly TimeSpan MinimumStatsInterval = TimeSpan.FromMilliseconds(250);

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
        finally
        {
            // Only the first read takes the dashes away: the periodic refresh while an index is being built must not
            // make the numbers disappear and come back on every tick. A failed first read also ends the wait, so the
            // cards do not stay on the dash for ever.
            if (_statsLoadPending)
            {
                _statsLoadPending = false;
                _statsShown = true;
                IsStatsLoading = false;
            }
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
