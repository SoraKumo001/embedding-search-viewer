using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;

using static EmbeddingSearchViewer.Core.Services.SearchPostProcessing;
namespace EmbeddingSearchViewer.Core.Services;

public class HybridSearchEngine
{
    private readonly IMediaRepository _repository;
    private readonly IEmbeddingService _embeddingService;

    private sealed record Snapshot(
        long Version,
        IReadOnlyList<MediaChunk> Chunks,
        Dictionary<Guid, MediaChunk> ChunkMap,
        Dictionary<Guid, MediaAsset> Assets);

    /// <summary>
    /// The full-embedding copy used by the brute-force fallback: every chunk with its vector, plus the groups of
    /// identical embeddings. Loaded (and cached) only when the repository has no usable vector index.
    /// </summary>
    private sealed record FullChunks(
        long Version,
        IReadOnlyList<MediaChunk> Chunks,
        Dictionary<Guid, ulong> EmbeddingGroups);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Snapshot? _snapshot;
    private FullChunks? _fullChunks;

    // Per-asset chunks for asset-scoped search, reused while the repository version is unchanged. Only assets that
    // were actually searched are held (a handful at a time), far smaller than the whole corpus, and never for an
    // unversioned repository (whose DataVersion is negative).
    private readonly object _assetCacheLock = new();
    private long _assetCacheVersion = long.MinValue;
    private readonly Dictionary<Guid, IReadOnlyList<MediaChunk>> _assetChunks = new();

    /// <summary>
    /// Returns every chunk's metadata plus all assets, reusing the in-memory copy while the repository data is
    /// unchanged. The embeddings are deliberately not part of the snapshot: at scale they are the expensive,
    /// memory-heavy part, and the vector path only needs the exact vectors of its few hits (fetched on demand). Only
    /// the fallback brute-force scan loads the full embeddings, through <see cref="GetFullChunksAsync"/>.
    /// </summary>
    private async Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        long version = _repository.DataVersion;
        var cached = _snapshot;
        if (version >= 0 && cached != null && cached.Version == version) return cached;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            version = _repository.DataVersion;
            cached = _snapshot;
            if (version >= 0 && cached != null && cached.Version == version) return cached;

            var chunks = await _repository.GetAllChunksMetadataAsync(null, cancellationToken).ConfigureAwait(false);
            var assets = (await _repository.GetAllAssetsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.Id);
            var snapshot = new Snapshot(version, chunks, chunks.ToDictionary(c => c.Id), assets);
            if (version >= 0) _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Loads every chunk with its embedding plus the groups of identical embeddings, for the brute-force fallback that
    /// runs when the repository has no usable vector index. Cached independently of the metadata snapshot, so a
    /// fallback does not reload the database and the next search reuses it. Never reached on the vector path.
    /// </summary>
    private async Task<FullChunks> GetFullChunksAsync(CancellationToken cancellationToken)
    {
        long version = _repository.DataVersion;
        var cached = _fullChunks;
        if (version >= 0 && cached != null && cached.Version == version) return cached;

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            version = _repository.DataVersion;
            cached = _fullChunks;
            if (version >= 0 && cached != null && cached.Version == version) return cached;

            var chunks = await _repository.GetAllChunksWithEmbeddingsAsync(null, cancellationToken).ConfigureAwait(false);
            var full = new FullChunks(version, chunks, GroupIdenticalEmbeddings(chunks));
            if (version >= 0) _fullChunks = full;
            return full;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// The chunks (with embeddings) of one asset, reused while the repository data is unchanged. Only the searched
    /// asset's rows are loaded, through the repository's AssetId index, so this stays small (a video's few thousand
    /// scenes) even when the whole corpus is huge. The cache is dropped whenever
    /// <see cref="IMediaRepository.DataVersion"/> changes; unversioned repositories are never cached.
    /// </summary>
    private async Task<IReadOnlyList<MediaChunk>> GetAssetChunksAsync(Guid assetId, CancellationToken cancellationToken)
    {
        long version = _repository.DataVersion;
        if (version >= 0)
        {
            lock (_assetCacheLock)
            {
                if (_assetCacheVersion == version && _assetChunks.TryGetValue(assetId, out var cached)) return cached;
            }
        }

        var chunks = await _repository.GetChunksByAssetIdAsync(assetId, cancellationToken).ConfigureAwait(false);

        if (version >= 0)
        {
            lock (_assetCacheLock)
            {
                if (_assetCacheVersion != version)
                {
                    _assetChunks.Clear();
                    _assetCacheVersion = version;
                }
                _assetChunks[assetId] = chunks;
            }
        }
        return chunks;
    }

    public HybridSearchEngine(IMediaRepository repository, IEmbeddingService embeddingService)
    {
        _repository = repository;
        _embeddingService = embeddingService;
    }

    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        // 1. Generate query embedding
        float[]? queryEmbedding = null;
        if (!string.IsNullOrWhiteSpace(query.QueryImagePath))
        {
            queryEmbedding = await _embeddingService.GenerateQueryEmbeddingAsync(
                query.QueryImagePath,
                modality: EmbeddingModalities.Image,
                truncateDim: query.TruncateDim,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(query.QueryAudioPath))
        {
            queryEmbedding = await _embeddingService.GenerateQueryEmbeddingAsync(
                query.QueryAudioPath,
                modality: EmbeddingModalities.Audio,
                truncateDim: query.TruncateDim,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(query.QueryText))
        {
            queryEmbedding = await _embeddingService.GenerateQueryEmbeddingAsync(
                query.QueryText,
                modality: EmbeddingModalities.Text,
                truncateDim: query.TruncateDim,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // 2. Asset-scoped queries (a single file, e.g. the scenes of one video) take a dedicated path: only that
        //    asset's chunks are loaded and scored exactly. The global vector index is deliberately not consulted: its
        //    top-K cannot be relied on to contain an arbitrary small asset out of a large corpus, which is what made
        //    filtering to one file fall back to a full-corpus scan.
        if (query.AssetIdFilter is { } assetId)
        {
            return await SearchSingleAssetAsync(query, queryEmbedding, assetId, cancellationToken).ConfigureAwait(false);
        }

        // 2. Fetch candidates (cached until the indexed data changes)
        var snapshot = await GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var assets = snapshot.Assets;
        var chunkMap = snapshot.ChunkMap;
        var allChunks = snapshot.Chunks
            .Where(c => assets.TryGetValue(c.AssetId, out var a) && PassesFilters(query, a))
            .ToList();
        if (allChunks.Count == 0)
        {
            return Array.Empty<SearchResultItem>();
        }

        // 3. Vector Similarity using TensorPrimitives.CosineSimilarity.
        //    When the repository has a vector index (sqlite-vec), the nearest neighbours it returns become the
        //    candidates. Phase 2 uses a binary-quantization coarse pass rescored with exact cosine; when that is not
        //    available it falls back to the Phase-1 flat KNN and, last, to the brute-force scan. The exact cosine score
        //    is always computed here on the returned hits only, so the scores, ranks, fusion and de-duplication stay the
        //    same as the brute-force path. Any vec miss/error falls back to a scan.
        var allVectorScores = new Dictionary<Guid, float>();
        var vectorScores = new Dictionary<Guid, float>();
        // Groups of identical embeddings, used to collapse duplicates. On the vector path only the hits are grouped
        // (the few vectors fetched on demand); on the brute-force fallback every chunk is, as before.
        var embeddingGroups = new Dictionary<Guid, ulong>();
        if (queryEmbedding != null && queryEmbedding.Length > 0)
        {
            var vecGroups = await TryFillVectorScoresFromVecAsync(
                query, queryEmbedding, chunkMap, assets, allVectorScores, vectorScores, cancellationToken).ConfigureAwait(false);

            if (vecGroups == null)
            {
                // No usable vector index: load the full embeddings once and scan them. The metadata snapshot has no
                // vectors, so the scan runs over the separately cached full copy.
                var full = await GetFullChunksAsync(cancellationToken).ConfigureAwait(false);
                embeddingGroups = full.EmbeddingGroups;

                ReadOnlySpan<float> qSpan = queryEmbedding;
                foreach (var chunk in full.Chunks)
                {
                    if (!assets.TryGetValue(chunk.AssetId, out var asset) || !PassesFilters(query, asset)) continue;
                    if (chunk.Embedding.Length == 0) continue;

                    // If dimensions match, compute cosine similarity directly
                    // (or slice if query / target are MRL truncated)
                    int minDim = Math.Min(qSpan.Length, chunk.Embedding.Length);
                    var subQ = qSpan[..minDim];
                    var subChunk = chunk.Embedding.AsSpan()[..minDim];

                    float sim = TensorPrimitives.CosineSimilarity(subQ, subChunk);
                    allVectorScores[chunk.Id] = sim;
                    if (sim >= query.MinSimilarity)
                    {
                        vectorScores[chunk.Id] = sim;
                    }
                }
            }
            else
            {
                embeddingGroups = vecGroups;
            }

            // Safety fallback: if MinSimilarity was too strict and filtered out all items,
            // retain top candidates so results are not empty when items exist
            if (vectorScores.Count == 0 && allVectorScores.Count > 0)
            {
                foreach (var kv in allVectorScores.OrderByDescending(x => x.Value).Take(query.TopK))
                {
                    vectorScores[kv.Key] = kv.Value;
                }
            }
        }

        // 4. Optional FTS Search if query text is present
        var ftsScores = new Dictionary<Guid, float>();
        if (query.EnableFts && !string.IsNullOrWhiteSpace(query.QueryText))
        {
            var ftsResults = await _repository.SearchFtsAsync(query.QueryText, limit: 100, cancellationToken).ConfigureAwait(false);
            foreach (var (chunkId, rank) in ftsResults)
            {
                ftsScores[chunkId] = rank;
            }
        }

        // 5. Fusion / Ranking (Reciprocal Rank Fusion or Normalized Weighted Sum)
        return FuseAndRank(query, vectorScores, ftsScores, chunkMap, assets, embeddingGroups);
    }

    /// <summary>
    /// Searches one asset only (its scenes, for a video): all of its chunks are loaded and their exact cosine is
    /// computed in-process, so a selective filter always sees its whole candidate set instead of hoping a global KNN
    /// returned it. The vector index and the full-embedding fallback are never touched here. FTS fusion,
    /// de-duplication and video grouping are the same as the unscoped path, over this one asset.
    /// </summary>
    private async Task<IReadOnlyList<SearchResultItem>> SearchSingleAssetAsync(
        SearchQuery query,
        float[]? queryEmbedding,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await _repository.GetAssetByIdAsync(assetId, cancellationToken).ConfigureAwait(false);
        if (asset == null || !PassesFilters(query, asset)) return Array.Empty<SearchResultItem>();

        var chunks = await GetAssetChunksAsync(assetId, cancellationToken).ConfigureAwait(false);
        var chunkMap = chunks.ToDictionary(c => c.Id);

        var allVectorScores = new Dictionary<Guid, float>();
        var vectorScores = new Dictionary<Guid, float>();
        var embeddingGroups = new Dictionary<Guid, ulong>();

        if (queryEmbedding != null && queryEmbedding.Length > 0)
        {
            ReadOnlySpan<float> qSpan = queryEmbedding;
            foreach (var chunk in chunks)
            {
                if (chunk.Embedding.Length == 0) continue;

                int minDim = Math.Min(qSpan.Length, chunk.Embedding.Length);
                float sim = TensorPrimitives.CosineSimilarity(qSpan[..minDim], chunk.Embedding.AsSpan()[..minDim]);
                allVectorScores[chunk.Id] = sim;
                if (sim >= query.MinSimilarity)
                {
                    vectorScores[chunk.Id] = sim;
                }
            }

            // Safety fallback, identical to the unscoped path: never return nothing when candidates exist.
            if (vectorScores.Count == 0 && allVectorScores.Count > 0)
            {
                foreach (var kv in allVectorScores.OrderByDescending(x => x.Value).Take(query.TopK))
                {
                    vectorScores[kv.Key] = kv.Value;
                }
            }

            embeddingGroups = GroupIdenticalEmbeddings(chunks);
        }

        // FTS stays the repository's global query, exactly as the unscoped path; only the results are scoped to the
        // asset (the fusion step drops the other assets), so the RRF weighting of this asset's keyword hits is
        // unchanged. Scoping the keyword search itself to one asset is left to a future, index-level change.
        var ftsScores = new Dictionary<Guid, float>();
        if (query.EnableFts && !string.IsNullOrWhiteSpace(query.QueryText))
        {
            var ftsResults = await _repository.SearchFtsAsync(query.QueryText, limit: 100, cancellationToken).ConfigureAwait(false);
            foreach (var (chunkId, rank) in ftsResults)
            {
                ftsScores[chunkId] = rank;
            }
        }

        var assets = new Dictionary<Guid, MediaAsset> { [asset.Id] = asset };
        return FuseAndRank(query, vectorScores, ftsScores, chunkMap, assets, embeddingGroups);
    }

    /// <summary>
    /// Fuses the vector and FTS scores into ranked results (Reciprocal Rank Fusion), collapses identical embeddings
    /// and, for a grouped search, merges a video's scenes. Shared by the unscoped and the asset-scoped paths so their
    /// ranking is identical.
    /// </summary>
    private static IReadOnlyList<SearchResultItem> FuseAndRank(
        SearchQuery query,
        Dictionary<Guid, float> vectorScores,
        Dictionary<Guid, float> ftsScores,
        Dictionary<Guid, MediaChunk> chunkMap,
        Dictionary<Guid, MediaAsset> assets,
        Dictionary<Guid, ulong> embeddingGroups)
    {
        var targetChunkIds = vectorScores.Keys.Union(ftsScores.Keys).ToList();

        // Equal scores (e.g. identical images) are ordered by path so the ranking does not depend on storage order.
        string PathOf(Guid chunkId) => chunkMap.TryGetValue(chunkId, out var c) && assets.TryGetValue(c.AssetId, out var a) ? a.FilePath : string.Empty;
        var vectorRanks = RankById(vectorScores, PathOf);
        var ftsRanks = RankById(ftsScores, PathOf);

        var results = new List<SearchResultItem>();
        const float k = 60f; // RRF constant

        foreach (var id in targetChunkIds)
        {
            if (!chunkMap.TryGetValue(id, out var chunk)) continue;
            if (!assets.TryGetValue(chunk.AssetId, out var asset)) continue;
            if (!PassesFilters(query, asset)) continue; // keyword (FTS) hits bypass the candidate list, so filter again

            float simScore = vectorScores.GetValueOrDefault(id, 0f);
            float ftsScore = ftsScores.GetValueOrDefault(id, 0f);

            // RRF Score calculation
            float rrf = 0f;
            if (vectorRanks.TryGetValue(id, out int vRank))
            {
                rrf += 1.0f / (k + vRank + 1);
            }
            if (ftsRanks.TryGetValue(id, out int fRank))
            {
                rrf += 0.5f / (k + fRank + 1); // FTS weight
            }

            results.Add(new SearchResultItem
            {
                Chunk = chunk,
                Asset = asset,
                SimilarityScore = simScore,
                FtsScore = ftsScore,
                CombinedScore = rrf > 0 ? rrf : simScore
            });
        }

        var ranked = results
            .OrderByDescending(r => r.CombinedScore)
            .ThenBy(r => r.Asset.FilePath, StringComparer.OrdinalIgnoreCase);

        var collapsed = CollapseDuplicates(ranked, embeddingGroups);
        if (query.GroupVideoSegments)
        {
            // Group everything first (a video's scenes can be spread over the whole ranking), then cut the list.
            return GroupByVideo(collapsed).ToList().Take(query.TopK).ToList();
        }

        return collapsed
            .Take(query.TopK)
            .ToList();
    }

    /// <summary>
    /// Fills <paramref name="allVectorScores"/>/<paramref name="vectorScores"/> from the repository's vector index,
    /// preferring the binary-quantization rescored KNN (Phase 2) and falling back to the flat KNN (Phase 1). Only the
    /// exact vectors of the returned hits are fetched from the repository (never the whole table); the exact cosine is
    /// then computed here on those hits, so scores, ranks, fusion and de-duplication stay identical to the brute-force
    /// path. Returns the groups of identical embeddings over the fetched hits (the de-duplication input), or null when
    /// the index is unusable (an unversioned repository, no index, no usable hit, or any error) so the caller loads the
    /// full embeddings and brute-forces instead.
    /// </summary>
    private async Task<Dictionary<Guid, ulong>?> TryFillVectorScoresFromVecAsync(
        SearchQuery query,
        float[] queryEmbedding,
        Dictionary<Guid, MediaChunk> chunkMap,
        Dictionary<Guid, MediaAsset> assets,
        Dictionary<Guid, float> allVectorScores,
        Dictionary<Guid, float> vectorScores,
        CancellationToken cancellationToken)
    {
        // A negative DataVersion means the repository does not track changes; it is a test double / non-indexed repo.
        if (_repository.DataVersion < 0) return null;

        try
        {
            // Filters (media type, metadata, single asset) cannot be pushed into the KNN, so ask for extra candidates
            // and filter afterwards, otherwise a selective filter could empty the candidate list.
            int k = HasSelectiveFilters(query) ? Math.Max(query.TopK * 20, query.TopK) : query.TopK;

            // Phase 2 (binary-quantization coarse pass rescored exactly) first; if the repository has no rescored index
            // (default implementation / older schema) or it yields nothing, fall back to the Phase-1 flat KNN.
            var hits = await _repository.SearchVecRescoredAsync(queryEmbedding, k, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (hits.Count == 0)
            {
                hits = await _repository.SearchVecAsync(queryEmbedding, k, cancellationToken).ConfigureAwait(false);
            }
            if (hits.Count == 0) return null;

            // Keep only the hits that pass the filters and resolve to a chunk. The exact vectors are then fetched for
            // that (small) set only: this is what keeps a cold search from loading every embedding in the table.
            var candidateIds = new List<Guid>(hits.Count);
            foreach (var (chunkId, _) in hits)
            {
                if (!chunkMap.TryGetValue(chunkId, out var chunk)) continue;
                if (!assets.TryGetValue(chunk.AssetId, out var asset)) continue;
                if (!PassesFilters(query, asset)) continue;
                candidateIds.Add(chunkId);
            }
            if (candidateIds.Count == 0) return null;

            var embeddings = await _repository.GetEmbeddingsByChunkIdsAsync(candidateIds, cancellationToken).ConfigureAwait(false);

            ReadOnlySpan<float> qSpan = queryEmbedding;
            foreach (var chunkId in candidateIds)
            {
                if (!embeddings.TryGetValue(chunkId, out var embedding) || embedding.Length == 0) continue;

                int minDim = Math.Min(qSpan.Length, embedding.Length);
                float sim = TensorPrimitives.CosineSimilarity(qSpan[..minDim], embedding.AsSpan()[..minDim]);
                allVectorScores[chunkId] = sim;
                if (sim >= query.MinSimilarity)
                {
                    vectorScores[chunkId] = sim;
                }
            }

            // If every KNN hit was filtered out (or had no vector), report failure so the caller scans the filtered
            // candidate list instead. De-duplication is grouped over the fetched hits only, not the whole corpus.
            if (allVectorScores.Count == 0) return null;
            return GroupEmbeddings(embeddings);
        }
        catch
        {
            // A vector-index failure must never break search.
            return null;
        }
    }

    /// <summary>Applies the media-type and metadata filters of a query to one asset.</summary>
    public static bool PassesFilters(SearchQuery query, MediaAsset asset)
    {
        if (query.MediaTypeFilter is { } mediaType && asset.MediaType != mediaType) return false;
        if (query.AssetIdFilter is { } assetId && asset.Id != assetId) return false;
        if (query.ModifiedFromUtc is { } from && asset.LastModifiedUtc < from) return false;
        if (query.ModifiedToUtc is { } to && asset.LastModifiedUtc >= to) return false;
        if (query.MinSizeBytes is { } min && asset.FileSizeBytes < min) return false;
        if (query.MaxSizeBytes is { } max && asset.FileSizeBytes > max) return false;
        if (!string.IsNullOrWhiteSpace(query.FolderContains))
        {
            var dir = System.IO.Path.GetDirectoryName(asset.FilePath) ?? string.Empty;
            if (!dir.Contains(query.FolderContains.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
