using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Profiles;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "esv_prof_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task AddAssetAsync(ProfileScopedRepository repo, string name)
    {
        var asset = new MediaAsset { FileName = name, FilePath = @"C:\data\" + name, MediaType = MediaType.Image, LastModifiedUtc = DateTime.UtcNow };
        await repo.SaveAssetWithChunksAsync(asset, Array.Empty<MediaChunk>());
    }

    [Fact]
    public void NewInstall_StartsWithTheDefaultProfile()
    {
        var manager = new ProfileManager(_root);

        var only = Assert.Single(manager.Profiles);
        Assert.Equal(ProfileManager.DefaultId, only.Id);
        Assert.Equal(only, manager.Current);
        Assert.StartsWith(Path.Combine(_root, "profiles"), manager.DatabasePath);
    }

    [Fact]
    public void LegacyFiles_AreMovedIntoTheDefaultProfileOnce()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cache", "pdf"));
        File.WriteAllText(Path.Combine(_root, "media_index.db"), "db");
        File.WriteAllText(Path.Combine(_root, "media_index.db-wal"), "wal");
        File.WriteAllText(Path.Combine(_root, "library.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "search_history.json"), "[]");
        File.WriteAllText(Path.Combine(_root, "cache", "pdf", "page.png"), "png");

        var manager = new ProfileManager(_root);

        Assert.Equal("db", File.ReadAllText(manager.DatabasePath));
        Assert.True(File.Exists(manager.DatabasePath + "-wal"));
        Assert.True(File.Exists(manager.LibraryPath));
        Assert.True(File.Exists(manager.HistoryPath));
        Assert.True(File.Exists(Path.Combine(manager.CacheDirectory, "pdf", "page.png")));
        Assert.False(File.Exists(Path.Combine(_root, "media_index.db")));
        Assert.False(Directory.Exists(Path.Combine(_root, "cache")));

        // A second start must not touch anything again.
        var again = new ProfileManager(_root);
        Assert.Equal("db", File.ReadAllText(again.DatabasePath));
        Assert.Single(again.Profiles);
    }

    [Fact]
    public void LockedLegacyDatabase_RefusesToStartAndMovesNothing()
    {
        Directory.CreateDirectory(_root);
        string db = Path.Combine(_root, "media_index.db");
        File.WriteAllText(db, "db");
        File.WriteAllText(db + "-wal", "wal");

        using (new FileStream(db, FileMode.Open, FileAccess.Read, FileShare.Read)) // an old instance keeps it open
        {
            Assert.Throws<IOException>(() => new ProfileManager(_root));
        }

        Assert.True(File.Exists(db));
        Assert.True(File.Exists(db + "-wal"));
        Assert.False(File.Exists(Path.Combine(_root, "profiles.json")));

        // Once the other instance is gone, the next start migrates normally.
        var manager = new ProfileManager(_root);
        Assert.Equal("db", File.ReadAllText(manager.DatabasePath));
        Assert.True(File.Exists(manager.DatabasePath + "-wal"));
    }

    [Fact]
    public void CreateRenameSwitchAndDelete_PersistAcrossRestarts()
    {
        var manager = new ProfileManager(_root);
        var work = manager.Create("仕事");
        var hobby = manager.Create("趣味");

        Assert.True(manager.Switch(work.Id));
        manager.Rename(work.Id, "仕事用");
        Assert.False(manager.Switch(work.Id)); // already current

        var restarted = new ProfileManager(_root);
        Assert.Equal(3, restarted.Profiles.Count);
        Assert.Equal("仕事用", restarted.Current.Name);

        Directory.CreateDirectory(restarted.CurrentDirectory);
        restarted.Switch(ProfileManager.DefaultId);
        string hobbyDir = Path.Combine(_root, "profiles", hobby.Id);
        Assert.True(Directory.Exists(hobbyDir));
        restarted.Delete(hobby.Id);
        Assert.False(Directory.Exists(hobbyDir));
        Assert.DoesNotContain(restarted.Profiles, p => p.Id == hobby.Id);
    }

    [Fact]
    public void Rules_RejectBadNamesAndDeletingTheCurrentProfile()
    {
        var manager = new ProfileManager(_root);
        manager.Create("仕事");

        Assert.Throws<ArgumentException>(() => manager.Create("   "));
        Assert.Throws<InvalidOperationException>(() => manager.Create("仕事"));
        Assert.Throws<InvalidOperationException>(() => manager.Create(" 仕事 "));
        Assert.Throws<InvalidOperationException>(() => manager.Delete(manager.Current.Id));
        Assert.Throws<InvalidOperationException>(() => manager.Switch("missing"));
    }

    [Fact]
    public void BrokenProfileList_FallsBackToTheDefaultProfile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "profiles.json"), "{ not json");

        var manager = new ProfileManager(_root);

        Assert.Equal(ProfileManager.DefaultId, manager.Current.Id);
    }

    [Fact]
    public void Switch_RaisesChangedAndMovesTheStores()
    {
        var manager = new ProfileManager(_root);
        var library = new LibrarySettingsStore(() => manager.LibraryPath);
        var history = new SearchHistoryStore(() => manager.HistoryPath);
        int changes = 0;
        manager.Changed += (_, _) => changes++;

        library.Save(new LibrarySettings { Folders = { @"C:\work" } });
        history.Add("work query");

        var hobby = manager.Create("趣味");
        manager.Switch(hobby.Id);

        Assert.Equal(1, changes);
        Assert.Empty(library.Load().Folders);
        Assert.Empty(history.Load());

        library.Save(new LibrarySettings { Folders = { @"C:\photos" } });
        manager.Switch(ProfileManager.DefaultId);

        Assert.Equal(new[] { @"C:\work" }, library.Load().Folders);
        Assert.Equal(new[] { "work query" }, history.Load().Select(h => h.Text));
    }

    [Fact]
    public async Task Repository_FollowsTheCurrentProfileAndChangesItsDataVersion()
    {
        var manager = new ProfileManager(_root);
        using var repo = new ProfileScopedRepository(manager);
        await AddAssetAsync(repo, "work.png");
        long workVersion = repo.DataVersion;

        var hobby = manager.Create("趣味");
        manager.Switch(hobby.Id);

        Assert.Empty(await repo.GetAllAssetsAsync());
        Assert.NotEqual(workVersion, repo.DataVersion);
        await AddAssetAsync(repo, "hobby.png");
        await AddAssetAsync(repo, "hobby2.png");

        manager.Switch(ProfileManager.DefaultId);

        var assets = await repo.GetAllAssetsAsync();
        Assert.Equal("work.png", Assert.Single(assets).FileName);
        // Back on the first profile the version must still differ from what a cache of the hobby profile saw.
        Assert.NotEqual(workVersion, repo.DataVersion);
    }

    [Fact]
    public async Task DeletingAnotherProfile_RemovesItsDatabaseFiles()
    {
        var manager = new ProfileManager(_root);
        using var repo = new ProfileScopedRepository(manager);

        var hobby = manager.Create("趣味");
        manager.Switch(hobby.Id);
        await AddAssetAsync(repo, "hobby.png");
        manager.Switch(ProfileManager.DefaultId);

        manager.Delete(hobby.Id);

        Assert.False(Directory.Exists(Path.Combine(_root, "profiles", hobby.Id)));
    }
}
