using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using Xunit.Abstractions;

namespace EmbeddingSearchViewer.Tests.Benchmarks;

/// <summary>
/// Benchmarks the Phase 1 (flat <c>float[768]</c> KNN) and Phase 2 (binary-quantization coarse pass + exact rescore)
/// vector paths against the brute-force HybridSearchEngine scan at a realistic 120k x 768 scale, and reports p50
/// latency plus recall@10 of the index paths versus the exact scan. It is opt-in (see
/// <see cref="BenchmarkFactAttribute"/>) so the normal test run stays fast.
/// </summary>
[Collection(LargeScaleVecBenchmarkTests.CollectionName)]
public sealed class LargeScaleVecBenchmarkTests : IDisposable
{
    public const string CollectionName = "LargeScaleVecBenchmark";

    private readonly ITestOutputHelper _output;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "esv_bench_" + Guid.NewGuid().ToString("N") + ".db");

    public LargeScaleVecBenchmarkTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { }
        }
    }

    // 120k near-duplicate unit vectors in clusters whose sizes (~25-75) sit inside the coarse candidate budget
    // (k * oversample = 80), which is the regime the binary-quantization index targets (see SqliteVecTests).
    private const int VectorCount = 120_000;
    private const int Dimensions = 768;
    private const int MinClusterSize = 25;
    private const int MaxClusterSize = 75;
    private const int QueryCount = 20;
    private const int TopK = 10;
    private const int Oversample = 8;
    private const int Seed = 20261017;
    private const float MemberNoise = 0.05f;
    private const float QueryNoise = 0.02f;
    private const int InsertBatchChunks = 5_000;

    [BenchmarkFact]
    public async Task Latency_120k_768d_BruteForce_FlatKnn_Rescore()
    {
        var started = Stopwatch.StartNew();
        _output.WriteLine($"building {VectorCount:N0} x {Dimensions} clustered corpus (seed {Seed})...");
        var (vectors, queries, clusterCount) = BuildCorpus();
        _output.WriteLine($"corpus ready in {started.ElapsedMilliseconds} ms");

        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();
        Assert.True(SqliteMediaRepository.VecAvailable, "the vec0 extension did not load; check that vec0.dll is shipped under runtimes/win-x64/native");

        // ---- insert (one asset per batch keeps memory bounded; SaveAssetWithChunksAsync + triggers populate the index)
        var chunks = new List<MediaChunk>(VectorCount);
        started.Restart();
        await InsertAsync(repo, vectors, chunks);
        int backfilled = await repo.EnsureVecBackfillAsync();
        _output.WriteLine($"inserted {chunks.Count:N0} chunks in {started.Elapsed.TotalSeconds:F1} s; EnsureVecBackfillAsync added {backfilled}");

        var indexById = new Dictionary<Guid, int>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++) indexById[chunks[i].Id] = i;

        // ---- exact ground truth (equals the brute-force ordering, used as the recall reference)
        started.Restart();
        var groundTruth = new int[QueryCount][];
        for (int i = 0; i < QueryCount; i++) groundTruth[i] = ExactTopK(queries[i], vectors, TopK);
        _output.WriteLine($"exact ground truth for {QueryCount} queries in {started.Elapsed.TotalSeconds:F1} s");

        // ---- path (a): brute-force HybridSearchEngine, forced by hiding the vector index (real snapshot caching kept)
        var embeddings = new QueryEmbeddingService();
        var bruteEngine = new HybridSearchEngine(new NoVecRepository(repo), embeddings);

        // ---- warm up every path once (caches the brute-force snapshot, loads vec, primes SQLite)
        embeddings.Current = queries[0];
        await bruteEngine.SearchAsync(EngineQuery());
        await repo.SearchVecAsync(queries[0], TopK);
        await repo.SearchVecRescoredAsync(queries[0], TopK, Oversample);

        var bruteMs = new double[QueryCount];
        var flatMs = new double[QueryCount];
        var rescoreMs = new double[QueryCount];
        double bruteRecall = 0, flatRecall = 0, rescoreRecall = 0;

        for (int i = 0; i < QueryCount; i++)
        {
            var q = queries[i];
            var truth = groundTruth[i];

            embeddings.Current = q;
            var sw = Stopwatch.StartNew();
            var engineResults = await bruteEngine.SearchAsync(EngineQuery());
            sw.Stop();
            bruteMs[i] = sw.Elapsed.TotalMilliseconds;
            bruteRecall += Recall(truth, engineResults.Take(TopK).Select(r => Index(indexById, r.Chunk.Id)));

            sw.Restart();
            var flat = await repo.SearchVecAsync(q, TopK);
            sw.Stop();
            flatMs[i] = sw.Elapsed.TotalMilliseconds;
            flatRecall += Recall(truth, flat.Select(h => Index(indexById, h.ChunkId)));

            sw.Restart();
            var rescored = await repo.SearchVecRescoredAsync(q, TopK, Oversample);
            sw.Stop();
            rescoreMs[i] = sw.Elapsed.TotalMilliseconds;
            rescoreRecall += Recall(truth, rescored.Select(h => Index(indexById, h.ChunkId)));
        }

        bruteRecall /= QueryCount;
        flatRecall /= QueryCount;
        rescoreRecall /= QueryCount;

        double bruteP50 = Percentile(bruteMs, 0.50);
        double flatP50 = Percentile(flatMs, 0.50);
        double rescoreP50 = Percentile(rescoreMs, 0.50);

        var report = $"""
            === 120k x 768 vector latency benchmark ===
            corpus      : {VectorCount:N0} x {Dimensions} ({clusterCount} clusters of {MinClusterSize}-{MaxClusterSize}, seed {Seed})
            queries     : {QueryCount}, k = {TopK}, oversample = {Oversample}

            (a) brute-force engine : p50 {bruteP50,8:F2} ms   recall@10 {bruteRecall:F3}
            (b) flat vec KNN       : p50 {flatP50,8:F2} ms   recall@10 {flatRecall:F3}   speedup {bruteP50 / Math.Max(flatP50, 0.001):F1}x
            (c) rescore ovs={Oversample}     : p50 {rescoreP50,8:F2} ms   recall@10 {rescoreRecall:F3}   speedup {bruteP50 / Math.Max(rescoreP50, 0.001):F1}x
            """;

        _output.WriteLine(report);
        Console.WriteLine(report);
        var reportPath = Path.Combine(Path.GetTempPath(), "esv_benchmark_120k.txt");
        await File.WriteAllTextAsync(reportPath, report);
        _output.WriteLine($"report written to {reportPath}");

        // The index paths must reproduce the exact ordering (Phase 2 target is recall >= 0.96).
        Assert.True(flatRecall >= 0.999, $"flat KNN recall@10 was {flatRecall:F3}");
        Assert.True(rescoreRecall >= 0.96, $"rescored recall@10 was {rescoreRecall:F3}");
    }

    private SearchQuery EngineQuery() => new()
    {
        QueryText = "benchmark",
        EnableFts = false,
        MinSimilarity = -1f,
        TopK = TopK,
        TruncateDim = Dimensions
    };

    private async Task InsertAsync(SqliteMediaRepository repo, float[][] vectors, List<MediaChunk> chunks)
    {
        for (int start = 0; start < vectors.Length; start += InsertBatchChunks)
        {
            int count = Math.Min(InsertBatchChunks, vectors.Length - start);
            var asset = new MediaAsset
            {
                FilePath = $@"C:\bench\cluster_{start:D6}.bin",
                FileName = $"cluster_{start:D6}.bin",
                MediaType = MediaType.Image,
                FileSizeBytes = count * Dimensions * sizeof(float),
                LastModifiedUtc = DateTime.UtcNow
            };

            var batch = new List<MediaChunk>(count);
            for (int i = 0; i < count; i++)
            {
                batch.Add(new MediaChunk
                {
                    AssetId = asset.Id,
                    Modality = ModalityType.Image,
                    ChunkIndex = start + i,
                    Embedding = vectors[start + i]
                });
            }

            await repo.SaveAssetWithChunksAsync(asset, batch);
            chunks.AddRange(batch);
        }
    }

    /// <summary>Deterministic clustered corpus plus one query near a randomly chosen cluster centre.</summary>
    private static (float[][] Vectors, float[][] Queries, int ClusterCount) BuildCorpus()
    {
        var rnd = new Random(Seed);

        // Cluster sizes vary a little (near-duplicate groups are not all the same depth) but stay inside the candidate
        // budget so the coarse pass can return a whole cluster.
        var sizes = new List<int>();
        for (int total = 0; total < VectorCount;)
        {
            int size = MinClusterSize + rnd.Next(MaxClusterSize - MinClusterSize + 1);
            if (total + size > VectorCount) size = VectorCount - total;
            sizes.Add(size);
            total += size;
        }

        var centres = new float[sizes.Count][];
        for (int c = 0; c < centres.Length; c++) centres[c] = Normalize(RandomGaussian(rnd, Dimensions));

        var vectors = new float[VectorCount][];
        int idx = 0;
        for (int c = 0; c < sizes.Count; c++)
        {
            var centre = centres[c];
            for (int p = 0; p < sizes[c]; p++)
            {
                var v = new float[Dimensions];
                for (int d = 0; d < Dimensions; d++) v[d] = centre[d] + MemberNoise * (float)NextGaussian(rnd);
                vectors[idx++] = Normalize(v);
            }
        }

        var queries = new float[QueryCount][];
        for (int i = 0; i < QueryCount; i++)
        {
            var centre = centres[rnd.Next(centres.Length)];
            var q = new float[Dimensions];
            for (int d = 0; d < Dimensions; d++) q[d] = centre[d] + QueryNoise * (float)NextGaussian(rnd);
            queries[i] = Normalize(q);
        }

        return (vectors, queries, sizes.Count);
    }

    private static int[] ExactTopK(float[] query, float[][] vectors, int k)
    {
        return Enumerable.Range(0, vectors.Length)
            .OrderByDescending(i => Dot(query, vectors[i]))
            .ThenBy(i => i)
            .Take(k)
            .ToArray();
    }

    private static int Index(Dictionary<Guid, int> indexById, Guid id) => indexById.TryGetValue(id, out var i) ? i : -1;

    private static double Recall(int[] truth, IEnumerable<int> got)
    {
        var set = new HashSet<int>(got);
        return truth.Count(set.Contains) / (double)truth.Length;
    }

    private static double Percentile(double[] values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        double idx = (sorted.Length - 1) * p;
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
    }

    private static float Dot(float[] a, float[] b)
    {
        float sum = 0;
        for (int i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
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

    private static float[] Normalize(float[] v)
    {
        var copy = (float[])v.Clone();
        double sum = 0;
        for (int i = 0; i < copy.Length; i++) sum += (double)copy[i] * copy[i];
        if (sum <= 0) return copy;
        float inv = (float)(1.0 / Math.Sqrt(sum));
        for (int i = 0; i < copy.Length; i++) copy[i] *= inv;
        return copy;
    }

    /// <summary>Returns the current query vector, so one engine can be driven with the 20 benchmark queries.</summary>
    private sealed class QueryEmbeddingService : IEmbeddingService
    {
        public float[] Current { get; set; } = Array.Empty<float>();

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = EmbeddingModalities.Text, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult(Current);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items.Select(_ => Current).ToList());
    }

    /// <summary>
    /// Hides the vector index from the hybrid engine (both vec paths return nothing) while forwarding the real
    /// <see cref="IMediaRepository.DataVersion"/>. This forces the brute-force scan but keeps the engine's in-memory
    /// snapshot caching, so the timing isolates the cosine scan + ranking rather than reloading the database each query.
    /// </summary>
    private sealed class NoVecRepository : IMediaRepository
    {
        private readonly IMediaRepository _inner;
        public NoVecRepository(IMediaRepository inner) => _inner = inner;

        public long DataVersion => _inner.DataVersion;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => _inner.InitializeAsync(cancellationToken);
        public Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default) => _inner.GetAssetByPathAsync(filePath, cancellationToken);
        public Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default) => _inner.GetAssetByIdAsync(assetId, cancellationToken);
        public Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default) => _inner.GetAllAssetsAsync(cancellationToken);
        public Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default) => _inner.SaveAssetWithChunksAsync(asset, chunks, cancellationToken);
        public Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => _inner.DeleteAssetAsync(assetId, cancellationToken);
        public Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default) => _inner.GetAllChunksWithEmbeddingsAsync(mediaTypeFilter, cancellationToken);
        public Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default) => _inner.SearchFtsAsync(keyword, limit, cancellationToken);

        public Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecAsync(float[] query, int k, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(Guid, float)>>(Array.Empty<(Guid, float)>());

        public Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecRescoredAsync(float[] query, int k, int oversample = 8, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<(Guid, float)>>(Array.Empty<(Guid, float)>());
    }
}

/// <summary>Runs the large-scale benchmark on its own so its timings are not perturbed by the parallel test suite.</summary>
[CollectionDefinition(LargeScaleVecBenchmarkTests.CollectionName, DisableParallelization = true)]
public sealed class LargeScaleVecBenchmarkCollection
{
}
