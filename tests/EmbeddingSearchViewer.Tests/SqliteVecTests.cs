using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Core.Services;
using EmbeddingSearchViewer.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>
/// Exercises the sqlite-vec (vec0) index against a real database file: the float[768] flat KNN index (Phase 1), the
/// binary-quantization <c>bit[768]</c> coarse column and its rescore path (Phase 2), the triggers that keep both in
/// step with <c>MediaChunks</c>, the backfill/migration of pre-existing rows, and the hybrid engine's vec-first search.
/// </summary>
public sealed class SqliteVecTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "esv_vec_" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    /// <summary>A unit vector turned away from the query axis ([1,0,...]) by <paramref name="theta"/> radians.</summary>
    private static float[] AngleVector(float theta)
    {
        var v = new float[768];
        v[0] = MathF.Cos(theta);
        v[1] = MathF.Sin(theta);
        return v;
    }

    private static float[] QueryVector()
    {
        var q = new float[768];
        q[0] = 1f;
        return q;
    }

    private async Task<SqliteMediaRepository> CreateInitializedRepoAsync()
    {
        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();
        return repo;
    }

    private async Task<List<(MediaAsset Asset, MediaChunk Chunk)>> SeedAsync(
        SqliteMediaRepository repo, int count, MediaType type = MediaType.Image)
    {
        var seeded = new List<(MediaAsset, MediaChunk)>(count);
        for (int i = 0; i < count; i++)
        {
            // theta grows with i, so chunk 0 is the nearest to the query and the order is unambiguous.
            var asset = new MediaAsset
            {
                FileName = $"v{i}.png",
                FilePath = $@"C:\vec\{i}.png",
                MediaType = type,
                FileSizeBytes = 1024,
                LastModifiedUtc = DateTime.UtcNow
            };
            var chunk = new MediaChunk { AssetId = asset.Id, Modality = ModalityType.Image, Embedding = AngleVector((i + 1) * 0.01f) };
            await repo.SaveAssetWithChunksAsync(asset, new[] { chunk });
            seeded.Add((asset, chunk));
        }
        return seeded;
    }

    /// <summary>Stores many vectors of one asset / one transaction and returns the chunks in storage order.</summary>
    private async Task<List<MediaChunk>> SeedVectorsAsync(SqliteMediaRepository repo, float[][] vectors, MediaType type = MediaType.Image)
    {
        var asset = new MediaAsset
        {
            FileName = "cluster.bin",
            FilePath = @"C:\vec\cluster.bin",
            MediaType = type,
            FileSizeBytes = 1024,
            LastModifiedUtc = DateTime.UtcNow
        };
        var chunks = new List<MediaChunk>(vectors.Length);
        for (int i = 0; i < vectors.Length; i++)
        {
            chunks.Add(new MediaChunk { AssetId = asset.Id, Modality = ModalityType.Image, ChunkIndex = i, Embedding = vectors[i] });
        }
        await repo.SaveAssetWithChunksAsync(asset, chunks);
        return chunks;
    }

    private static SqliteConnection OpenWithVec(string dbPath)
    {
        var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        conn.EnableExtensions(true);
        conn.LoadExtension(FindVec0());
        return conn;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string FindVec0()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "vec0.dll") };
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string dirs)
        {
            candidates.AddRange(dirs.Split(Path.PathSeparator)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => Path.Combine(d.Trim(), "vec0.dll")));
        }
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "vec0.dll"));

        return candidates.First(File.Exists);
    }

    private class FixedEmbeddingService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult(QueryVector());

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items.Select(_ => QueryVector()).ToList());
    }

    [Fact]
    public async Task SearchVecAsync_ReturnsNearestFirst()
    {
        var repo = await CreateInitializedRepoAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load; check that vec0.dll is shipped under runtimes/win-x64/native");

        var seeded = await SeedAsync(repo, 100);

        var hits = await repo.SearchVecAsync(QueryVector(), 10);

        Assert.NotEmpty(hits);
        Assert.Equal(seeded[0].Chunk.Id, hits[0].ChunkId);

        var distances = hits.Select(h => h.Distance).ToList();
        Assert.Equal(distances.OrderBy(d => d).ToList(), distances);
        Assert.All(hits, h => Assert.Contains(h.ChunkId, seeded.Select(s => s.Chunk.Id)));
    }

    [Fact]
    public async Task SearchVecAsync_ReturnsEmpty_ForUnexpectedDimension()
    {
        var repo = await CreateInitializedRepoAsync();
        await SeedAsync(repo, 5);

        // A 256-dim (MRL-truncated) query cannot match the float[768] column: no index hits, so search falls back.
        Assert.Empty(await repo.SearchVecAsync(new float[256], 10));
    }

    [Fact]
    public async Task DeleteTrigger_RemovesVectorFromIndex()
    {
        var repo = await CreateInitializedRepoAsync();
        var seeded = await SeedAsync(repo, 20);

        var nearestBefore = await repo.SearchVecAsync(QueryVector(), 20);
        Assert.Equal(seeded[0].Chunk.Id, nearestBefore[0].ChunkId);

        await repo.DeleteAssetAsync(seeded[0].Asset.Id);

        var nearestAfter = await repo.SearchVecAsync(QueryVector(), 20);
        Assert.DoesNotContain(seeded[0].Chunk.Id, nearestAfter.Select(h => h.ChunkId));
        Assert.Equal(seeded[1].Chunk.Id, nearestAfter[0].ChunkId);
    }

    [Fact]
    public async Task ClearAllAsync_EmptiesVectorIndex()
    {
        var repo = await CreateInitializedRepoAsync();
        await SeedAsync(repo, 10);

        await repo.ClearAllAsync();

        Assert.Empty(await repo.SearchVecAsync(QueryVector(), 10));
    }

    [Fact]
    public async Task EnsureVecBackfillAsync_IndexesPreExistingRows_ThenIsNoOp()
    {
        var repo = await CreateInitializedRepoAsync();
        var seeded = await SeedAsync(repo, 50);

        // Simulate rows that were indexed before the vector index existed: wipe the index, keep MediaChunks.
        using (var raw = OpenWithVec(_dbPath))
        {
            using var del = raw.CreateCommand();
            del.CommandText = "DELETE FROM VecMediaChunks;";
            del.ExecuteNonQuery();
        }
        Assert.Empty(await repo.SearchVecAsync(QueryVector(), 10));

        int inserted = await repo.EnsureVecBackfillAsync();

        Assert.Equal(50, inserted);
        var hits = await repo.SearchVecAsync(QueryVector(), 10);
        Assert.Equal(seeded[0].Chunk.Id, hits[0].ChunkId);

        // A second run has nothing to add.
        Assert.Equal(0, await repo.EnsureVecBackfillAsync());
    }

    [Fact]
    public async Task HybridEngine_VecFirst_RespectsMediaTypeFilterWithOversampling()
    {
        var repo = await CreateInitializedRepoAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load");

        // Mostly images, a handful of videos. Only the videos may come back when the query filters for video.
        await SeedAsync(repo, 40, MediaType.Image);
        var videos = await SeedAsync(repo, 5, MediaType.Video);

        var engine = new HybridSearchEngine(repo, new FixedEmbeddingService());
        var query = new SearchQuery
        {
            QueryText = "anything",
            EnableFts = false,
            MediaTypeFilter = MediaType.Video,
            MinSimilarity = 0.0f,
            TopK = 10
        };

        var results = await engine.SearchAsync(query);

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal(MediaType.Video, r.Asset.MediaType));
        // The nearest video (first seeded video has the smallest angle) ranks first.
        Assert.Equal(videos[0].Chunk.Id, results[0].Chunk.Id);
    }

    [Fact]
    public async Task HybridEngine_VecFirst_MatchesBruteForceOrdering()
    {
        var repo = await CreateInitializedRepoAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load");

        var seeded = await SeedAsync(repo, 30);
        var engine = new HybridSearchEngine(repo, new FixedEmbeddingService());
        var query = new SearchQuery { QueryText = "anything", EnableFts = false, MinSimilarity = 0.0f, TopK = 10 };

        var results = await engine.SearchAsync(query);

        Assert.Equal(10, results.Count);
        Assert.Equal(seeded[0].Chunk.Id, results[0].Chunk.Id);
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(results[i - 1].SimilarityScore >= results[i].SimilarityScore);
        }
    }

    // ---- Phase 2: binary-quantization coarse column + rescore ----

    [Fact]
    public async Task CoarseColumn_IsPopulatedByTrigger_AndIsBinarySized()
    {
        var repo = await CreateInitializedRepoAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load");
        await SeedAsync(repo, 25);

        using var raw = OpenWithVec(_dbPath);

        // The trigger declared the coarse column and fills it for every inserted vector.
        Assert.Equal(1L, ScalarLong(raw, "SELECT count(*) FROM pragma_table_info('VecMediaChunks') WHERE name = 'embedding_coarse';"));
        // A bit[768] code is 768 bits = 96 bytes.
        Assert.Equal(25L, ScalarLong(raw, "SELECT count(*) FROM VecMediaChunks WHERE embedding_coarse IS NOT NULL AND length(embedding_coarse) = 96;"));
    }

    [Fact]
    public async Task SearchVecRescoredAsync_ReturnsCosineOrderedNearestFirst()
    {
        var repo = await CreateInitializedRepoAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load");

        // One known nearest (the query axis) among random unit vectors: the coarse codes differ, so the rescore path
        // resolves the exact order like the flat KNN does.
        var rnd = new Random(11);
        var query = QueryVector();
        var vectors = new float[100][];
        vectors[0] = (float[])query.Clone();
        for (int i = 1; i < vectors.Length; i++) vectors[i] = Normalize(RandomGaussian(rnd, 768));
        var chunks = await SeedVectorsAsync(repo, vectors);

        var hits = await repo.SearchVecRescoredAsync(query, 10, oversample: 8);

        Assert.NotEmpty(hits);
        Assert.Equal(chunks[0].Id, hits[0].ChunkId);
        var distances = hits.Select(h => h.Distance).ToList();
        Assert.Equal(distances.OrderBy(d => d).ToList(), distances);
        Assert.All(hits, h => Assert.Contains(h.ChunkId, chunks.Select(c => c.Id)));
    }

    [Fact]
    public async Task SearchVecRescoredAsync_ReturnsEmpty_ForUnexpectedDimension()
    {
        var repo = await CreateInitializedRepoAsync();
        await SeedAsync(repo, 5);

        // A 256-dim query cannot match the float[768]/bit[768] columns: the caller falls back to the flat KNN/scan.
        Assert.Empty(await repo.SearchVecRescoredAsync(new float[256], 10));
    }

    [Fact]
    public async Task Initialize_MigratesPhase1Index_AddingCoarseColumn()
    {
        var repo = await CreateInitializedRepoAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load");
        var seeded = await SeedAsync(repo, 30);

        // Rebuild the index as the Phase-1 schema (exact column only), as an older install would have left it.
        using (var raw = OpenWithVec(_dbPath))
        {
            Exec(raw, "DROP TABLE VecMediaChunks;");
            Exec(raw, "CREATE VIRTUAL TABLE VecMediaChunks USING vec0(chunk_id INTEGER PRIMARY KEY, embedding float[768] distance_metric=cosine);");
            Exec(raw, "INSERT INTO VecMediaChunks(chunk_id, embedding) SELECT rowid, Embedding FROM MediaChunks WHERE Embedding IS NOT NULL AND length(Embedding) = 3072;");
            Assert.Equal(0L, ScalarLong(raw, "SELECT count(*) FROM pragma_table_info('VecMediaChunks') WHERE name = 'embedding_coarse';"));
        }

        // Re-initialising the same database must detect the missing column, rebuild the table and backfill the codes.
        var reopened = new SqliteMediaRepository(_dbPath);
        await reopened.InitializeAsync();

        using (var raw = OpenWithVec(_dbPath))
        {
            Assert.Equal(1L, ScalarLong(raw, "SELECT count(*) FROM pragma_table_info('VecMediaChunks') WHERE name = 'embedding_coarse';"));
            Assert.Equal(30L, ScalarLong(raw, "SELECT count(*) FROM VecMediaChunks WHERE embedding_coarse IS NOT NULL;"));
        }

        var hits = await reopened.SearchVecRescoredAsync(QueryVector(), 10);
        Assert.Equal(seeded[0].Chunk.Id, hits[0].ChunkId);
    }

    [Fact]
    public async Task EnsureVecBackfillAsync_RepairsRowsMissingFromIndex()
    {
        var repo = await CreateInitializedRepoAsync();
        await SeedAsync(repo, 20);

        // Simulate a partially-built / stale index: only part of the vectors are present.
        using (var raw = OpenWithVec(_dbPath))
        {
            Exec(raw, "DELETE FROM VecMediaChunks WHERE chunk_id <= 5;");
            Assert.Equal(15L, ScalarLong(raw, "SELECT count(*) FROM VecMediaChunks;"));
        }

        int inserted = await repo.EnsureVecBackfillAsync();

        Assert.Equal(5, inserted);
        using (var raw = OpenWithVec(_dbPath))
        {
            Assert.Equal(20L, ScalarLong(raw, "SELECT count(*) FROM VecMediaChunks WHERE embedding_coarse IS NOT NULL;"));
        }
    }

    [Fact]
    public async Task SearchVecRescoredAsync_RespectsOversample()
    {
        var repo = await CreateInitializedRepoAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load");

        // Every vector shares the all-positive sign pattern of the query, so every coarse code is an exact tie (Hamming
        // 0) and vec0 returns ties newest-rowid-first. The true nearest is deliberately the OLDEST row, so a small
        // oversample cannot see it while a large one can: this pins down that the oversample parameter is honoured.
        var rnd = new Random(7);
        var query = new float[768];
        Array.Fill(query, 1f);
        NormalizeInPlace(query);

        var vectors = new float[10][];
        for (int i = 0; i < vectors.Length; i++)
        {
            var v = new float[768];
            var scale = 0.4f + 0.05f * i;
            for (int d = 0; d < 768; d++) v[d] = 1f + (float)(rnd.NextDouble() - 0.5) * scale;
            vectors[i] = Normalize(v);
        }
        vectors[0] = (float[])query.Clone(); // oldest row is the exact nearest
        var chunks = await SeedVectorsAsync(repo, vectors);

        var brute = BruteForceTopIndices(query, vectors, 5);
        var small = await repo.SearchVecRescoredAsync(query, 5, oversample: 1);
        var big = await repo.SearchVecRescoredAsync(query, 5, oversample: 100);

        Assert.Equal(5, small.Count);
        Assert.Equal(5, big.Count);

        // A large oversample rescans every tie and recovers the exact brute-force top-5.
        Assert.Equal(brute, IndicesOf(big, chunks));
        // A 1x oversample only saw the newest five ties, so the oldest (true-nearest) vector cannot be returned.
        Assert.DoesNotContain(small, h => h.ChunkId == chunks[0].Id);
    }

    [Fact]
    public async Task Rescore_RecallAt10_MatchesBruteForce_OnClusteredVectors()
    {
        var repo = await CreateInitializedRepoAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load");

        // 20 well-separated, tight clusters of 50 near-duplicate unit vectors, queried near the first cluster's centre.
        // Binary quantization then keeps the coarse pass close to the exact ranking; oversample=8 asks for 80 candidates.
        const int clusters = 20, perCluster = 50;
        var (vectors, query) = ClusteredVectors(clusters, perCluster, seed: 20240517);
        var chunks = await SeedVectorsAsync(repo, vectors);

        var bruteTop = BruteForceTopIndices(query, vectors, 10).ToHashSet();
        var rescored = await repo.SearchVecRescoredAsync(query, 10, oversample: 8);
        var rescoredIndices = IndicesOf(rescored, chunks).ToHashSet();

        int hits = bruteTop.Count(i => rescoredIndices.Contains(i));
        double recall = hits / 10.0;

        Assert.True(recall >= 0.96, $"recall@10 was {recall:F2} ({hits}/10)");
    }

    // ---- helpers ----

    private static int[] IndicesOf(IReadOnlyList<(Guid ChunkId, float Distance)> hits, List<MediaChunk> chunks)
    {
        var index = new Dictionary<Guid, int>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++) index[chunks[i].Id] = i;
        return hits.Select(h => index[h.ChunkId]).OrderBy(i => i).ToArray();
    }

    private static int[] BruteForceTopIndices(float[] query, float[][] vectors, int k)
    {
        return Enumerable.Range(0, vectors.Length)
            .OrderByDescending(i => CosineSimilarity(query, vectors[i]))
            .ThenBy(i => i)
            .Take(k)
            .OrderBy(i => i)
            .ToArray();
    }

    private static (float[][] Vectors, float[] Query) ClusteredVectors(int clusters, int perCluster, int seed)
    {
        var rnd = new Random(seed);
        var all = new List<float[]>(clusters * perCluster);
        float[]? cluster0Center = null;
        for (int c = 0; c < clusters; c++)
        {
            var center = Normalize(RandomGaussian(rnd, 768));
            if (c == 0) cluster0Center = center;
            for (int p = 0; p < perCluster; p++)
            {
                // Tight clusters keep every cluster-0 member's binary code close to the centre's, so cluster 0 dominates
                // the coarse candidate pool and the exact top-10 is recovered.
                var v = new float[768];
                for (int d = 0; d < 768; d++) v[d] = center[d] + 0.05f * (float)NextGaussian(rnd);
                all.Add(Normalize(v));
            }
        }

        // The query sits just off cluster 0's centre, so the exact top-10 lies inside that one cluster.
        var q = new float[768];
        for (int d = 0; d < 768; d++) q[d] = cluster0Center![d] + 0.02f * (float)NextGaussian(rnd);
        NormalizeInPlace(q);
        return (all.ToArray(), q);
    }

    private static float[] RandomGaussian(Random rnd, int dim)
    {
        var v = new float[dim];
        for (int d = 0; d < dim; d++) v[d] = (float)NextGaussian(rnd);
        return v;
    }

    /// <summary>Box-Muller normal sample; deterministic for a given <see cref="Random"/>.</summary>
    private static double NextGaussian(Random rnd)
    {
        double u1 = 1.0 - rnd.NextDouble();
        double u2 = 1.0 - rnd.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        double denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0f : (float)(dot / denom);
    }

    private static float[] Normalize(float[] v)
    {
        var copy = (float[])v.Clone();
        NormalizeInPlace(copy);
        return copy;
    }

    private static void NormalizeInPlace(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        if (sum <= 0) return;
        float inv = (float)(1.0 / Math.Sqrt(sum));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
    }
}


