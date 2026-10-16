using System;
using System.IO;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>Settings of the whole app (the same for every profile).</summary>
public sealed class AppSettings
{
    /// <summary>"auto" (follow Windows), "ja" or "en". Takes effect at the next start.</summary>
    public string Language { get; set; } = "auto";
}

/// <summary>Keeps <see cref="AppSettings"/> in settings.json under LocalAppData.</summary>
public sealed class AppSettingsStore
{
    private readonly string _path;

    public AppSettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer",
            "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Unreadable settings fall back to the defaults.
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to remember the language must not get in the way of using the app.
        }
    }
}
