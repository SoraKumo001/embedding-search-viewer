using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Profiles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>
/// Backup and restore round trips (PR1-PR3) on profile folders without a database. The database path (integrity check,
/// vec backfill, WAL checkpoint) and a real search match need the model and real media, so those are covered by the
/// manual checklist in docs/OPERATIONS.md instead.
/// </summary>
public sealed class ProfileBackupRestoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "esv_bk_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ProfileManager NewManager() => new(_root);

    private string ProfilesRoot => Path.Combine(_root, "profiles");

    /// <summary>Writes the two JSON stores a profile always has (a valid library.json and a search history).</summary>
    private static void WriteProfileFiles(ProfileManager manager, string id, string history, string modelTag = "test-model")
    {
        Directory.CreateDirectory(manager.DirectoryOf(id));
        File.WriteAllText(manager.HistoryPathOf(id), history);
        new LibrarySettingsStore(manager.LibraryPathOf(id)).Save(new LibrarySettings { IndexModelTag = modelTag });
    }

    [Fact]
    public async Task SingleBackup_Preview_NamesTheProfileAndWarnsOnTheModel()
    {
        var manager = NewManager();
        var profile = manager.Current;
        WriteProfileFiles(manager, profile.Id, "history-1");

        string zip = Path.Combine(_root, "single.zip");
        await new ProfileBackupService(manager).BackupSingleAsync(profile.Id, zip, includeCache: false);

        var preview = await new ProfileRestoreService(manager).PreviewAsync(zip);

        Assert.Equal(profile.Id, preview.ProfileId);
        Assert.Equal(profile.Name, preview.ProfileName);
        Assert.Equal("test-model", preview.ModelTag);
        Assert.Equal(2, preview.FileCount); // library.json + search_history.json
        Assert.True(preview.TotalBytes > 0);
        Assert.False(preview.CacheIncluded);
        Assert.NotEmpty(preview.Warnings); // the backup's model is not the one the app uses now
    }

    [Fact]
    public async Task SingleRestore_PutsTheJsonStoresBack()
    {
        var manager = NewManager();
        var profile = manager.Current;
        WriteProfileFiles(manager, profile.Id, "history-before");

        string zip = Path.Combine(_root, "single.zip");
        await new ProfileBackupService(manager).BackupSingleAsync(profile.Id, zip, includeCache: false);

        // Damage the live profile: replace the history and drop library.json.
        File.WriteAllText(manager.HistoryPathOf(profile.Id), "history-after");
        File.Delete(manager.LibraryPathOf(profile.Id));

        await new ProfileRestoreService(manager).RestoreSingleAsync(zip, getBusyState: null);

        Assert.Equal("history-before", File.ReadAllText(manager.HistoryPathOf(profile.Id)));
        Assert.True(File.Exists(manager.LibraryPathOf(profile.Id)));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(ProfilesRoot),
            d => Path.GetFileName(d).Contains(".bak-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SingleBackup_IncludesTheCacheOnlyWhenAsked()
    {
        var manager = NewManager();
        var profile = manager.Current;
        WriteProfileFiles(manager, profile.Id, "history-1");

        string cache = manager.CacheDirectoryOf(profile.Id);
        Directory.CreateDirectory(Path.Combine(cache, "pdf"));
        File.WriteAllText(Path.Combine(cache, "pdf", "page.png"), "png");

        string plain = Path.Combine(_root, "plain.zip");
        string withCache = Path.Combine(_root, "cache.zip");
        var backup = new ProfileBackupService(manager);
        await backup.BackupSingleAsync(profile.Id, plain, includeCache: false);
        await backup.BackupSingleAsync(profile.Id, withCache, includeCache: true);

        var restore = new ProfileRestoreService(manager);
        var plainPreview = await restore.PreviewAsync(plain);
        var cachePreview = await restore.PreviewAsync(withCache);

        Assert.False(plainPreview.CacheIncluded);
        Assert.Equal(2, plainPreview.FileCount); // no cache file
        Assert.True(cachePreview.CacheIncluded);
        Assert.Equal(3, cachePreview.FileCount); // + cache/pdf/page.png

        // Restore the cache-bearing archive after losing the cache, and the file comes back.
        Directory.Delete(cache, recursive: true);
        await restore.RestoreSingleAsync(withCache, getBusyState: null);
        Assert.Equal("png", File.ReadAllText(Path.Combine(cache, "pdf", "page.png")));
    }

    [Fact]
    public async Task AllBackup_Preview_ListsEveryProfile()
    {
        var manager = NewManager();
        var work = manager.Create("Work");
        WriteProfileFiles(manager, manager.Current.Id, "h-default");
        WriteProfileFiles(manager, work.Id, "h-work");

        string zip = Path.Combine(_root, "all.zip");
        await new ProfileBackupService(manager).BackupAllAsync(zip, includeCache: false);

        var preview = await new ProfileRestoreService(manager).PreviewAllAsync(zip);

        Assert.Equal(2, preview.ProfileCount);
        Assert.Equal(4, preview.FileCount); // two files for each of two profiles
        Assert.NotNull(preview.CreatedAtUtc);
        Assert.Contains(preview.Profiles, p => p.ProfileId == work.Id && p.ProfileName == "Work");
    }

    [Fact]
    public async Task AllRestore_ReplacesEveryProfileAndProfilesJson()
    {
        var manager = NewManager();
        var work = manager.Create("Work");
        WriteProfileFiles(manager, manager.Current.Id, "h-default");
        WriteProfileFiles(manager, work.Id, "h-work");

        string zip = Path.Combine(_root, "all.zip");
        await new ProfileBackupService(manager).BackupAllAsync(zip, includeCache: false);

        // Wreck the live state: wrong history, missing library, and a wrong profiles.json.
        File.WriteAllText(manager.HistoryPathOf(manager.Current.Id), "h-changed");
        File.Delete(manager.LibraryPathOf(work.Id));
        File.WriteAllText(Path.Combine(_root, "profiles.json"), "{\"Profiles\":[],\"CurrentId\":null}");

        await new ProfileRestoreService(manager).RestoreAllAsync(zip, getBusyState: null);

        Assert.Equal("h-default", File.ReadAllText(manager.HistoryPathOf(manager.Current.Id)));
        Assert.Equal("h-work", File.ReadAllText(manager.HistoryPathOf(work.Id)));
        Assert.Contains("\"Work\"", File.ReadAllText(Path.Combine(_root, "profiles.json")));
        // Committed: the marker, the .bak copies and the staging folder are gone.
        Assert.False(File.Exists(Path.Combine(ProfilesRoot, "restore.in-progress")));
        Assert.Empty(Directory.EnumerateDirectories(_root, "profiles.bak-*"));
        Assert.Empty(Directory.EnumerateDirectories(ProfilesRoot, ".staging-*"));
    }

    [Fact]
    public async Task AllRestore_RollsBackAndKeepsTheMarker_WhenAFileIsCorrupt()
    {
        var manager = NewManager();
        var work = manager.Create("Work");
        WriteProfileFiles(manager, manager.Current.Id, "h-default");
        WriteProfileFiles(manager, work.Id, "h-work");

        string zip = Path.Combine(_root, "all.zip");
        await new ProfileBackupService(manager).BackupAllAsync(zip, includeCache: false);

        // Change a file the manifest covers, so the hash check fails during the restore.
        ReplaceEntry(zip, $"profiles/{work.Id}/search_history.json", "h-work-x");

        var restore = new ProfileRestoreService(manager);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => restore.RestoreAllAsync(zip, getBusyState: null));

        // Rolled back to the pre-restore state, and the marker is left for the next start.
        Assert.Equal("h-default", File.ReadAllText(manager.HistoryPathOf(manager.Current.Id)));
        Assert.Equal("h-work", File.ReadAllText(manager.HistoryPathOf(work.Id)));
        Assert.True(File.Exists(Path.Combine(ProfilesRoot, "restore.in-progress")));
    }

    [Fact]
    public async Task Previews_RejectTheOtherForm()
    {
        var manager = NewManager();
        WriteProfileFiles(manager, manager.Current.Id, "h");

        string single = Path.Combine(_root, "single.zip");
        string all = Path.Combine(_root, "all.zip");
        var backup = new ProfileBackupService(manager);
        await backup.BackupSingleAsync(manager.Current.Id, single, includeCache: false);
        await backup.BackupAllAsync(all, includeCache: false);

        var restore = new ProfileRestoreService(manager);
        await Assert.ThrowsAsync<NotSupportedException>(() => restore.PreviewAllAsync(single));
        await Assert.ThrowsAsync<NotSupportedException>(() => restore.PreviewAsync(all));
    }

    /// <summary>Rewrites one entry of a zip in place, so a hash-checked file no longer matches its manifest.</summary>
    private static void ReplaceEntry(string zipPath, string entryName, string newContent)
    {
        var entries = new List<(string Name, byte[] Bytes)>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                entries.Add((entry.FullName, entry.FullName == entryName ? Encoding.UTF8.GetBytes(newContent) : buffer.ToArray()));
            }
        }

        using var file = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var rewritten = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var entryStream = rewritten.CreateEntry(name).Open();
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }
}
