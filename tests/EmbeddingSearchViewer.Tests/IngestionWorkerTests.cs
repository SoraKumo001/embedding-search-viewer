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

public sealed class IngestionWorkerTests : IDisposable
{
    private class StubEmbeddingService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items.Select(_ => new float[truncateDim]).ToList());
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "esv_test_" + Guid.NewGuid().ToString("N"));
    private readonly string _media;
    private readonly SqliteMediaRepository _repo;
    private readonly IngestionPipelineWorker _worker;
    private int _lastRunningFlag = 1;

    public IngestionWorkerTests()
    {
        _media = Path.Combine(_root, "media");
        Directory.CreateDirectory(_media);
        _repo = new SqliteMediaRepository(Path.Combine(_root, "test.db"));
        _repo.InitializeAsync().GetAwaiter().GetResult();
        _worker = new IngestionPipelineWorker(_repo, new StubEmbeddingService(), Path.Combine(_root, "cache"));
        _worker.ProgressChanged += p => Volatile.Write(ref _lastRunningFlag, p.IsRunning ? 1 : 0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return await condition();
    }

    private void CreateImage(string name) => File.WriteAllBytes(Path.Combine(_media, name), new byte[] { 1, 2, 3, (byte)name.Length });

    [Fact]
    public async Task Worker_FinishesAfterQueueDrained_AndCanRunAgain()
    {
        CreateImage("a.png");
        CreateImage("b.jpg");
        CreateImage("c.webp");

        await _worker.QueueDirectoryAsync(_media);
        Assert.True(await WaitUntilAsync(async () => (await _repo.GetAllAssetsAsync()).Count == 3 && Volatile.Read(ref _lastRunningFlag) == 0, 10000),
            "worker should index everything and report IsRunning=false");

        // A later run must start a fresh pass instead of staying stuck in the finished one
        CreateImage("dd.png");
        await _worker.QueueDirectoryAsync(_media);
        Assert.True(await WaitUntilAsync(async () => (await _repo.GetAllAssetsAsync()).Count == 4 && Volatile.Read(ref _lastRunningFlag) == 0, 10000));
    }


    [Fact]
    public async Task Repository_ReportsTheHashOfEveryIndexedFile()
    {
        CreateImage("a.png");
        CreateImage("b.jpg");
        await _worker.QueueDirectoryAsync(_media);
        Assert.True(await WaitUntilAsync(async () => (await _repo.GetAllAssetsAsync()).Count == 2 && Volatile.Read(ref _lastRunningFlag) == 0, 10000));

        var hashes = await _repo.GetAssetHashesAsync();
        var assets = await _repo.GetAllAssetsAsync();

        Assert.Equal(assets.ToDictionary(a => a.FilePath, a => a.FileHash), hashes.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    [Fact]
    public async Task Worker_SkipsUnchangedFilesOfAnEarlierRun_ButIndexesChangedAndNewOnes()
    {
        CreateImage("a.png");
        CreateImage("b.jpg");
        await _worker.QueueDirectoryAsync(_media);
        Assert.True(await WaitUntilAsync(async () => (await _repo.GetAllAssetsAsync()).Count == 2 && Volatile.Read(ref _lastRunningFlag) == 0, 10000));
        var before = (await _repo.GetAllAssetsAsync()).ToDictionary(a => a.FileName, a => a.IndexedAtUtc);

        // Second run: a.png untouched, b.jpg changed (other size), c.webp new.
        File.WriteAllBytes(Path.Combine(_media, "b.jpg"), new byte[] { 9, 9, 9, 9, 9, 9 });
        CreateImage("c.webp");
        Volatile.Write(ref _lastRunningFlag, 1);
        await _worker.QueueDirectoryAsync(_media);
        Assert.True(await WaitUntilAsync(async () => (await _repo.GetAllAssetsAsync()).Count == 3 && Volatile.Read(ref _lastRunningFlag) == 0, 10000));

        var after = (await _repo.GetAllAssetsAsync()).ToDictionary(a => a.FileName, a => a.IndexedAtUtc);
        Assert.Equal(before["a.png"], after["a.png"]);   // not indexed again
        Assert.NotEqual(before["b.jpg"], after["b.jpg"]); // changed: indexed again
    }

    [Fact]
    public async Task Worker_WithNoSupportedFiles_ReportsFinished()
    {
        File.WriteAllText(Path.Combine(_media, "notes.txt"), "x");

        await _worker.QueueDirectoryAsync(_media);

        Assert.True(await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref _lastRunningFlag) == 0), 5000));
    }

    [Fact]
    public async Task FolderWatcher_IndexesNewFilesAndRemovesDeletedOnes()
    {
        var store = new LibrarySettingsStore(Path.Combine(_root, "library.json"));
        store.Save(new LibrarySettings { Folders = { _media }, AutoIndex = true });
        using var watcher = new FolderWatcher(store, _worker, _repo);
        watcher.Refresh();

        CreateImage("new1.png");
        Assert.True(await WaitUntilAsync(async () => (await _repo.GetAllAssetsAsync()).Any(a => a.FileName == "new1.png"), 20000),
            "created file should be indexed automatically");

        File.Delete(Path.Combine(_media, "new1.png"));
        Assert.True(await WaitUntilAsync(async () => (await _repo.GetAllAssetsAsync()).All(a => a.FileName != "new1.png"), 20000),
            "deleted file should be removed from the index");
    }

    [Fact]
    public async Task FolderWatcher_DoesNothingWhenAutoIndexIsOff()
    {
        var store = new LibrarySettingsStore(Path.Combine(_root, "library.json"));
        store.Save(new LibrarySettings { Folders = { _media }, AutoIndex = false });
        using var watcher = new FolderWatcher(store, _worker, _repo);
        watcher.Refresh();

        CreateImage("ignored.png");
        await Task.Delay(5000);

        Assert.Empty(await _repo.GetAllAssetsAsync());
    }

    [Fact]
    public void SettingsStore_RoundTripsAndToleratesCorruptFile()
    {
        var path = Path.Combine(_root, "s.json");
        var store = new LibrarySettingsStore(path);
        store.Save(new LibrarySettings { Folders = { @"C:\a", @"C:\b" }, AutoIndex = true });

        var loaded = store.Load();
        Assert.Equal(new[] { @"C:\a", @"C:\b" }, loaded.Folders);
        Assert.True(loaded.AutoIndex);

        File.WriteAllText(path, "{ not json");
        Assert.Empty(store.Load().Folders);
    }
}
