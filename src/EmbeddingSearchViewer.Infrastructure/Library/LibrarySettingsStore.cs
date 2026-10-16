using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

public class LibrarySettings
{
    public List<string> Folders { get; set; } = new();
    public bool AutoIndex { get; set; }

    /// <summary>Model that produced the current index; a different model makes the stored embeddings incomparable.</summary>
    public string? IndexModelTag { get; set; }
}

/// <summary>Persists the indexed-folder list and auto-index flag as JSON under LocalAppData.</summary>
public class LibrarySettingsStore
{
    private readonly string _path;
    private readonly object _lock = new();

    public LibrarySettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer",
            "library.json");
    }

    public bool Exists => File.Exists(_path);

    public LibrarySettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    return JsonSerializer.Deserialize<LibrarySettings>(File.ReadAllText(_path)) ?? new LibrarySettings();
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
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
