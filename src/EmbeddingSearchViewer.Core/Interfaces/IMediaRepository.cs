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
    Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default);
    Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default);
}
