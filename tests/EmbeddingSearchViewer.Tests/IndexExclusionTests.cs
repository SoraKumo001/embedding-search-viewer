using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>Files that fail on every retry are left out of the next indexing runs, until they change or are released.</summary>
public sealed class IndexExclusionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_excl_" + Guid.NewGuid().ToString("N"));

    public IndexExclusionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string StorePath(string name = "excluded.json") => Path.Combine(_dir, name);

    private static ExcludedFile Entry(string path, long length = 10, long ticks = 100, string error = "corrupt", DateTime? at = null)
        => new(path, length, ticks, error, at ?? new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc));

    // ---- the store ----

    [Fact]
    public void Store_ExcludesOnlyTheSameFile_SameSizeAndModificationTime()
    {
        var store = new IndexExclusionStore(StorePath());
        store.Add(Entry(@"C:\img\bad.jpg", length: 10, ticks: 100));

        Assert.True(store.IsExcluded(@"C:\img\bad.jpg", 10, 100));
        Assert.True(store.IsExcluded(@"C:\IMG\BAD.JPG", 10, 100));    // Windows paths do not care about case
        Assert.False(store.IsExcluded(@"C:\img\bad.jpg", 11, 100));   // replaced: a different size
        Assert.False(store.IsExcluded(@"C:\img\bad.jpg", 10, 101));   // repaired: another modification time
        Assert.False(store.IsExcluded(@"C:\img\other.jpg", 10, 100));
    }

    [Fact]
    public void Store_KeepsItsListAcrossRestarts()
    {
        new IndexExclusionStore(StorePath()).Add(Entry(@"C:\img\a.jpg", error: "bad header"));

        var again = new IndexExclusionStore(StorePath());

        var item = Assert.Single(again.List());
        Assert.Equal(@"C:\img\a.jpg", item.Path);
        Assert.Equal("bad header", item.Error);
        Assert.Equal("a.jpg", item.FileName);
        Assert.Equal(1, again.Count);
    }

    [Fact]
    public void Store_RemoveAndClear_ReleaseFiles()
    {
        var store = new IndexExclusionStore(StorePath());
        store.Add(Entry(@"C:\img\a.jpg"));
        store.Add(Entry(@"C:\img\b.jpg"));
        store.Add(Entry(@"C:\img\c.jpg"));

        Assert.True(store.Remove(@"C:\img\a.jpg"));
        Assert.False(store.Remove(@"C:\img\a.jpg"));
        Assert.False(store.IsExcluded(@"C:\img\a.jpg", 10, 100));

        Assert.Equal(2, store.Clear());
        Assert.Equal(0, store.Clear());
        Assert.Empty(new IndexExclusionStore(StorePath()).List());
    }

    [Fact]
    public void Store_AddingTheSameFileAgain_ReplacesTheEntry()
    {
        var store = new IndexExclusionStore(StorePath());
        store.Add(Entry(@"C:\img\a.jpg", length: 10, error: "first"));
        store.Add(Entry(@"C:\img\a.jpg", length: 20, error: "second"));

        var item = Assert.Single(store.List());
        Assert.Equal(20, item.Length);
        Assert.Equal("second", item.Error);
    }

    [Fact]
    public void Store_ListsTheMostRecentFailureFirst()
    {
        var store = new IndexExclusionStore(StorePath());
        store.Add(Entry(@"C:\img\old.jpg", at: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        store.Add(Entry(@"C:\img\new.jpg", at: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(new[] { "new.jpg", "old.jpg" }, store.List().Select(i => i.FileName));
    }

    [Fact]
    public void Store_ABrokenFile_MeansAnEmptyList()
    {
        File.WriteAllText(StorePath(), "{ not json");

        var store = new IndexExclusionStore(StorePath());

        Assert.Empty(store.List());
        store.Add(Entry(@"C:\img\a.jpg"));   // and it can be written again
        Assert.Single(new IndexExclusionStore(StorePath()).List());
    }

    [Fact]
    public void Store_FollowsTheProfile_WhenThePathChanges()
    {
        string current = StorePath("work.json");
        var store = new IndexExclusionStore(() => current);
        store.Add(Entry(@"C:\work\a.jpg"));

        current = StorePath("hobby.json");
        Assert.Empty(store.List());
        store.Add(Entry(@"C:\hobby\b.jpg"));

        current = StorePath("work.json");
        Assert.Equal(@"C:\work\a.jpg", Assert.Single(store.List()).Path);
    }

    // ---- through the worker ----

    /// <summary>Fails (a corrupt file, nothing to retry) for any batch that has a file named "bad*"; remembers what it was asked.</summary>
    private sealed class PickyEmbedding : IEmbeddingService
    {
        public List<string> Asked { get; } = new();
        private readonly object _lock = new();

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
        {
            var paths = items.Select(i => i.Path ?? string.Empty).ToList();
            lock (_lock) Asked.AddRange(paths.Select(Path.GetFileName)!);
            if (paths.Any(p => Path.GetFileName(p).StartsWith("bad"))) throw new InvalidDataException("Unsupported or corrupt image");
            return Task.FromResult<IReadOnlyList<float[]>>(paths.Select(_ => Enumerable.Repeat(0.5f, truncateDim).ToArray()).ToList());
        }
    }

    private sealed class Rig
    {
        public required SqliteMediaRepository Repo { get; init; }
        public required IndexExclusionStore Store { get; init; }
        public required string Media { get; init; }
        public int Files => Directory.GetFiles(Media).Length;

        /// <summary>One indexing run with a fresh worker, like the next start of the app.</summary>
        public async Task<(IngestionProgress Last, PickyEmbedding Embedding, List<string> Errors)> RunAsync(string cache)
        {
            var embedding = new PickyEmbedding();
            var worker = new IngestionPipelineWorker(Repo, embedding, cache, exclusions: Store);
            var errors = new List<string>();
            IngestionProgress? last = null;
            worker.ProgressChanged += p =>
            {
                last = p;
                if (p.ErrorMessage != null) lock (errors) errors.Add(p.ErrorMessage);
            };

            await worker.QueueDirectoryAsync(Media);
            for (int i = 0; i < 100 && (last == null || last.IsRunning || last.ProcessedFiles < Files); i++) await Task.Delay(100);
            return (last!, embedding, errors);
        }
    }

    private async Task<Rig> NewRigAsync(params string[] files)
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        foreach (var f in files) File.WriteAllText(Path.Combine(media, f), "content of " + f);

        var repo = new SqliteMediaRepository(Path.Combine(_dir, "t.db"));
        await repo.InitializeAsync();
        return new Rig { Repo = repo, Store = new IndexExclusionStore(StorePath()), Media = media };
    }

    [Fact]
    public async Task AFileThatFailsEveryRetry_IsExcludedAndNotTriedAgainNextTime()
    {
        var rig = await NewRigAsync("a.jpg", "b.jpg", "bad.jpg");
        string cache = Path.Combine(_dir, "cache");

        var (first, firstEmbedding, firstErrors) = await rig.RunAsync(cache);

        Assert.Contains("bad.jpg", firstEmbedding.Asked);
        var excluded = Assert.Single(rig.Store.List());
        Assert.Equal("bad.jpg", excluded.FileName);
        Assert.Contains("corrupt", excluded.Error);
        Assert.Contains(firstErrors, e => e.Contains("bad.jpg") && e.Contains("次回から"));
        Assert.Equal(0, first.ExcludedFiles); // excluded just now, not skipped because of an earlier run

        var (second, secondEmbedding, _) = await rig.RunAsync(cache);

        Assert.DoesNotContain("bad.jpg", secondEmbedding.Asked);
        Assert.Equal(1, second.ExcludedFiles);
        Assert.Equal(3, second.ProcessedFiles);
        Assert.Equal(new[] { "a.jpg", "b.jpg" }, (await rig.Repo.GetAllAssetsAsync()).Select(a => a.FileName).OrderBy(x => x));
    }

    [Fact]
    public async Task AFileThatWasReplaced_IsTriedAgain()
    {
        var rig = await NewRigAsync("a.jpg", "bad.jpg");
        string cache = Path.Combine(_dir, "cache");
        await rig.RunAsync(cache);
        Assert.Single(rig.Store.List());

        File.WriteAllText(Path.Combine(rig.Media, "bad.jpg"), "a different, longer content than before");
        var (_, embedding, _) = await rig.RunAsync(cache);

        Assert.Contains("bad.jpg", embedding.Asked); // a new size: no longer "the same file"
        Assert.Equal(new FileInfo(Path.Combine(rig.Media, "bad.jpg")).Length, Assert.Single(rig.Store.List()).Length); // failed again: excluded again
    }

    [Fact]
    public async Task ReleasingTheExclusion_MakesTheNextRunTryTheFileAgain()
    {
        var rig = await NewRigAsync("a.jpg", "bad.jpg");
        string cache = Path.Combine(_dir, "cache");
        await rig.RunAsync(cache);

        rig.Store.Clear();
        var (last, embedding, _) = await rig.RunAsync(cache);

        Assert.Contains("bad.jpg", embedding.Asked);
        Assert.Equal(0, last.ExcludedFiles);
    }

    [Fact]
    public async Task Worker_WithoutAStore_KeepsTryingEveryRun()
    {
        var rig = await NewRigAsync("a.jpg", "bad.jpg");
        string cache = Path.Combine(_dir, "cache");

        var embedding = new PickyEmbedding();
        for (int run = 0; run < 2; run++)
        {
            var worker = new IngestionPipelineWorker(rig.Repo, embedding, cache);
            IngestionProgress? last = null;
            worker.ProgressChanged += p => last = p;
            await worker.QueueDirectoryAsync(rig.Media);
            for (int i = 0; i < 100 && (last == null || last.IsRunning || last.ProcessedFiles < 2); i++) await Task.Delay(100);
        }

        Assert.True(embedding.Asked.Count(n => n == "bad.jpg") >= 2);
    }

    [Fact]
    public void FailureMessage_SaysThatTheFilesAreLeftOutFromNowOn_OnlyWhenTheyAre()
    {
        var failures = new[] { new ImageFailure(@"C:\x\f1.jpg", "corrupt") };

        Assert.Contains("次回から", IngestionPipelineWorker.DescribeFailures(failures, willExclude: true));
        Assert.DoesNotContain("次回から", IngestionPipelineWorker.DescribeFailures(failures));
    }
}
