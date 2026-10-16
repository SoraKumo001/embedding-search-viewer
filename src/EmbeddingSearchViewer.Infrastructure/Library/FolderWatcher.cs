using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Ingestion;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>
/// Watches the registered folders and keeps the index up to date when auto-index is enabled.
/// Changes are debounced so a file being copied is not read half-written.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly LibrarySettingsStore _store;
    private readonly IngestionPipelineWorker _worker;
    private readonly IMediaRepository _repository;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, bool> _changed = new(StringComparer.OrdinalIgnoreCase); // path -> exists?
    private readonly Timer _timer;
    private readonly object _lock = new();

    public FolderWatcher(LibrarySettingsStore store, IngestionPipelineWorker worker, IMediaRepository repository)
    {
        _store = store;
        _worker = worker;
        _repository = repository;
        _timer = new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Re-reads the saved settings and (re)creates the watchers accordingly.
    /// <paramref name="discardPending"/> drops changes that were noticed but not yet queued (used when the profile changes).
    /// </summary>
    public void Refresh(bool discardPending = false)
    {
        if (discardPending)
        {
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            _changed.Clear();
        }

        lock (_lock)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();

            var settings = _store.Load();
            if (!settings.AutoIndex) return;

            foreach (var folder in settings.Folders.Where(Directory.Exists))
            {
                try
                {
                    var w = new FileSystemWatcher(folder)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024
                    };
                    w.Created += (_, e) => OnChanged(e.FullPath, exists: true);
                    w.Changed += (_, e) => OnChanged(e.FullPath, exists: true);
                    w.Deleted += (_, e) => OnChanged(e.FullPath, exists: false);
                    w.Renamed += (_, e) =>
                    {
                        OnChanged(e.OldFullPath, exists: false);
                        OnChanged(e.FullPath, exists: true);
                    };
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    // Folder became unavailable; skip it.
                }
            }
        }
    }

    private void OnChanged(string path, bool exists)
    {
        _changed[path] = exists;
        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The index may only grow while it was built with the model in use; an empty index simply adopts the current model.
    /// </summary>
    private async Task<bool> IsIndexCompatibleAsync()
    {
        string current = Onnx.EmbeddingModelLocation.ModelTag(Onnx.EmbeddingModelLocation.DefaultPrecision);
        var settings = _store.Load();
        if (settings.IndexModelTag == current) return true;

        if ((await _repository.GetAllAssetsAsync()).Count > 0) return false;

        settings.IndexModelTag = current;
        _store.Save(settings);
        return true;
    }

    private async Task FlushAsync()
    {
        try
        {
            if (!await IsIndexCompatibleAsync())
            {
                _changed.Clear(); // the user has to rebuild the index first; a rebuild picks these files up anyway
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in _changed.Keys.ToList())
        {
            if (!_changed.TryRemove(path, out bool exists)) continue;
            try
            {
                if (exists && File.Exists(path))
                {
                    await _worker.QueueFileAsync(path);
                }
                else if (!exists)
                {
                    var asset = await _repository.GetAssetByPathAsync(path);
                    if (asset != null) await _repository.DeleteAssetAsync(asset.Id);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Transient file access problems are retried on the next change event.
            }
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_lock)
        {
            foreach (var w in _watchers) w.Dispose();
            _watchers.Clear();
        }
    }
}
