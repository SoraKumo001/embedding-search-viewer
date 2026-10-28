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
}
