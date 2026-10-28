using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;


namespace EmbeddingSearchViewer.Core.Services;

/// <summary>Stateless ranking helpers of the search: identical-embedding grouping, duplicate collapsing and video grouping.</summary>
internal static class SearchPostProcessing
{
    /// <summary>True when the query restricts candidates by media type, a single asset or metadata.</summary>
    internal static bool HasSelectiveFilters(SearchQuery query) =>
        query.MediaTypeFilter.HasValue || query.AssetIdFilter.HasValue || query.ExcludeAssetId.HasValue || query.HasMetadataFilters;

    /// <summary>
    /// Replaces the scenes of each video by one result at the rank of its best scene; that result lists all the matching
    /// scenes of the video (best first, itself included). Images and PDF pages pass through unchanged.
    /// </summary>
    internal static IEnumerable<SearchResultItem> GroupByVideo(IEnumerable<SearchResultItem> ranked)
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
    internal static Dictionary<Guid, ulong> GroupIdenticalEmbeddings(IReadOnlyList<MediaChunk> chunks)
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
    /// The same grouping, over an explicit id→embedding map: used by the vector path so only the handful of fetched
    /// hit vectors are hashed instead of the whole corpus.
    /// </summary>
    internal static Dictionary<Guid, ulong> GroupEmbeddings(IReadOnlyDictionary<Guid, float[]> embeddings)
    {
        var groups = new Dictionary<Guid, ulong>(embeddings.Count);
        foreach (var (id, embedding) in embeddings)
        {
            if (embedding.Length == 0) continue;
            var hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(embedding.AsSpan()));
            groups[id] = BitConverter.ToUInt64(hash, 0) | 1UL; // never 0
        }
        return groups;
    }

    /// <summary>
    /// Keeps the best-ranked result of each group of identical embeddings and notes the other files on it,
    /// so identical images do not fill the result list.
    /// </summary>
    internal static IEnumerable<SearchResultItem> CollapseDuplicates(IEnumerable<SearchResultItem> ranked, Dictionary<Guid, ulong> groups)
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

    internal static Dictionary<Guid, int> RankById(Dictionary<Guid, float> scores, Func<Guid, string> tieBreakPath)
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
