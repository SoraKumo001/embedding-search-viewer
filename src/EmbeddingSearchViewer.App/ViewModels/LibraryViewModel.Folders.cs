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

    /// <summary>True while the folders are changed in bulk, so the files per folder are not recounted for every single one.</summary>
    private bool _suspendSync;

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
}
