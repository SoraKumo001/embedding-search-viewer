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
    Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default);
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
}
