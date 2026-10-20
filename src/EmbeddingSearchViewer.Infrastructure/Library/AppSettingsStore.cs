using System;
using System.IO;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>Settings of the whole app (the same for every profile).</summary>
public sealed class AppSettings
{
    /// <summary>"auto" (follow Windows), "ja" or "en". Takes effect at the next start.</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Remember the search conditions (type, similarity, sort, filters) together with each history entry.</summary>
    public bool SaveSearchOptionsInHistory { get; set; } = true;
}

/// <summary>Keeps <see cref="AppSettings"/> in settings.json under LocalAppData.</summary>
public sealed class AppSettingsStore
{
    private readonly string _path;

    // The settings that were read last: the file is small, but reading it again and again (every view model asks for
    // them) costs IO that the start does not need.
    private AppSettings? _cached;

    public AppSettingsStore(string? path = null)
    {
        _path = path ?? AppDataPaths.DefaultSettingsPath;
    }

    /// <summary>The settings; they are read from the file once and then kept in memory.</summary>
    public AppSettings Load() => _cached ??= AtomicJsonFile.Load(_path, new AppSettings());

    public void Save(AppSettings settings)
    {
        _cached = settings;
        AtomicJsonFile.Save(_path, settings);
    }
}
