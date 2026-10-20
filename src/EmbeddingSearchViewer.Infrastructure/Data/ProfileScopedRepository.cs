using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Profiles;
using Microsoft.Data.Sqlite;

namespace EmbeddingSearchViewer.Infrastructure.Data;

/// <summary>
/// An <see cref="IMediaRepository"/> that always talks to the database of the current profile.
/// Everything that holds the repository (search engine, worker, view models) keeps working across a profile switch.
/// <para>
/// The database is opened in the background: the constructor returns at once and every call waits for the repository
/// to be there. There is no blocking wait anywhere, so the UI thread can never be held up (or deadlocked) by the
/// start-up.
/// </para>
/// </summary>
public sealed class ProfileScopedRepository : IMediaRepository, IDisposable
{
    // Room for each profile's own DataVersion before the epoch of the next switch starts.
    private const int EpochShift = 40;

    private readonly ProfileManager _profiles;
    private readonly object _lock = new();
    private SqliteMediaRepository? _inner;
    private string _innerPath = string.Empty;
    private long _epoch;

    /// <summary>
    /// The data version to report while no repository is installed: the version of the previous repository, captured
    /// when the switch started (0 while there is none). Once the new repository is installed its own version is read.
    /// </summary>
    private long _innerDataVersion;

    /// <summary>The open of the repository that is current (or of the one that is being opened).</summary>
    private Task<SqliteMediaRepository> _ready;

    /// <summary>Counts the profile switches, so an open that belongs to an older switch is dropped.</summary>
    private long _generation;

    private bool _disposed;

    public ProfileScopedRepository(ProfileManager profiles)
    {
        _profiles = profiles;
        // The path is read here and not in the background task: the profile can change while the task waits for its
        // turn, and the task has to open the profile it belongs to.
        string path = profiles.DatabasePath;
        // The open runs in the background: the constructor returns immediately, and every call waits for the result.
        _ready = Task.Run(() => OpenAsync(path, 0));
        _profiles.Changed += OnProfilesChanged;
    }

    /// <summary>
    /// Changes on every profile switch, so cached search snapshots of another profile are never reused. The epoch
    /// advances when the switch starts, so the value read while the new repository opens cannot hit a snapshot of the
    /// profile that is leaving; it advances again when the new repository is installed.
    /// </summary>
    public long DataVersion
    {
        get
        {
            // The epoch and the repository are read together: a half-read pair (the new epoch with the old value or the
            // other way round) could name a snapshot of the wrong profile.
            lock (_lock)
            {
                long epoch = Interlocked.Read(ref _epoch);
                // While the new repository opens the version of the previous one is reported; at the very start there
                // is none and the value stays 0. Once the repository is there its own version is read.
                long inner = _inner?.DataVersion ?? Interlocked.Read(ref _innerDataVersion);
                return (epoch << EpochShift) + inner;
            }
        }
    }

    /// <summary>
    /// Opens the database of the given profile and makes it the current one. The slow part of the start-up (the
    /// derived indexes) runs in the background, so the repository is usable right away.
    /// </summary>
    private async Task<SqliteMediaRepository> OpenAsync(string path, long generation)
    {
        var repo = new SqliteMediaRepository(path);
        try
        {
            // Only the fast part of the start-up (the schema) is waited for; the rebuild of the derived indexes runs
            // in the background of the repository.
            await repo.InitializeFastAsync().ConfigureAwait(false);
        }
        catch
        {
            repo.Dispose();
            throw;
        }

        bool stale;
        bool disposed;
        lock (_lock)
        {
            disposed = _disposed;
            stale = disposed || generation != _generation;
            if (!stale)
            {
                _inner = repo;
                _innerPath = path;
                Interlocked.Exchange(ref _innerDataVersion, repo.DataVersion);
                // The epoch advances once more when the repository is installed: the values that were handed out while
                // the switch window was open (the previous profile's version under the switch epoch) are never reused
                // for this repository.
                Interlocked.Increment(ref _epoch);
            }
        }

        if (stale)
        {
            // Another switch came while this one was running: this repository is dropped and the caller waits for
            // the newer one instead.
            repo.Dispose();
            ReleasePooledConnections();
            if (disposed)
            {
                // Dispose does not replace _ready with a newer open, so ReadyAsync() would return this very task and
                // awaiting it from inside itself would hang forever. Report that the repository is gone instead.
                throw new ObjectDisposedException(nameof(ProfileScopedRepository));
            }
            return await ReadyAsync().ConfigureAwait(false);
        }

        return repo;
    }

    /// <summary>The repository of the current profile, once it is open.</summary>
    private Task<SqliteMediaRepository> ReadyAsync()
    {
        Task<SqliteMediaRepository> ready;
        lock (_lock) ready = _ready;
        return ready;
    }

    /// <summary>Waits for the repository of the current profile and runs something with it.</summary>
    private async Task<T> RunAsync<T>(Func<SqliteMediaRepository, Task<T>> action)
    {
        var repo = await ReadyAsync().ConfigureAwait(false);
        return await action(repo).ConfigureAwait(false);
    }

    /// <summary>Waits for the repository of the current profile and runs something with it.</summary>
    private async Task RunAsync(Func<SqliteMediaRepository, Task> action)
    {
        var repo = await ReadyAsync().ConfigureAwait(false);
        await action(repo).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the pooled connections of the profiles that are not the current one any more, so the folder of a
    /// profile can be deleted. The writer connection of a repository is closed by its own dispose.
    /// </summary>
    private static void ReleasePooledConnections() => SqliteConnection.ClearAllPools();

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        SqliteMediaRepository? previous;
        lock (_lock)
        {
            if (_disposed) return;
            if (string.Equals(_innerPath, _profiles.DatabasePath, StringComparison.OrdinalIgnoreCase)) return; // a rename keeps the same folder

            previous = _inner;
            if (previous is not null)
            {
                // Remember the version of the profile that is leaving before its repository goes: while the new one
                // opens, DataVersion reports it under the new epoch (see below).
                Interlocked.Exchange(ref _innerDataVersion, previous.DataVersion);
            }
            _inner = null;

            // The epoch changes when the switch starts, not only once the new repository is there: a caller that runs
            // during the switch already reaches the new repository, so a value of the old epoch must not be reported.
            Interlocked.Increment(ref _epoch);

            long generation = ++_generation;

            // The path is read here and not in the background task: the profile can change again while the task waits
            // for its turn, and the task has to open the profile it belongs to.
            string path = _profiles.DatabasePath;

            // The switch is opened in the background as well: the callers that come in the meantime wait for it.
            _ready = Task.Run(() => OpenAsync(path, generation));
        }

        // The previous profile's writer connection (it is not pooled) is closed by its dispose, which returns without
        // waiting, and its pooled connections are released here, so the profile folder can be deleted even while the
        // new database is opening. Nothing slow happens on this thread (the profile switch may run on the UI thread).
        previous?.Dispose();
        ReleasePooledConnections();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.InitializeAsync(cancellationToken));

    public Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetAssetByPathAsync(filePath, cancellationToken));

    public Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetAssetByIdAsync(assetId, cancellationToken));

    public Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetAllAssetsAsync(cancellationToken));

    public Task<IReadOnlyDictionary<string, string>> GetAssetHashesAsync(CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetAssetHashesAsync(cancellationToken));

    public Task<AssetStats> GetAssetStatsAsync(CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetAssetStatsAsync(cancellationToken));

    public Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.SaveAssetWithChunksAsync(asset, chunks, cancellationToken));

    public Task<IReadOnlyList<AssetSaveFailure>> SaveAssetsWithChunksAsync(IReadOnlyList<AssetChunkBatch> items, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.SaveAssetsWithChunksAsync(items, cancellationToken));

    public Task BeginBulkIngestAsync(CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.BeginBulkIngestAsync(cancellationToken));

    public Task EndBulkIngestAsync(CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.EndBulkIngestAsync(cancellationToken));

    public Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.DeleteAssetAsync(assetId, cancellationToken));

    public Task DeleteAssetsAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.DeleteAssetsAsync(assetIds, cancellationToken));

    public Task ClearAllAsync(CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.ClearAllAsync(cancellationToken));

    public Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetAllChunksWithEmbeddingsAsync(mediaTypeFilter, cancellationToken));

    public Task<IReadOnlyList<MediaChunk>> GetAllChunksMetadataAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetAllChunksMetadataAsync(mediaTypeFilter, cancellationToken));

    public Task<IReadOnlyDictionary<Guid, float[]>> GetEmbeddingsByChunkIdsAsync(IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetEmbeddingsByChunkIdsAsync(chunkIds, cancellationToken));

    public Task<IReadOnlyList<MediaChunk>> GetChunksByAssetIdAsync(Guid assetId, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.GetChunksByAssetIdAsync(assetId, cancellationToken));

    public Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.SearchFtsAsync(keyword, limit, cancellationToken));

    public Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecAsync(float[] query, int k, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.SearchVecAsync(query, k, cancellationToken));

    public Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecRescoredAsync(float[] query, int k, int oversample = SqliteMediaRepository.DefaultVecOversample, CancellationToken cancellationToken = default)
        => RunAsync(repo => repo.SearchVecRescoredAsync(query, k, oversample, cancellationToken));

    public void Dispose()
    {
        SqliteMediaRepository? inner;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            inner = _inner;
            _inner = null;
            // A switch that is still running must not install its repository any more.
            _generation++;
        }

        _profiles.Changed -= OnProfilesChanged;
        inner?.Dispose();
    }
}
