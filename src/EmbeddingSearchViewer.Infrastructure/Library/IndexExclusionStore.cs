using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>
/// A file that could not be read even after retries. The size and modification time are those of the file when it
/// failed: a file that has been replaced or repaired since no longer matches and is tried again.
/// </summary>
public sealed record ExcludedFile(string Path, long Length, long LastWriteTicks, string Error, DateTime FailedAtUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string FailedAtText => FailedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}

/// <summary>
/// Files left out of the index because reading them failed repeatedly (most likely they are damaged). Kept per profile as
/// JSON, so the next indexing run does not spend time, and retries, on them again.
/// </summary>
public sealed class IndexExclusionStore
{
    private readonly Func<string> _pathProvider;
    private readonly object _lock = new();
    private string? _loadedPath;
    private Dictionary<string, ExcludedFile> _items = new(StringComparer.OrdinalIgnoreCase);

    public IndexExclusionStore(string? path = null)
    {
        string fixedPath = path ?? AppDataPaths.DefaultExclusionsPath;
        _pathProvider = () => fixedPath;
    }

    /// <summary>Follows the current profile: every call resolves the path again.</summary>
    public IndexExclusionStore(Func<string> pathProvider)
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

    /// <summary>All the excluded files, the most recent failure first.</summary>
    public IReadOnlyList<ExcludedFile> List()
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _items.Values.OrderByDescending(i => i.FailedAtUtc).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    /// <summary>True when the file failed before and is still the same file (same size and modification time).</summary>
    public bool IsExcluded(string path, long length, long lastWriteTicks)
    {
        lock (_lock)
        {
            EnsureLoaded();
            return _items.TryGetValue(path, out var item) && item.Length == length && item.LastWriteTicks == lastWriteTicks;
        }
    }

    public void Add(ExcludedFile file)
    {
        lock (_lock)
        {
            EnsureLoaded();
            _items[file.Path] = file;
            Save();
        }
    }

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

    /// <summary>Lets all excluded files be tried again; returns how many there were.</summary>
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
    /// Drops entries whose file no longer exists (deleted after it failed). Returns how many were removed.
    /// Without this, deleted files stay in "files left out of the index" forever.
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
        _items = new Dictionary<string, ExcludedFile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return;
            var list = JsonSerializer.Deserialize<List<ExcludedFile>>(File.ReadAllText(path));
            if (list == null) return;
            foreach (var item in list.Where(i => !string.IsNullOrEmpty(i?.Path))) _items[item.Path] = item;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // An unreadable list only means that those files are tried (and, if they fail again, excluded) once more.
        }
    }

    private void Save()
    {
        string path = _loadedPath ?? _pathProvider();
        AtomicJsonFile.Save(path, _items.Values.ToList());
    }
}
