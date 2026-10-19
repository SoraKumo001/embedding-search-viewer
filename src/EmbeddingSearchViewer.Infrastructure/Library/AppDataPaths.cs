using System;
using System.IO;

namespace EmbeddingSearchViewer.Infrastructure.Library;

public static class AppDataPaths
{
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmbeddingSearchViewer");

    public static string DefaultModelsDirectory => Path.Combine(RootDirectory, "models");
    public static string DefaultCacheDirectory => Path.Combine(RootDirectory, "cache");
    public static string DefaultDatabasePath => Path.Combine(RootDirectory, "media_index.db");
    public static string DefaultLibraryPath => Path.Combine(RootDirectory, "library.json");
    public static string DefaultSettingsPath => Path.Combine(RootDirectory, "settings.json");
    public static string DefaultWindowPlacementPath => Path.Combine(RootDirectory, "window.json");
    public static string DefaultSearchHistoryPath => Path.Combine(RootDirectory, "search_history.json");
    public static string DefaultExclusionsPath => Path.Combine(RootDirectory, "excluded_files.json");
    public static string DefaultEmptySkipsPath => Path.Combine(RootDirectory, "empty_embedding_skips.json");
    public static string DefaultProfilesPath => Path.Combine(RootDirectory, "profiles.json");
}
