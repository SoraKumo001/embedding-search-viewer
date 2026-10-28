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
    /// <summary>Counts the requests for the excluded files, so an older answer is dropped when a newer one is on the way.</summary>
    private int _excludedRequestId;

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
}
