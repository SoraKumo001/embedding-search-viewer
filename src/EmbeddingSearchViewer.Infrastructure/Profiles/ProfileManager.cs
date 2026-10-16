using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace EmbeddingSearchViewer.Infrastructure.Profiles;

public record ProfileInfo(string Id, string Name);

internal class ProfileFile
{
    public List<ProfileInfo> Profiles { get; set; } = new();
    public string? CurrentId { get; set; }
}

/// <summary>
/// Keeps independent indexes ("work", "hobby", ...) side by side. Each profile owns its database, folder list,
/// search history and cache under profiles\&lt;id&gt;; the list and the last used profile live in profiles.json.
/// </summary>
public sealed class ProfileManager
{
    public const string DefaultId = "default";
    public const string DefaultName = "標準";

    private readonly string _root;
    private readonly string _listPath;
    private readonly object _lock = new();
    private ProfileFile _file = new();

    public event EventHandler? Changed;

    public ProfileManager(string? rootDirectory = null)
    {
        _root = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer");
        _listPath = Path.Combine(_root, "profiles.json");
        Directory.CreateDirectory(_root);
        Load();
    }

    public IReadOnlyList<ProfileInfo> Profiles
    {
        get { lock (_lock) return _file.Profiles.ToList(); }
    }

    public ProfileInfo Current
    {
        get { lock (_lock) return _file.Profiles.First(p => p.Id == _file.CurrentId); }
    }

    public string CurrentDirectory => DirectoryOf(Current.Id);
    public string DatabasePath => Path.Combine(CurrentDirectory, "media_index.db");
    public string LibraryPath => Path.Combine(CurrentDirectory, "library.json");
    public string HistoryPath => Path.Combine(CurrentDirectory, "search_history.json");
    public string ExclusionsPath => Path.Combine(CurrentDirectory, "excluded_files.json");
    public string CacheDirectory => Path.Combine(CurrentDirectory, "cache");

    private string DirectoryOf(string id) => Path.Combine(_root, "profiles", id);

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_listPath))
                {
                    _file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(_listPath)) ?? new ProfileFile();
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _file = new ProfileFile(); // a broken list falls back to the default profile; the data folders stay untouched
            }

            _file.Profiles = _file.Profiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                .ToList();

            if (_file.Profiles.Count == 0)
            {
                _file.Profiles.Add(new ProfileInfo(DefaultId, DefaultName));
                MigrateLegacyData();
            }
            if (_file.Profiles.All(p => p.Id != _file.CurrentId)) _file.CurrentId = _file.Profiles[0].Id;

            Directory.CreateDirectory(CurrentDirectory);
            Save();
        }
    }

    /// <summary>
    /// Moves the single-index layout of earlier versions into the default profile.
    /// The database moves together with its -wal/-shm files or not at all: if another running instance still has it
    /// open, starting is refused rather than leaving the default profile with an empty index.
    /// </summary>
    private void MigrateLegacyData()
    {
        string target = DirectoryOf(DefaultId);
        Directory.CreateDirectory(target);

        string db = Path.Combine(_root, "media_index.db");
        string dbTarget = Path.Combine(target, "media_index.db");
        if (File.Exists(db) && !File.Exists(dbTarget))
        {
            MoveDatabase(db, dbTarget);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(db + suffix)) File.Move(db + suffix, dbTarget + suffix, overwrite: true);
            }
        }

        foreach (var name in new[] { "library.json", "search_history.json" })
        {
            string from = Path.Combine(_root, name);
            string to = Path.Combine(target, name);
            if (File.Exists(from) && !File.Exists(to)) TryMove(() => File.Move(from, to));
        }

        string cacheFrom = Path.Combine(_root, "cache");
        string cacheTo = Path.Combine(target, "cache");
        if (Directory.Exists(cacheFrom) && !Directory.Exists(cacheTo)) TryMove(() => Directory.Move(cacheFrom, cacheTo));
    }

    private static void MoveDatabase(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                {
                    throw new IOException(
                        "既存のインデックスを新しい保存場所へ移動できませんでした。古いバージョンのアプリが起動したままの可能性があります。" +
                        "すべてのウィンドウを閉じてから、もう一度起動してください。", ex);
                }
                Thread.Sleep(300);
            }
        }
    }

    private static void TryMove(Action move)
    {
        try { move(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked legacy file stays where it is; the default profile starts without it.
        }
    }

    private void Save()
    {
        string tmp = _listPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _listPath, overwrite: true);
    }

    private static string NormalizeName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException("プロファイル名を入力してください。", nameof(name));
        return name;
    }

    private void EnsureNameUnused(string name, string? exceptId = null)
    {
        if (_file.Profiles.Any(p => p.Id != exceptId && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException($"「{name}」という名前のプロファイルがすでにあります。");
    }

    /// <summary>Creates an empty profile; it does not become the current one.</summary>
    public ProfileInfo Create(string name)
    {
        name = NormalizeName(name);
        lock (_lock)
        {
            EnsureNameUnused(name);
            var profile = new ProfileInfo(Guid.NewGuid().ToString("N")[..12], name);
            _file.Profiles.Add(profile);
            Directory.CreateDirectory(DirectoryOf(profile.Id));
            Save();
            return profile;
        }
    }

    public void Rename(string id, string name)
    {
        name = NormalizeName(name);
        lock (_lock)
        {
            int index = _file.Profiles.FindIndex(p => p.Id == id);
            if (index < 0) throw new InvalidOperationException("プロファイルが見つかりません。");
            EnsureNameUnused(name, id);
            _file.Profiles[index] = new ProfileInfo(id, name);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Switch(string id)
    {
        lock (_lock)
        {
            if (_file.CurrentId == id) return false;
            if (_file.Profiles.All(p => p.Id != id)) throw new InvalidOperationException("プロファイルが見つかりません。");
            _file.CurrentId = id;
            Directory.CreateDirectory(CurrentDirectory);
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Deletes a profile that is not the current one, with all its data (the indexed source files are untouched).
    /// The last remaining profile cannot be deleted.
    /// </summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            if (_file.CurrentId == id) throw new InvalidOperationException("使用中のプロファイルは削除できません。別のプロファイルに切り替えてから削除してください。");
            if (_file.Profiles.RemoveAll(p => p.Id == id) == 0) return;
            Save();
        }

        try { Directory.Delete(DirectoryOf(id), recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The profile is already gone from the list; leftover files are harmless.
        }
    }
}
