using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbeddingSearchViewer.Core.Localization;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>The search conditions saved with a history entry, so that running the entry again gives the same search.</summary>
public sealed class SearchHistoryOptions
{
    public int MediaTypeIndex { get; set; }
    public float MinSimilarity { get; set; }
    public int SortIndex { get; set; }
    public bool EnableFts { get; set; }
    public bool GroupVideos { get; set; } = true;
    public DateTimeOffset? DateFrom { get; set; }
    public DateTimeOffset? DateTo { get; set; }
    public double? MinSizeMb { get; set; }
    public double? MaxSizeMb { get; set; }
    public string? Folder { get; set; }
}

/// <summary>One remembered search: a text, an image, or both, optionally with the conditions it was run with.</summary>
public sealed class SearchHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? Text { get; set; }
    public string? ImagePath { get; set; }
    public SearchHistoryOptions? Options { get; set; }
    public bool Pinned { get; set; }
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && string.IsNullOrWhiteSpace(ImagePath);

    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            string image = string.IsNullOrWhiteSpace(ImagePath) ? string.Empty : Loc.T("Search.History.Image", Path.GetFileName(ImagePath));
            if (string.IsNullOrWhiteSpace(Text)) return image;
            return image.Length == 0 ? Text : $"{Text}  ({image})";
        }
    }

    public bool SameSearchAs(SearchHistoryEntry other)
        => string.Equals(Text ?? string.Empty, other.Text ?? string.Empty, StringComparison.CurrentCultureIgnoreCase)
           && string.Equals(ImagePath ?? string.Empty, other.ImagePath ?? string.Empty, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Keeps the recent searches (newest first, pinned ones always on top) in a JSON file under LocalAppData.
/// The file is <c>{ "Version": 2, "Entries": [...] }</c>; the older format, a plain list of query texts, is still read.
/// </summary>
public class SearchHistoryStore
{
    /// <summary>How many entries are kept besides the pinned ones.</summary>
    public const int MaxEntries = 20;

    public const int MaxPinned = 10;

    private sealed class FileContent
    {
        public int Version { get; set; } = 2;
        public List<SearchHistoryEntry> Entries { get; set; } = new();
    }

    private readonly Func<string> _pathProvider;
    private readonly object _lock = new();

    private string _path => _pathProvider();

    public SearchHistoryStore(string? path = null)
    {
        string fixedPath = path ?? AppDataPaths.DefaultSearchHistoryPath;
        _pathProvider = () => fixedPath;
    }

    /// <summary>Follows the current profile: every call resolves the path again.</summary>
    public SearchHistoryStore(Func<string> pathProvider)
    {
        _pathProvider = pathProvider;
    }

    /// <summary>Pinned entries first, then the others, each group newest first.</summary>
    public IReadOnlyList<SearchHistoryEntry> Load()
    {
        lock (_lock)
        {
            return Ordered(ReadStored());
        }
    }

    public IReadOnlyList<SearchHistoryEntry> Add(string query) => Add(new SearchHistoryEntry { Text = query });

    /// <summary>
    /// Puts a search on top, replacing an earlier one with the same text and image (which keeps its pin).
    /// Searches that are neither a text nor an image are ignored.
    /// </summary>
    public IReadOnlyList<SearchHistoryEntry> Add(SearchHistoryEntry entry)
    {
        entry.Text = entry.Text?.Trim();
        lock (_lock)
        {
            var list = ReadStored();
            if (entry.IsEmpty) return Ordered(list);

            var previous = list.FirstOrDefault(e => e.SameSearchAs(entry));
            if (previous != null)
            {
                entry.Id = previous.Id;
                entry.Pinned = previous.Pinned;
                list.Remove(previous);
            }
            entry.LastUsedUtc = DateTime.UtcNow;
            list.Insert(0, entry);

            // Only the unpinned ones are limited; drop the oldest of them.
            while (list.Count(e => !e.Pinned) > MaxEntries)
            {
                list.RemoveAt(list.FindLastIndex(e => !e.Pinned));
            }
            Write(list);
            return Ordered(list);
        }
    }

    public void Remove(Guid id)
    {
        lock (_lock)
        {
            var list = ReadStored();
            if (list.RemoveAll(e => e.Id == id) > 0) Write(list);
        }
    }

    /// <summary>Pins or unpins an entry. Returns false when it cannot be pinned (unknown id, or <see cref="MaxPinned"/> reached).</summary>
    public bool SetPinned(Guid id, bool pinned)
    {
        lock (_lock)
        {
            var list = ReadStored();
            var entry = list.FirstOrDefault(e => e.Id == id);
            if (entry == null) return false;
            if (entry.Pinned == pinned) return true;
            if (pinned && list.Count(e => e.Pinned) >= MaxPinned) return false;

            entry.Pinned = pinned;
            Write(list);
            return true;
        }
    }

    /// <summary>Removes the history; the pinned entries stay unless <paramref name="includePinned"/> is set.</summary>
    public void Clear(bool includePinned = false)
    {
        lock (_lock)
        {
            var list = ReadStored();
            list.RemoveAll(e => includePinned || !e.Pinned);
            Write(list);
        }
    }

    private static List<SearchHistoryEntry> Ordered(List<SearchHistoryEntry> list)
        => list.Where(e => e.Pinned).Concat(list.Where(e => !e.Pinned)).ToList();

    private List<SearchHistoryEntry> ReadStored()
    {
        try
        {
            string path = _path;
            if (!File.Exists(path)) return new List<SearchHistoryEntry>();

            string text = File.ReadAllText(path).TrimStart();
            if (text.StartsWith('['))
            {
                // The first version of the file: only the query texts, newest first.
                var texts = JsonSerializer.Deserialize<List<string>>(text) ?? new List<string>();
                return texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => new SearchHistoryEntry { Text = t.Trim() }).ToList();
            }

            var content = JsonSerializer.Deserialize<FileContent>(text);
            return content?.Entries?.Where(e => e != null && !e.IsEmpty).ToList() ?? new List<SearchHistoryEntry>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            // An unreadable history is treated as empty.
            return new List<SearchHistoryEntry>();
        }
    }

    private void Write(List<SearchHistoryEntry> list)
    {
        AtomicJsonFile.Save(_path, new FileContent { Entries = list }, new JsonSerializerOptions { WriteIndented = false });
    }
}
