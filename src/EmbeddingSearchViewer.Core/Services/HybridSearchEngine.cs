using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Core.Services;

public class HybridSearchEngine
{
    private readonly IMediaRepository _repository;
    private readonly IEmbeddingService _embeddingService;

    private sealed record Snapshot(
        long Version,
        IReadOnlyList<MediaChunk> Chunks,
        Dictionary<Guid, MediaChunk> ChunkMap,
        Dictionary<Guid, MediaAsset> Assets,
        Dictionary<Guid, ulong> EmbeddingGroups);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private Snapshot? _snapshot;

    /// <summary>
    /// Returns all chunks and assets, reusing the in-memory copy while the repository data is unchanged.
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

            var chunks = await _repository.GetAllChunksWithEmbeddingsAsync(null, cancellationToken).ConfigureAwait(false);
            var assets = (await _repository.GetAllAssetsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.Id);
            var snapshot = new Snapshot(version, chunks, chunks.ToDictionary(c => c.Id), assets, GroupIdenticalEmbeddings(chunks));
            if (version >= 0) _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _loadLock.Release();
        }
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
        if (queryEmbedding != null && queryEmbedding.Length > 0)
        {
            bool fromVec = await TryFillVectorScoresFromVecAsync(
                query, queryEmbedding, chunkMap, assets, allVectorScores, vectorScores, cancellationToken).ConfigureAwait(false);

            if (!fromVec)
            {
                ReadOnlySpan<float> qSpan = queryEmbedding;
                foreach (var chunk in allChunks)
                {
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

        var collapsed = CollapseDuplicates(ranked, snapshot.EmbeddingGroups);
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
    /// preferring the binary-quantization rescored KNN (Phase 2) and falling back to the flat KNN (Phase 1). The exact
    /// cosine score is always recomputed here on the returned hits, so the scores, ranks, fusion and de-duplication stay
    /// identical whichever index path produced the candidates. Returns false when the index is unusable (an unversioned
    /// repository, no index, no usable hit, or any error) so the caller brute-forces instead.
    /// </summary>
    private async Task<bool> TryFillVectorScoresFromVecAsync(
        SearchQuery query,
        float[] queryEmbedding,
        Dictionary<Guid, MediaChunk> chunkMap,
        Dictionary<Guid, MediaAsset> assets,
        Dictionary<Guid, float> allVectorScores,
        Dictionary<Guid, float> vectorScores,
        CancellationToken cancellationToken)
    {
        // A negative DataVersion means the repository does not track changes; it is a test double / non-indexed repo.
        if (_repository.DataVersion < 0) return false;

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
            if (hits.Count == 0) return false;

            ReadOnlySpan<float> qSpan = queryEmbedding;
            foreach (var (chunkId, _) in hits)
            {
                if (!chunkMap.TryGetValue(chunkId, out var chunk)) continue;
                if (!assets.TryGetValue(chunk.AssetId, out var asset)) continue;
                if (!PassesFilters(query, asset)) continue;
                if (chunk.Embedding.Length == 0) continue;

                int minDim = Math.Min(qSpan.Length, chunk.Embedding.Length);
                float sim = TensorPrimitives.CosineSimilarity(qSpan[..minDim], chunk.Embedding.AsSpan()[..minDim]);
                allVectorScores[chunk.Id] = sim;
                if (sim >= query.MinSimilarity)
                {
                    vectorScores[chunk.Id] = sim;
                }
            }

            // If every KNN hit was filtered out, report failure so the caller scans the filtered candidate list instead.
            return allVectorScores.Count > 0;
        }
        catch
        {
            // A vector-index failure must never break search.
            return false;
        }
    }

    /// <summary>True when the query restricts candidates by media type, a single asset or metadata.</summary>
    private static bool HasSelectiveFilters(SearchQuery query) =>
        query.MediaTypeFilter.HasValue || query.AssetIdFilter.HasValue || query.HasMetadataFilters;

    /// <summary>
    /// Replaces the scenes of each video by one result at the rank of its best scene; that result lists all the matching
    /// scenes of the video (best first, itself included). Images and PDF pages pass through unchanged.
    /// </summary>
    private static IEnumerable<SearchResultItem> GroupByVideo(IEnumerable<SearchResultItem> ranked)
    {
        var videos = new Dictionary<Guid, SearchResultItem>();
        foreach (var item in ranked)
        {
            if (item.Asset.MediaType != MediaType.Video)
            {
                yield return item;
                continue;
            }

            if (videos.TryGetValue(item.Asset.Id, out var representative))
            {
                representative.Scenes.Add(item);
                continue;
            }

            item.Scenes.Add(item);
            videos[item.Asset.Id] = item;
            yield return item;
        }
    }

    /// <summary>Gives chunks with exactly the same embedding the same non-zero group id (0 = no embedding).</summary>
    private static Dictionary<Guid, ulong> GroupIdenticalEmbeddings(IReadOnlyList<MediaChunk> chunks)
    {
        var groups = new Dictionary<Guid, ulong>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.Embedding.Length == 0) continue;
            var hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Embedding.AsSpan()));
            groups[chunk.Id] = BitConverter.ToUInt64(hash, 0) | 1UL; // never 0
        }
        return groups;
    }

    /// <summary>
    /// Keeps the best-ranked result of each group of identical embeddings and notes the other files on it,
    /// so identical images do not fill the result list.
    /// </summary>
    private static IEnumerable<SearchResultItem> CollapseDuplicates(IEnumerable<SearchResultItem> ranked, Dictionary<Guid, ulong> groups)
    {
        var representatives = new Dictionary<ulong, SearchResultItem>();
        foreach (var item in ranked)
        {
            if (groups.TryGetValue(item.Chunk.Id, out var group))
            {
                if (representatives.TryGetValue(group, out var representative))
                {
                    if (!string.Equals(representative.Asset.FilePath, item.Asset.FilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        representative.DuplicatePaths.Add(item.Asset.FilePath);
                    }
                    continue;
                }
                representatives[group] = item;
            }
            yield return item;
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

    private static Dictionary<Guid, int> RankById(Dictionary<Guid, float> scores, Func<Guid, string> tieBreakPath)
    {
        var ranks = new Dictionary<Guid, int>(scores.Count);
        int rank = 0;
        foreach (var kv in scores.OrderByDescending(kv => kv.Value).ThenBy(kv => tieBreakPath(kv.Key), StringComparer.OrdinalIgnoreCase))
        {
            ranks[kv.Key] = rank++;
        }
        return ranks;
    }
}
