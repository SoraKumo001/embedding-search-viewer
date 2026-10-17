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
/// </summary>
public sealed class ProfileScopedRepository : IMediaRepository, IDisposable
{
    // Room for each profile's own DataVersion before the epoch of the next switch starts.
    private const int EpochShift = 40;

    private readonly ProfileManager _profiles;
    private readonly object _lock = new();
    private SqliteMediaRepository _inner;
    private string _innerPath;
    private long _epoch;

    public ProfileScopedRepository(ProfileManager profiles)
    {
        _profiles = profiles;
        (_inner, _innerPath) = Open();
        _profiles.Changed += OnProfilesChanged;
    }

    /// <summary>Changes on every profile switch, so cached search snapshots of another profile are never reused.</summary>
    public long DataVersion => (Interlocked.Read(ref _epoch) << EpochShift) + Current().DataVersion;

    private (SqliteMediaRepository, string) Open()
    {
        string path = _profiles.DatabasePath;
        var repo = new SqliteMediaRepository(path);
        repo.InitializeAsync().GetAwaiter().GetResult();
        return (repo, path);
    }

    private SqliteMediaRepository Current()
    {
        lock (_lock) return _inner;
    }

    private void OnProfilesChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (string.Equals(_innerPath, _profiles.DatabasePath, StringComparison.OrdinalIgnoreCase)) return; // a rename keeps the same folder

            (_inner, _innerPath) = Open();
            Interlocked.Increment(ref _epoch);
        }

        // Release the pooled connections of the previous database so its profile folder can be deleted.
        SqliteConnection.ClearAllPools();
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Current().InitializeAsync(cancellationToken);

    public Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default)
        => Current().GetAssetByPathAsync(filePath, cancellationToken);

    public Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default)
        => Current().GetAssetByIdAsync(assetId, cancellationToken);

    public Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default)
        => Current().GetAllAssetsAsync(cancellationToken);

    public Task<IReadOnlyDictionary<string, string>> GetAssetHashesAsync(CancellationToken cancellationToken = default)
        => Current().GetAssetHashesAsync(cancellationToken);

    public Task<AssetStats> GetAssetStatsAsync(CancellationToken cancellationToken = default)
        => Current().GetAssetStatsAsync(cancellationToken);

    public Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default)
        => Current().SaveAssetWithChunksAsync(asset, chunks, cancellationToken);

    public Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
        => Current().DeleteAssetAsync(assetId, cancellationToken);

    public Task DeleteAssetsAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default)
        => Current().DeleteAssetsAsync(assetIds, cancellationToken);

    public Task ClearAllAsync(CancellationToken cancellationToken = default)
        => Current().ClearAllAsync(cancellationToken);

    public Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default)
        => Current().GetAllChunksWithEmbeddingsAsync(mediaTypeFilter, cancellationToken);

    public Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default)
        => Current().SearchFtsAsync(keyword, limit, cancellationToken);

    public Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecAsync(float[] query, int k, CancellationToken cancellationToken = default)
        => Current().SearchVecAsync(query, k, cancellationToken);

    public Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecRescoredAsync(float[] query, int k, int oversample = SqliteMediaRepository.DefaultVecOversample, CancellationToken cancellationToken = default)
        => Current().SearchVecRescoredAsync(query, k, oversample, cancellationToken);

    public void Dispose() => _profiles.Changed -= OnProfilesChanged;
}
