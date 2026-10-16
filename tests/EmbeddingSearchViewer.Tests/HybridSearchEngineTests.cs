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
}
