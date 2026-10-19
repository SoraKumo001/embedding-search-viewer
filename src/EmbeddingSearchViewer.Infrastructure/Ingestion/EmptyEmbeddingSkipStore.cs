using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EmbeddingSearchViewer.Infrastructure.Library;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// A file the embedding service produced no embedding for (an almost single-colour image). The size and modification
/// time are those of the file when it was skipped: a file that has been replaced since no longer matches and is
/// embedded again. The record is the same size+modification-time pair as <c>MediaAsset.ComputeFileHash</c>.
/// </summary>
public sealed record SkippedEmbeddingFile(string Path, long Length, long LastWriteTicks, DateTime SkippedAtUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    // SkippedAtUtc is recorded but not used for a decision yet: a later version may use it to forget entries older
    // than some age, or to clear the memory when the embedding model changes.
}

/// <summary>
/// Files that were skipped because the embedding service returned nothing for them (blank pages), kept per profile as
/// JSON so the next run does not spend CPU embedding them again. This is deliberately separate from the search index
/// (nothing is written to MediaAssets/MediaChunks) and from <see cref="IndexExclusionStore"/> (which holds failures and
/// is shown in the UI): an empty embedding is not a failure, so remembering it must not change the excluded-files list.
/// </summary>
public sealed class EmptyEmbeddingSkipStore
{
    private readonly Func<string> _pathProvider;
    private readonly object _lock = new();
    private string? _loadedPath;
    private Dictionary<string, SkippedEmbeddingFile> _items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Follows the current profile: every call resolves the path again.</summary>
    public EmptyEmbeddingSkipStore(Func<string> pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _items.Count;
            }
        }
    }

    /// <summary>True when the file was skipped before and is still the same file (same size and modification time).</summary>
    public bool IsSkipped(string path, long length, long lastWriteTicks)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _items.TryGetValue(path, out var item) && item.Length == length && item.LastWriteTicks == lastWriteTicks;
        }
    }

    /// <summary>Remembers a batch of skipped files with a single write.</summary>
    public void AddRange(IEnumerable<SkippedEmbeddingFile> files)
    {
        lock (_lock)
        {
            EnsureLoaded();
            bool any = false;
            foreach (var file in files)
            {
                if (string.IsNullOrEmpty(file?.Path)) continue;
                _items[file.Path] = file;
                any = true;
            }
            if (any) Save();
        }
    }

    /// <summary>Forgets a file (it changed since it was skipped, so it must be embedded again).</summary>
    public bool Remove(string path)
    {
        lock (_lock)
        {
            EnsureLoaded();
            if (!_items.Remove(path)) return false;
            Save();
            return true;
        }
    }

    /// <summary>Lets all remembered files be embedded again; returns how many there were.</summary>
    public int Clear()
    {
        lock (_lock)
        {
            EnsureLoaded();
            int count = _items.Count;
            if (count == 0) return 0;
            _items.Clear();
            Save();
            return count;
        }
    }

    /// <summary>
    /// Drops entries whose file no longer exists (deleted after it was skipped). Returns how many were removed.
    /// Without this, deleted files stay in the memory forever. It is not run on every indexing run: only callers that
    /// explicitly want the list tidied up do so.
    /// </summary>
    public int PruneMissing()
    {
        lock (_lock)
        {
            EnsureLoaded();
            var missing = _items.Values.Where(i => !File.Exists(i.Path)).Select(i => i.Path).ToList();
            if (missing.Count == 0) return 0;
            foreach (var path in missing) _items.Remove(path);
            Save();
            return missing.Count;
        }
    }

    private void EnsureLoaded()
    {
        string path = _pathProvider();
        if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase)) return;

        _loadedPath = path;
        _items = new Dictionary<string, SkippedEmbeddingFile>(StringComparer.OrdinalIgnoreCase);
        var list = AtomicJsonFile.Load<List<SkippedEmbeddingFile>>(path, null!);
        if (list == null) return;
        foreach (var item in list.Where(i => !string.IsNullOrEmpty(i?.Path))) _items[item.Path] = item;
    }

    private void Save()
    {
        string path = _loadedPath ?? _pathProvider();
        AtomicJsonFile.Save(path, _items.Values.ToList());
    }
}
