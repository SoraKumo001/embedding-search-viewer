using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>Keeps the most recent distinct text queries (newest first) in a JSON file under LocalAppData.</summary>
public class SearchHistoryStore
{
    public const int MaxEntries = 20;

    private readonly Func<string> _pathProvider;
    private readonly object _lock = new();

    private string _path => _pathProvider();

    public SearchHistoryStore(string? path = null)
    {
        string fixedPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer",
            "search_history.json");
        _pathProvider = () => fixedPath;
    }

    /// <summary>Follows the current profile: every call resolves the path again.</summary>
    public SearchHistoryStore(Func<string> pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public IReadOnlyList<string> Load()
    {
        lock (_lock)
        {
            try
            {
                string path = _path;
                if (File.Exists(path))
                {
                    return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // An unreadable history is treated as empty.
            }
            return new List<string>();
        }
    }

    /// <summary>Adds a query to the top, removing earlier duplicates, and returns the updated list.</summary>
    public IReadOnlyList<string> Add(string query)
    {
        query = query.Trim();
        lock (_lock)
        {
            var list = Load().ToList();
            if (query.Length == 0) return list;

            list.RemoveAll(q => string.Equals(q, query, StringComparison.CurrentCultureIgnoreCase));
            list.Insert(0, query);
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            Write(list);
            return list;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            Write(new List<string>());
        }
    }

    private void Write(List<string> list)
    {
        try
        {
            string path = _path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(list));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; failing to persist it must not break searching.
        }
    }
}
