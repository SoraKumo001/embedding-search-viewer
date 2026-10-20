using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

public class LibrarySettings
{
    public List<string> Folders { get; set; } = new();
    public bool AutoIndex { get; set; }

    /// <summary>Kinds of files to put in the index. Files already indexed stay in it when a kind is switched off.</summary>
    public bool IndexImages { get; set; } = true;
    public bool IndexPdfs { get; set; } = true;
    public bool IndexVideos { get; set; } = true;
    public bool IndexDocuments { get; set; } = true;

    /// <summary>Length of one video segment in seconds (one frame is searched per segment). Applies to videos indexed from now on.</summary>
    public int VideoSegmentSeconds { get; set; } = 15;

    /// <summary>Lengthens the segments of long videos so that none needs more than <see cref="Ingestion.IngestionPipelineWorker.AutoMaxVideoSegments"/> segments.</summary>
    public bool VideoAutoSegment { get; set; } = true;

    /// <summary>
    /// Videos are decoded with FFmpeg first (the Windows media stack as the fallback) when true, or with the Windows media
    /// stack alone when false. Applies to videos indexed from now on; a missing key keeps the previous behaviour (true).
    /// </summary>
    public bool UseFfmpegDecoder { get; set; } = true;

    /// <summary>How the folder list is ordered on screen (<see cref="FolderSortMode"/>); the saved order of <see cref="Folders"/> is not changed.</summary>
    public int FolderSortIndex { get; set; }

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
        string fixedPath = path ?? AppDataPaths.DefaultLibraryPath;
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
            return AtomicJsonFile.Load(_path, new LibrarySettings());
        }
    }

    public void Save(LibrarySettings settings)
    {
        lock (_lock)
        {
            AtomicJsonFile.Save(_path, settings);
        }
    }

    /// <summary>
    /// Checks whether the index matches the expected model tag. If the index is empty, adopts the expected model tag and saves it.
    /// </summary>
    /// <returns>True if compatible or adopted; false if mismatched with existing indexed assets.</returns>
    public bool CheckIndexModelCompatibility(string expectedModelTag, int assetCount, out bool wasAdopted)
    {
        return CheckIndexModelCompatibility(expectedModelTag, assetCount, Load(), out wasAdopted);
    }

    /// <summary>
    /// The same check, but with settings that were already read: the file is not read a second time.
    /// If the index is empty, the expected model tag is adopted and saved (only when it differs, so an empty index
    /// writes the file once).
    /// </summary>
    /// <returns>True if compatible or adopted; false if mismatched with existing indexed assets.</returns>
    public bool CheckIndexModelCompatibility(string expectedModelTag, int assetCount, LibrarySettings settings, out bool wasAdopted)
    {
        wasAdopted = false;
        if (assetCount == 0)
        {
            if (settings.IndexModelTag != expectedModelTag)
            {
                settings.IndexModelTag = expectedModelTag;
                Save(settings);
                wasAdopted = true;
            }
            return true;
        }

        return settings.IndexModelTag == expectedModelTag;
    }
}
