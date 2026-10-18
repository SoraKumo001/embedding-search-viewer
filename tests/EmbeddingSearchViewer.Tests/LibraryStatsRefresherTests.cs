using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Library;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class LibraryStatsRefresherTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    private sealed class VersionedRepository : IMediaRepository
    {
        private long _version;
        public long DataVersion => Interlocked.Read(ref _version);
        public void SetVersion(long value) => Interlocked.Exchange(ref _version, value);
        public void Changed() => Interlocked.Increment(ref _version);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult<MediaAsset?>(null);
        public Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.FromResult<MediaAsset?>(null);
        public Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MediaAsset>>(Array.Empty<MediaAsset>());
        public Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MediaChunk>>(Array.Empty<MediaChunk>());
        public Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<(Guid, float)>>(Array.Empty<(Guid, float)>());
    }

    /// <summary>Waits (up to 5 s) until <paramref name="condition"/> holds; returns whether it did.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        for (int i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        return condition();
    }

    [Fact]
    public async Task DoesNothing_WhileTheDataStaysTheSame()
    {
        var repo = new VersionedRepository();
        int calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, Tick);

        refresher.Start();
        await Task.Delay(Tick * 8);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RefreshesOnce_PerChange_NotPerTick()
    {
        var repo = new VersionedRepository();
        int calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, Tick);
        refresher.Start();

        repo.Changed();
        Assert.True(await WaitForAsync(() => calls == 1));
        await Task.Delay(Tick * 6);
        Assert.Equal(1, calls); // several ticks passed without another change

        repo.Changed();
        Assert.True(await WaitForAsync(() => calls == 2));
    }

    [Fact]
    public async Task WhatWasOnScreenAtStart_CountsAsUpToDate()
    {
        var repo = new VersionedRepository();
        repo.SetVersion(41); // data already there before the run began
        int calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, Tick);

        refresher.Start();
        await Task.Delay(Tick * 6);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ARepositoryThatCannotTrackChanges_IsRefreshedOnEveryTick()
    {
        var repo = new VersionedRepository();
        repo.SetVersion(-1);
        int calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, Tick);

        refresher.Start();

        Assert.True(await WaitForAsync(() => calls >= 3));
    }

    [Fact]
    public async Task RefreshesNeverOverlap_EvenWhenTheyAreSlowAndTheDataKeepsChanging()
    {
        var repo = new VersionedRepository();
        int running = 0, peak = 0, calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, async () =>
        {
            int now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen) { }
            await Task.Delay(80); // longer than several ticks
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref calls);
        }, Tick);
        refresher.Start();

        for (int i = 0; i < 20; i++)
        {
            repo.Changed();
            await Task.Delay(15);
        }
        Assert.True(await WaitForAsync(() => calls >= 2));

        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task Stop_EndsTheTicking_AndItCanBeStartedAgain()
    {
        var repo = new VersionedRepository();
        int calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, Tick);
        refresher.Start();
        Assert.True(refresher.IsRunning);

        refresher.Stop();
        Assert.False(refresher.IsRunning);
        repo.Changed();
        await Task.Delay(Tick * 6);
        Assert.Equal(0, calls);

        refresher.Start();
        repo.Changed();
        Assert.True(await WaitForAsync(() => calls == 1));
    }

    [Fact]
    public async Task StartingTwice_DoesNotRunTwoLoops()
    {
        var repo = new VersionedRepository();
        int calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, Tick);
        refresher.Start();
        refresher.Start();

        repo.Changed();
        Assert.True(await WaitForAsync(() => calls >= 1));
        await Task.Delay(Tick * 6);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AFailedRefresh_DoesNotStopTheLoop()
    {
        var repo = new VersionedRepository();
        int calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("database busy");
            return Task.CompletedTask;
        }, Tick);
        refresher.Start();

        repo.Changed();
        Assert.True(await WaitForAsync(() => calls == 1));
        repo.Changed();

        Assert.True(await WaitForAsync(() => calls == 2));
        Assert.True(refresher.IsRunning);
    }

    [Fact]
    public async Task Thinning_RefreshesAtMostOncePerMinimumInterval()
    {
        var repo = new VersionedRepository();
        int calls = 0;
        // A tick every 20 ms, but no refresh faster than once per 200 ms: ten changes over ~200 ms must not become ten refreshes.
        using var refresher = new LibraryStatsRefresher(repo, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, Tick, TimeSpan.FromMilliseconds(200));
        refresher.Start();

        for (int i = 0; i < 10; i++)
        {
            repo.Changed();
            await Task.Delay(20);
        }

        Assert.True(calls <= 2, $"the refreshes were not thinned: {calls} calls");
    }

    [Fact]
    public async Task Thinning_DoesNotDropAChangeThatArrivedBetweenRefreshes()
    {
        var repo = new VersionedRepository();
        int calls = 0;
        using var refresher = new LibraryStatsRefresher(repo, () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, Tick, TimeSpan.FromMilliseconds(120));
        refresher.Start();

        // The change is deferred by the thinning, but it still comes through once the interval has passed.
        repo.Changed();
        Assert.True(await WaitForAsync(() => calls == 1));

        repo.Changed();
        Assert.True(await WaitForAsync(() => calls == 2));
    }
}
