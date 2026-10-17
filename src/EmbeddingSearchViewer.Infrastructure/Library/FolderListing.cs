using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>How the list of folders to index is ordered.</summary>
public enum FolderSortMode
{
    /// <summary>The order the folders were added in.</summary>
    Added = 0,
    Name = 1,
    /// <summary>The folders with the most indexed files first.</summary>
    Count = 2
}

/// <summary>Counting and ordering for the list of folders to index.</summary>
public static class FolderListing
{
    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>
    /// For every folder, how many of the files lie under it (in it or in any folder below it).
    /// A file under two listed folders (one inside the other) counts for both.
    /// </summary>
    public static Dictionary<string, int> CountFiles(IEnumerable<string> folders, IEnumerable<string> filePaths)
    {
        var prefixes = folders
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(f => (Folder: f, Prefix: f.TrimEnd(Separators) + "\\"))
            .ToList();
        var counts = prefixes.ToDictionary(p => p.Folder, _ => 0, StringComparer.OrdinalIgnoreCase);

        foreach (string file in filePaths)
        {
            foreach (var (folder, prefix) in prefixes)
            {
                if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) counts[folder]++;
            }
        }
        return counts;
    }

    /// <summary>The folders in the wanted order. Folders without a known count come last when sorting by count.</summary>
    public static List<string> Order(IReadOnlyList<string> folders, IReadOnlyDictionary<string, int> counts, FolderSortMode mode)
    {
        int CountOf(string f) => counts.TryGetValue(f, out int n) ? n : -1;

        return mode switch
        {
            FolderSortMode.Name => folders.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList(),
            FolderSortMode.Count => folders.OrderByDescending(CountOf).ThenBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList(),
            _ => folders.ToList()
        };
    }
}
