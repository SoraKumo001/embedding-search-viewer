using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

public class LibrarySettings
{
    public List<string> Folders { get; set; } = new();
    public bool AutoIndex { get; set; }

    /// <summary>Length of one video segment in seconds (one frame is searched per segment). Applies to videos indexed from now on.</summary>
    public int VideoSegmentSeconds { get; set; } = 15;

    /// <summary>Lengthens the segments of long videos so that none needs more than <see cref="Ingestion.IngestionPipelineWorker.AutoMaxVideoSegments"/> segments.</summary>
    public bool VideoAutoSegment { get; set; } = true;

    /// <summary>Model that produced the current index; a different model makes the stored embeddings incomparable.</summary>
    public string? IndexModelTag { get; set; }
}

/// <summary>Persists the indexed-folder list and auto-index flag as JSON under LocalAppData.</summary>
public class LibrarySettingsStore
{
    private readonly Func<string> _pathProvider;
    private readonly object _lock = new();

    private string _path => _pathProvider();

    public LibrarySettingsStore(string? path = null)
    {
        string fixedPath = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer",
            "library.json");
        _pathProvider = () => fixedPath;
    }

    /// <summary>Follows the current profile: every call resolves the path again.</summary>
    public LibrarySettingsStore(Func<string> pathProvider)
    {
        _pathProvider = pathProvider;
    }

    public bool Exists => File.Exists(_path);

    public LibrarySettings Load()
    {
        lock (_lock)
        {
            try
            {
                string path = _path;
                if (File.Exists(path))
                {
                    return JsonSerializer.Deserialize<LibrarySettings>(File.ReadAllText(path)) ?? new LibrarySettings();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Corrupt or unreadable settings fall back to defaults.
            }
            return new LibrarySettings();
        }
    }

    public void Save(LibrarySettings settings)
    {
        lock (_lock)
        {
            string path = _path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
