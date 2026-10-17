using System;
using System.IO;
using System.Text.Json;

namespace EmbeddingSearchViewer.Infrastructure.Library;

public static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions DefaultOptions = new() { WriteIndented = true };

    public static T Load<T>(string path, T defaultValue) where T : class?
    {
        try
        {
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(text) ?? defaultValue;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Corrupt or unreadable file returns default
        }
        return defaultValue;
    }

    public static bool Save<T>(string path, T data, JsonSerializerOptions? options = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, options ?? DefaultOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
