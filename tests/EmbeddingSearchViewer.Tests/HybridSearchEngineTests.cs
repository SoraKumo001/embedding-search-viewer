using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Core.Services;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public class HybridSearchEngineTests
{
    private class MockEmbeddingService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(
            string query,
            string modality = "text",
            int truncateDim = 768,
            CancellationToken cancellationToken = default)
        {
            // Return deterministic unit vector
            float[] vec = new float[truncateDim];
            vec[0] = 1.0f; // Unit vector pointing along dimension 0
            return Task.FromResult(vec);
        }

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
            IEnumerable<EmbeddingItemInput> items,
            int truncateDim = 768,
            CancellationToken cancellationToken = default)
        {
            var list = items.Select(_ =>
            {
                float[] vec = new float[truncateDim];
                vec[0] = 1.0f;
                return vec;
            }).ToList();
            return Task.FromResult<IReadOnlyList<float[]>>(list);
        }
    }

    private class MockMediaRepository : IMediaRepository
    {
        public List<MediaAsset> Assets { get; } = new();
        public List<MediaChunk> Chunks { get; } = new();
        public List<(Guid ChunkId, float FtsRank)> FtsResults { get; } = new();
        public long DataVersion { get; set; } = -1;
        public int ChunkLoadCount { get; private set; }
        public int AssetChunkLoadCount { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(Assets.FirstOrDefault(a => a.FilePath == filePath));
        public Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Assets.FirstOrDefault(a => a.Id == assetId));
        public Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MediaAsset>>(Assets);
        public Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default)
        {
            ChunkLoadCount++;
            if (mediaTypeFilter.HasValue)
            {
                var assetIds = Assets.Where(a => a.MediaType == mediaTypeFilter.Value).Select(a => a.Id).ToHashSet();
                return Task.FromResult<IReadOnlyList<MediaChunk>>(Chunks.Where(c => assetIds.Contains(c.AssetId)).ToList());
            }
            return Task.FromResult<IReadOnlyList<MediaChunk>>(Chunks);
        }
        public Task<IReadOnlyList<MediaChunk>> GetChunksByAssetIdAsync(Guid assetId, CancellationToken cancellationToken = default)
        {
            AssetChunkLoadCount++;
            return Task.FromResult<IReadOnlyList<MediaChunk>>(Chunks.Where(c => c.AssetId == assetId).ToList());
        }
        public Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<(Guid ChunkId, float FtsRank)>>(FtsResults);
        public Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task SearchAsync_ReusesCacheUntilDataVersionChanges()
    {
        var repo = new MockMediaRepository { DataVersion = 1 };
        var engine = new HybridSearchEngine(repo, new MockEmbeddingService());
        var query = new SearchQuery { QueryText = "x", EnableFts = false };

        await engine.SearchAsync(query);
        await engine.SearchAsync(query);
        Assert.Equal(1, repo.ChunkLoadCount);

        repo.DataVersion = 2;
        await engine.SearchAsync(query);
        Assert.Equal(2, repo.ChunkLoadCount);
    }

    [Fact]
    public async Task SearchAsync_DoesNotCacheWhenRepositoryIsUnversioned()
    {
        var repo = new MockMediaRepository(); // DataVersion = -1
        var engine = new HybridSearchEngine(repo, new MockEmbeddingService());
        var query = new SearchQuery { QueryText = "x", EnableFts = false };

        await engine.SearchAsync(query);
        await engine.SearchAsync(query);
        Assert.Equal(2, repo.ChunkLoadCount);
    }

    [Fact]
    public async Task SearchAsync_WithMatchingVector_ReturnsSortedResults()
    {
        // Arrange
        var repo = new MockMediaRepository();
        var embeddingService = new MockEmbeddingService();
        var engine = new HybridSearchEngine(repo, embeddingService);

        var asset1 = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FileName = "sample1.png",
            FilePath = "C:\\test\\sample1.png",
            MediaType = MediaType.Image
        };
        var asset2 = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FileName = "sample2.png",
            FilePath = "C:\\test\\sample2.png",
            MediaType = MediaType.Image
        };
        repo.Assets.Add(asset1);
        repo.Assets.Add(asset2);

        // Chunk 1 has perfect cosine similarity (1.0)
        float[] v1 = new float[768];
        v1[0] = 1.0f;
        var chunk1 = new MediaChunk
        {
            Id = Guid.NewGuid(),
            AssetId = asset1.Id,
            Modality = ModalityType.Image,
            Embedding = v1
        };

        // Chunk 2 has lower cosine similarity (0.5)
        float[] v2 = new float[768];
        v2[0] = 0.5f;
        v2[1] = MathF.Sqrt(0.75f); // unit vector
        var chunk2 = new MediaChunk
        {
            Id = Guid.NewGuid(),
            AssetId = asset2.Id,
            Modality = ModalityType.Image,
            Embedding = v2
        };

        repo.Chunks.Add(chunk1);
        repo.Chunks.Add(chunk2);

        var query = new SearchQuery
        {
            QueryText = "test query",
            MinSimilarity = 0.0f,
            TopK = 10
        };

        // Act
        var results = await engine.SearchAsync(query);

        // Assert
        Assert.Equal(2, results.Count);
        Assert.Equal(chunk1.Id, results[0].Chunk.Id);
        Assert.True(results[0].SimilarityScore > results[1].SimilarityScore);
        Assert.InRange(results[0].SimilarityScore, 0.99f, 1.01f);
    }

    [Fact]
    public async Task SearchAsync_WhenThresholdFiltersAll_ReturnsSafetyFallbackTopK()
    {
        // Arrange
        var repo = new MockMediaRepository();
        var embeddingService = new MockEmbeddingService();
        var engine = new HybridSearchEngine(repo, embeddingService);

        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FileName = "low_sim.png",
            FilePath = "C:\\test\\low_sim.png",
            MediaType = MediaType.Image
        };
        repo.Assets.Add(asset);

        // Similarity is 0.1, but query threshold is 0.9
        float[] v = new float[768];
        v[0] = 0.1f;
        v[1] = MathF.Sqrt(1.0f - 0.01f);
        var chunk = new MediaChunk
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            Modality = ModalityType.Image,
            Embedding = v
        };
        repo.Chunks.Add(chunk);

        var query = new SearchQuery
        {
            QueryText = "query",
            MinSimilarity = 0.9f, // Strict threshold
            TopK = 5
        };

        // Act
        var results = await engine.SearchAsync(query);

        // Assert: Safety fallback should prevent 0 results
        Assert.Single(results);
        Assert.Equal(chunk.Id, results[0].Chunk.Id);
    }

    [Fact]
    public async Task SearchAsync_WhenVectorIndexReturnsNothing_FallsBackToBruteForce()
    {
        // Arrange: a versioned repository (so the vec-first path is attempted) whose index yields nothing.
        var repo = new MockMediaRepository { DataVersion = 7 };
        var embeddingService = new MockEmbeddingService();
        var engine = new HybridSearchEngine(repo, embeddingService);

        var asset1 = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FileName = "a.png",
            FilePath = "C:\\test\\a.png",
            MediaType = MediaType.Image
        };
        var asset2 = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FileName = "b.png",
            FilePath = "C:\\test\\b.png",
            MediaType = MediaType.Image
        };
        repo.Assets.Add(asset1);
        repo.Assets.Add(asset2);

        float[] v1 = new float[768];
        v1[0] = 1.0f;
        var chunk1 = new MediaChunk { Id = Guid.NewGuid(), AssetId = asset1.Id, Modality = ModalityType.Image, Embedding = v1 };

        float[] v2 = new float[768];
        v2[0] = 0.5f;
        v2[1] = MathF.Sqrt(0.75f);
        var chunk2 = new MediaChunk { Id = Guid.NewGuid(), AssetId = asset2.Id, Modality = ModalityType.Image, Embedding = v2 };

        repo.Chunks.Add(chunk1);
        repo.Chunks.Add(chunk2);

        var query = new SearchQuery { QueryText = "q", MinSimilarity = 0.0f, TopK = 10, EnableFts = false };

        // Act
        var results = await engine.SearchAsync(query);

        // Assert: the empty index result triggers the brute-force scan, which still finds both chunks correctly.
        Assert.Equal(2, results.Count);
        Assert.Equal(chunk1.Id, results[0].Chunk.Id);
        Assert.True(results[0].SimilarityScore > results[1].SimilarityScore);
    }

    [Fact]
    public async Task SearchAsync_WithAssetFilter_UsesOnlyThatAsset_WithoutLoadingTheWholeCorpus()
    {
        // Arrange: two videos; the other one holds a better match that must be ignored.
        var repo = new MockMediaRepository { DataVersion = 5 };
        var engine = new HybridSearchEngine(repo, new MockEmbeddingService());

        var target = new MediaAsset { Id = Guid.NewGuid(), FileName = "v.mp4", FilePath = "C:\\test\\v.mp4", MediaType = MediaType.Video };
        var other = new MediaAsset { Id = Guid.NewGuid(), FileName = "o.mp4", FilePath = "C:\\test\\o.mp4", MediaType = MediaType.Video };
        repo.Assets.Add(target);
        repo.Assets.Add(other);

        float[] perfect = new float[768];
        perfect[0] = 1.0f;
        float[] lower = new float[768];
        lower[0] = 0.5f;
        lower[1] = MathF.Sqrt(0.75f);

        var targetBest = new MediaChunk { Id = Guid.NewGuid(), AssetId = target.Id, Modality = ModalityType.VideoSegment, Embedding = perfect };
        var targetWorse = new MediaChunk { Id = Guid.NewGuid(), AssetId = target.Id, Modality = ModalityType.VideoSegment, Embedding = lower };
        var otherBest = new MediaChunk { Id = Guid.NewGuid(), AssetId = other.Id, Modality = ModalityType.VideoSegment, Embedding = perfect };
        repo.Chunks.Add(targetBest);
        repo.Chunks.Add(targetWorse);
        repo.Chunks.Add(otherBest);

        var query = new SearchQuery { QueryText = "x", EnableFts = false, MinSimilarity = 0f, TopK = 10, AssetIdFilter = target.Id };

        // Act
        var results = await engine.SearchAsync(query);

        // Assert: only the target video's chunks, ranked exactly, and the full-corpus load never happened.
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(target.Id, r.Asset.Id));
        Assert.Equal(targetBest.Id, results[0].Chunk.Id);
        Assert.Equal(0, repo.ChunkLoadCount);       // GetAllChunksWithEmbeddingsAsync (whole corpus) never called
        Assert.Equal(1, repo.AssetChunkLoadCount);  // only the asset's chunks were loaded
    }

    [Fact]
    public async Task SearchAsync_AssetScoped_ReusesCacheUntilDataVersionChanges()
    {
        var repo = new MockMediaRepository { DataVersion = 3 };
        var engine = new HybridSearchEngine(repo, new MockEmbeddingService());

        var asset = new MediaAsset { Id = Guid.NewGuid(), FileName = "v.mp4", FilePath = "C:\\test\\v.mp4", MediaType = MediaType.Video };
        repo.Assets.Add(asset);
        float[] v = new float[768];
        v[0] = 1.0f;
        repo.Chunks.Add(new MediaChunk { Id = Guid.NewGuid(), AssetId = asset.Id, Modality = ModalityType.VideoSegment, Embedding = v });

        var query = new SearchQuery { QueryText = "x", EnableFts = false, MinSimilarity = 0f, TopK = 10, AssetIdFilter = asset.Id };

        await engine.SearchAsync(query);
        await engine.SearchAsync(query);
        Assert.Equal(1, repo.AssetChunkLoadCount);

        repo.DataVersion = 4;
        await engine.SearchAsync(query);
        Assert.Equal(2, repo.AssetChunkLoadCount);
    }

    private class ThrowingEmbeddingService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("the query embedding must not be generated");

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not used");
    }

    private static (MockMediaRepository Repo, MediaAsset Source, MediaAsset Other, MediaChunk SourceChunk) SimilarFixture()
    {
        var repo = new MockMediaRepository();
        MediaAsset Asset(string name) => new() { Id = Guid.NewGuid(), FileName = name, FilePath = "C:\\test\\" + name, MediaType = MediaType.Image };
        var source = Asset("source.png");
        var other = Asset("other.png");
        repo.Assets.Add(source);
        repo.Assets.Add(other);

        MediaChunk Chunk(MediaAsset a, float second) => new()
        {
            Id = Guid.NewGuid(),
            AssetId = a.Id,
            Modality = ModalityType.Image,
            Embedding = new[] { 1f, second, 0f }
        };
        var sourceChunk = Chunk(source, 0f);
        repo.Chunks.Add(sourceChunk);
        repo.Chunks.Add(Chunk(other, 0.1f));
        return (repo, source, other, sourceChunk);
    }

    [Fact]
    public async Task SearchAsync_QueryEmbedding_SkipsEmbeddingGenerationAndExcludesSourceAsset()
    {
        var (repo, source, other, sourceChunk) = SimilarFixture();
        var engine = new HybridSearchEngine(repo, new ThrowingEmbeddingService());

        var results = await engine.SearchAsync(new SearchQuery
        {
            QueryEmbedding = sourceChunk.Embedding,
            ExcludeAssetId = source.Id,
            EnableFts = false,
            MinSimilarity = 0f
        });

        var hit = Assert.Single(results);
        Assert.Equal(other.Id, hit.Asset.Id);
    }

    [Fact]
    public async Task SearchAsync_QueryEmbedding_WithoutExclusionFindsSourceFirst()
    {
        var (repo, source, _, sourceChunk) = SimilarFixture();
        var engine = new HybridSearchEngine(repo, new ThrowingEmbeddingService());

        var results = await engine.SearchAsync(new SearchQuery
        {
            QueryEmbedding = sourceChunk.Embedding,
            EnableFts = false,
            MinSimilarity = 0f
        });

        Assert.Equal(2, results.Count);
        Assert.Equal(source.Id, results[0].Asset.Id);
    }

    [Fact]
    public async Task GetChunkEmbeddingAsync_ReturnsStoredEmbeddingOrNull()
    {
        var (repo, _, _, sourceChunk) = SimilarFixture();
        var engine = new HybridSearchEngine(repo, new ThrowingEmbeddingService());

        Assert.Equal(sourceChunk.Embedding, await engine.GetChunkEmbeddingAsync(sourceChunk.Id));
        Assert.Null(await engine.GetChunkEmbeddingAsync(Guid.NewGuid()));
    }
}
