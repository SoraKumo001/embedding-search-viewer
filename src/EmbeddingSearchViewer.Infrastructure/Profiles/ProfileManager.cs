using System;
using EmbeddingSearchViewer.Core.Localization;
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

    /// <summary>Name of the per-profile file that remembers files the embedding service returned nothing for.</summary>
    public const string EmptySkipsFileName = "empty_embedding_skips.json";

    private readonly string _root;
    private readonly string _listPath;
    private readonly object _lock = new();
    private ProfileFile _file = new();

    public event EventHandler? Changed;

    public ProfileManager(string? rootDirectory = null)
    {
        _root = rootDirectory ?? EmbeddingSearchViewer.Infrastructure.Library.AppDataPaths.RootDirectory;
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
    public string DatabasePath => DatabasePathOf(Current.Id);
    public string LibraryPath => LibraryPathOf(Current.Id);
    public string HistoryPath => HistoryPathOf(Current.Id);
    public string ExclusionsPath => ExclusionsPathOf(Current.Id);
    public string EmptySkipsPath => EmptySkipsPathOf(Current.Id);
    public string CacheDirectory => CacheDirectoryOf(Current.Id);

    /// <summary>The folder that holds profiles.json and the profiles folder (the root the manager was created with).</summary>
    public string RootDirectory => _root;

    /// <summary>The folder of the given profile, current or not.</summary>
    public string DirectoryOf(string id) => Path.Combine(_root, "profiles", id);

    // Per-profile paths, so a backup can address a profile other than the current one. The current-profile properties
    // above are just these applied to <see cref="Current"/>.
    public string DatabasePathOf(string id) => Path.Combine(DirectoryOf(id), "media_index.db");
    public string LibraryPathOf(string id) => Path.Combine(DirectoryOf(id), "library.json");
    public string HistoryPathOf(string id) => Path.Combine(DirectoryOf(id), "search_history.json");
    public string ExclusionsPathOf(string id) => Path.Combine(DirectoryOf(id), "excluded_files.json");
    public string EmptySkipsPathOf(string id) => Path.Combine(DirectoryOf(id), EmptySkipsFileName);
    public string CacheDirectoryOf(string id) => Path.Combine(DirectoryOf(id), "cache");


    private void Load()
    {
        DetectInterruptedRestore();
        lock (_lock)
        {
            // The list is only written when this start really changes it; a start that finds everything in order
            // leaves the file (and its time stamp) alone.
            bool dirty = false;

            try
            {
                if (File.Exists(_listPath))
                {
                    _file = JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(_listPath)) ?? new ProfileFile();
                }
                else
                {
                    dirty = true; // the list is written for the first time
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _file = new ProfileFile(); // a broken list falls back to the default profile; the data folders stay untouched
                dirty = true; // the unusable file is replaced by a list that can be read
            }

            var usable = _file.Profiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                .ToList();
            if (usable.Count != _file.Profiles.Count) dirty = true;
            _file.Profiles = usable;

            if (_file.Profiles.Count == 0)
            {
                _file.Profiles.Add(new ProfileInfo(DefaultId, Loc.T("Profile.DefaultName")));
                MigrateLegacyData();
                dirty = true;
            }
            if (_file.Profiles.All(p => p.Id != _file.CurrentId))
            {
                _file.CurrentId = _file.Profiles[0].Id;
                dirty = true;
            }

            Directory.CreateDirectory(CurrentDirectory);
            if (dirty) SaveBestEffort();
        }
    }

    /// <summary>
    /// Detects an all-profiles restore (PR3) that did not finish. Such a restore writes <c>profiles/restore.in-progress</c>
    /// before it moves the current data aside, so a marker left behind means the data may be half swapped and the previous
    /// state is still under a <c>profiles.bak-*</c> folder next to <c>profiles/</c>.
    /// <para>
    /// TODO(PR3 follow-up): implement the recovery — when the marker is found, delete the partial <c>profiles/</c> (and a
    /// partially placed <c>profiles.json</c>), rename <c>profiles.bak-*</c> back over <c>profiles/</c> (and
    /// <c>profiles.json.bak-*</c> over <c>profiles.json</c>), then remove the marker — instead of starting on partial
    /// data. For now this only records that it happened.
    /// </para>
    /// </summary>
    private void DetectInterruptedRestore()
    {
        try
        {
            string marker = Path.Combine(_root, "profiles", ProfileRestoreService.RestoreInProgressMarkerFileName);
            string? found = File.Exists(marker) ? marker : null;

            if (found is null)
            {
                // A crash between the move-aside and the re-create leaves no live profiles folder; the marker then sits
                // inside the backup folder instead.
                foreach (string dir in Directory.EnumerateDirectories(_root, "profiles.bak-*"))
                {
                    string candidate = Path.Combine(dir, ProfileRestoreService.RestoreInProgressMarkerFileName);
                    if (File.Exists(candidate)) { found = candidate; break; }
                }
            }

            if (found is not null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Restore] An interrupted all-profiles restore was detected ({found}). " +
                    "The previous data is still under profiles.bak-*; automatic recovery is not implemented yet.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Detection only: failing to look at the marker must never block startup.
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
                        Loc.T("Profile.MigrationFailed"), ex);
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
        // The profile list is not a convenience setting: losing a change silently would leave the screen and the disk apart.
        if (!EmbeddingSearchViewer.Infrastructure.Library.AtomicJsonFile.Save(_listPath, _file))
            throw new IOException($"Could not save the profile list: {_listPath}");
    }

    /// <summary>
    /// Writes the list without letting a failure stop the start: the list on disk then stays the one of the last
    /// change, which is what the app already had when it started, so the app can go on with it.
    /// </summary>
    private void SaveBestEffort()
    {
        try
        {
            Save();
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Profiles] The profile list could not be written ({_listPath}): {ex.Message}");
        }
    }

    private static string NormalizeName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0) throw new ArgumentException(Loc.T("Profile.NameRequired"), nameof(name));
        return name;
    }

    private void EnsureNameUnused(string name, string? exceptId = null)
    {
        if (_file.Profiles.Any(p => p.Id != exceptId && string.Equals(p.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException(Loc.T("Profile.NameInUse", name));
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
            if (index < 0) throw new InvalidOperationException(Loc.T("Profile.NotFound"));
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
            if (_file.Profiles.All(p => p.Id != id)) throw new InvalidOperationException(Loc.T("Profile.NotFound"));
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
            if (_file.CurrentId == id) throw new InvalidOperationException(Loc.T("Profile.CannotDeleteCurrent"));
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
