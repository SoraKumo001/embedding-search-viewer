using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Core.Interfaces;

public interface IMediaRepository
{
    /// <summary>
    /// Monotonic counter bumped whenever indexed data changes, so callers can cache safely.
    /// A negative value means the repository does not track changes and results must not be cached.
    /// </summary>
    long DataVersion => -1;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default);
    Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The file hash (size and modification time) of every indexed file, by path: enough to tell which files are unchanged,
    /// in one query instead of one per file. Repositories can override this with something cheaper than loading every asset.
    /// </summary>
    async Task<IReadOnlyDictionary<string, string>> GetAssetHashesAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var asset in await GetAllAssetsAsync(cancellationToken)) result[asset.FilePath] = asset.FileHash;
        return result;
    }

    /// <summary>How many files are indexed, in total and by type, without loading the files themselves.</summary>
    async Task<AssetStats> GetAssetStatsAsync(CancellationToken cancellationToken = default)
    {
        var assets = await GetAllAssetsAsync(cancellationToken);
        return new AssetStats(
            assets.Count,
            assets.Count(a => a.MediaType == MediaType.Image),
            assets.Count(a => a.MediaType == MediaType.Pdf),
            assets.Count(a => a.MediaType == MediaType.Video),
            assets.Count(a => a.MediaType == MediaType.Document));
    }

    Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores many assets (each with its chunks) with as few transactions as possible. A single failing asset must not
    /// take its neighbours with it: an implementation isolates the per-asset work (a SAVEPOINT, or a lone retry) and
    /// reports the failures instead of throwing, so "one bad asset = one rollback" is kept. The default implementation
    /// stores one asset at a time through <see cref="SaveAssetWithChunksAsync"/>, which already guarantees that.
    /// </summary>
    async Task<IReadOnlyList<AssetSaveFailure>> SaveAssetsWithChunksAsync(
        IReadOnlyList<AssetChunkBatch> items, CancellationToken cancellationToken = default)
    {
        var failures = new List<AssetSaveFailure>();
        if (items is null) return failures;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SaveAssetWithChunksAsync(item.Asset, item.Chunks, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new AssetSaveFailure(item.Asset.Id, item.Asset.FilePath, ex.Message));
            }
        }
        return failures;
    }

    /// <summary>
    /// Announces a bulk ingest, so the repository can suspend derived-index maintenance that is far cheaper to do once
    /// at the end than per row (the vec0 insert trigger). The full-text triggers stay in place. Default: nothing to do.
    /// </summary>
    Task BeginBulkIngestAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Ends a bulk ingest started with <see cref="BeginBulkIngestAsync"/>: restores what was suspended and back-fills
    /// the derived index in one pass (see <c>EnsureVecBackfillAsync</c>). Default: nothing to do.
    /// </summary>
    Task EndBulkIngestAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default);

    async Task DeleteAssetsAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in assetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeleteAssetAsync(id, cancellationToken);
        }
    }

    async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        var assets = await GetAllAssetsAsync(cancellationToken);
        await DeleteAssetsAsync(assets.Select(a => a.Id), cancellationToken);
    }

    Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Nearest neighbours by embedding distance, best first, through the repository's vector index when it has one.
    /// The default implementation has no index and returns nothing, so search falls back to a brute-force scan; this
    /// keeps existing implementations working unchanged.
    /// </summary>
    Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecAsync(float[] query, int k, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<(Guid ChunkId, float Distance)>>(Array.Empty<(Guid, float)>());

    /// <summary>
    /// Nearest neighbours by embedding distance, best first, through a binary-quantization coarse pass that is then
    /// rescored with exact cosine distance (Phase 2). <paramref name="oversample"/> is how many coarse candidates are
    /// rescored per requested neighbour. The default implementation has no such index and returns nothing, so search
    /// falls back to the flat <see cref="SearchVecAsync"/> KNN and, last, to a brute-force scan; this keeps existing
    /// implementations working unchanged.
    /// </summary>
    Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecRescoredAsync(float[] query, int k, int oversample = 8, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<(Guid ChunkId, float Distance)>>(Array.Empty<(Guid, float)>());
}
