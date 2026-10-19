using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class SqliteMediaRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "esv_repomedia_" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task DeleteAssetsAsync_DeletesSpecifiedAssets()
    {
        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        var asset1 = new MediaAsset { FileName = "1.jpg", FilePath = @"C:\1.jpg", MediaType = MediaType.Image, LastModifiedUtc = DateTime.UtcNow };
        var asset2 = new MediaAsset { FileName = "2.jpg", FilePath = @"C:\2.jpg", MediaType = MediaType.Image, LastModifiedUtc = DateTime.UtcNow };
        var asset3 = new MediaAsset { FileName = "3.jpg", FilePath = @"C:\3.jpg", MediaType = MediaType.Image, LastModifiedUtc = DateTime.UtcNow };

        await repo.SaveAssetWithChunksAsync(asset1, Array.Empty<MediaChunk>());
        await repo.SaveAssetWithChunksAsync(asset2, Array.Empty<MediaChunk>());
        await repo.SaveAssetWithChunksAsync(asset3, Array.Empty<MediaChunk>());

        await repo.DeleteAssetsAsync(new[] { asset1.Id, asset3.Id });

        var remaining = await repo.GetAllAssetsAsync();
        var remainingAsset = Assert.Single(remaining);
        Assert.Equal(asset2.Id, remainingAsset.Id);
    }

    [Fact]
    public async Task DeleteAssetsAsync_HandlesEmptyListGracefully()
    {
        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        var asset1 = new MediaAsset { FileName = "1.jpg", FilePath = @"C:\1.jpg", MediaType = MediaType.Image, LastModifiedUtc = DateTime.UtcNow };
        await repo.SaveAssetWithChunksAsync(asset1, Array.Empty<MediaChunk>());

        await repo.DeleteAssetsAsync(Array.Empty<Guid>());

        var remaining = await repo.GetAllAssetsAsync();
        Assert.Single(remaining);
    }

    [Fact]
    public async Task DeleteAssetsAsync_ExceedingBatchLimit_DeletesAllSpecified()
    {
        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        const int count = 550;
        var assets = new List<MediaAsset>(count);
        for (int i = 0; i < count; i++)
        {
            var asset = new MediaAsset { FileName = $"{i}.jpg", FilePath = $@"C:\{i}.jpg", MediaType = MediaType.Image, LastModifiedUtc = DateTime.UtcNow };
            await repo.SaveAssetWithChunksAsync(asset, Array.Empty<MediaChunk>());
            assets.Add(asset);
        }

        Assert.Equal(count, (await repo.GetAllAssetsAsync()).Count);

        // Delete first 520 assets
        var toDelete = assets.Take(520).Select(a => a.Id).ToList();
        await repo.DeleteAssetsAsync(toDelete);

        var remaining = await repo.GetAllAssetsAsync();
        Assert.Equal(30, remaining.Count);
    }

    [Fact]
    public async Task ClearAllAsync_RemovesAllAssetsAndChunks()
    {
        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        var asset1 = new MediaAsset { FileName = "1.jpg", FilePath = @"C:\1.jpg", MediaType = MediaType.Image, LastModifiedUtc = DateTime.UtcNow };
        var chunk1 = new MediaChunk { AssetId = asset1.Id, ChunkIndex = 0, Embedding = new float[] { 0.1f, 0.2f } };
        await repo.SaveAssetWithChunksAsync(asset1, new[] { chunk1 });

        Assert.Single(await repo.GetAllAssetsAsync());
        Assert.Single(await repo.GetAllChunksWithEmbeddingsAsync());

        await repo.ClearAllAsync();

        Assert.Empty(await repo.GetAllAssetsAsync());
        Assert.Empty(await repo.GetAllChunksWithEmbeddingsAsync());
    }

    [Fact]
    public async Task GetChunksByAssetIdAsync_ReturnsOnlyThatAssetsChunks_WithEmbeddingsInChunkOrder()
    {
        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        var target = new MediaAsset { FileName = "v.mp4", FilePath = @"C:\v.mp4", MediaType = MediaType.Video, LastModifiedUtc = DateTime.UtcNow };
        var other = new MediaAsset { FileName = "o.mp4", FilePath = @"C:\o.mp4", MediaType = MediaType.Video, LastModifiedUtc = DateTime.UtcNow };

        await repo.SaveAssetWithChunksAsync(target, new[]
        {
            new MediaChunk { AssetId = target.Id, Modality = ModalityType.VideoSegment, ChunkIndex = 1, Embedding = new[] { 0f, 1f, 0f } },
            new MediaChunk { AssetId = target.Id, Modality = ModalityType.VideoSegment, ChunkIndex = 0, Embedding = new[] { 1f, 0f, 0f } },
        });
        var otherChunk = new MediaChunk { AssetId = other.Id, Modality = ModalityType.VideoSegment, ChunkIndex = 0, Embedding = new[] { 0.5f, 0.5f, 0f } };
        await repo.SaveAssetWithChunksAsync(other, new[] { otherChunk });

        var chunks = await repo.GetChunksByAssetIdAsync(target.Id);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(target.Id, c.AssetId));
        Assert.Equal(new[] { 0, 1 }, chunks.Select(c => c.ChunkIndex)); // ChunkIndex order
        Assert.Equal(new[] { 1f, 0f, 0f }, chunks[0].Embedding);        // embedding round-trips
        Assert.DoesNotContain(chunks, c => c.Id == otherChunk.Id);      // the other asset is not read
    }

    [Fact]
    public async Task SearchFtsAsync_IsContentSync_FindsBySnippetAndFileName_AndTracksWrites()
    {
        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        var asset = new MediaAsset { FileName = "report.pdf", FilePath = @"C:\docs\report.pdf", MediaType = MediaType.Pdf, LastModifiedUtc = DateTime.UtcNow };
        var chunk = new MediaChunk { AssetId = asset.Id, Modality = ModalityType.Text, ChunkIndex = 0, SnippetText = "alpha bravo content" };
        await repo.SaveAssetWithChunksAsync(asset, new[] { chunk });

        // The AFTER INSERT trigger indexed the snippet and the denormalized file name.
        Assert.Equal(chunk.Id, Assert.Single(await repo.SearchFtsAsync("alpha")).ChunkId);
        Assert.Equal(chunk.Id, Assert.Single(await repo.SearchFtsAsync("report")).ChunkId);
        Assert.Empty(await repo.SearchFtsAsync("charlie"));

        // Re-saving the same asset replaces its chunks, so the old snippet must vanish with the old chunk.
        var reChunk = new MediaChunk { AssetId = asset.Id, Modality = ModalityType.Text, ChunkIndex = 0, SnippetText = "charlie delta" };
        await repo.SaveAssetWithChunksAsync(asset, new[] { reChunk });
        Assert.Empty(await repo.SearchFtsAsync("alpha"));
        Assert.Equal(reChunk.Id, Assert.Single(await repo.SearchFtsAsync("charlie")).ChunkId);

        // Deleting the asset clears its FTS entry through the AFTER DELETE trigger.
        await repo.DeleteAssetAsync(asset.Id);
        Assert.Empty(await repo.SearchFtsAsync("charlie"));
    }

    [Fact]
    public async Task InitializeAsync_MigratesLegacyStandaloneFts_ToContentSyncIndex()
    {
        var assetId = Guid.NewGuid();
        var chunkId = Guid.NewGuid();
        var now = DateTime.UtcNow.ToString("O");

        // Build a database in the pre-P1b shape: a standalone FTS5 table with a stored ChunkId and no content= option,
        // and a MediaChunks table without the denormalized FileName column.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            const string legacySchema = """
                CREATE TABLE MediaAssets (
                    Id TEXT PRIMARY KEY, FilePath TEXT UNIQUE NOT NULL, FileName TEXT NOT NULL, MediaType TEXT NOT NULL,
                    FileSizeBytes INTEGER NOT NULL, FileHash TEXT NOT NULL, LastModifiedUtc TEXT NOT NULL,
                    ThumbnailPath TEXT, DurationOrPages INTEGER, IndexedAtUtc TEXT NOT NULL);
                CREATE TABLE MediaChunks (
                    Id TEXT PRIMARY KEY, AssetId TEXT NOT NULL, Modality TEXT NOT NULL, ChunkIndex INTEGER NOT NULL,
                    PageNumber INTEGER, StartTimeMs INTEGER, EndTimeMs INTEGER, SnippetText TEXT, PreviewImagePath TEXT,
                    Embedding BLOB, FOREIGN KEY (AssetId) REFERENCES MediaAssets(Id) ON DELETE CASCADE);
                CREATE INDEX IX_MediaChunks_AssetId ON MediaChunks(AssetId);
                CREATE INDEX IX_MediaAssets_MediaType ON MediaAssets(MediaType);
                CREATE VIRTUAL TABLE MediaChunksFts USING fts5(ChunkId UNINDEXED, FileName, SnippetText, tokenize = 'unicode61');
                """;
            await using (var schema = new SqliteCommand(legacySchema, conn))
            {
                await schema.ExecuteNonQueryAsync();
            }

            await using var seed = new SqliteCommand(
                "INSERT INTO MediaAssets VALUES (@id, @path, @name, 'Pdf', 10, 'h', @now, NULL, NULL, @now); " +
                "INSERT INTO MediaChunks (Id, AssetId, Modality, ChunkIndex, SnippetText) VALUES (@cid, @id, 'Text', 0, 'legacyalpha body'); " +
                "INSERT INTO MediaChunksFts (ChunkId, FileName, SnippetText) VALUES (@cid, 'legacy.pdf', 'legacyalpha body');", conn);
            seed.Parameters.AddWithValue("@id", assetId.ToString());
            seed.Parameters.AddWithValue("@cid", chunkId.ToString());
            seed.Parameters.AddWithValue("@path", @"C:\legacy\legacy.pdf");
            seed.Parameters.AddWithValue("@name", "legacy.pdf");
            seed.Parameters.AddWithValue("@now", now);
            await seed.ExecuteNonQueryAsync();
        }

        var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        // The rebuild re-indexed the existing content, so the same keyword still matches the same chunk.
        Assert.Equal(chunkId, Assert.Single(await repo.SearchFtsAsync("legacyalpha")).ChunkId);

        await using var check = new SqliteConnection($"Data Source={_dbPath}");
        await check.OpenAsync();
        // The migrated index is the content-sync one (no stored ChunkId) and FileName was back-filled onto the chunk.
        Assert.Equal(0L, ScalarLong(check, "SELECT count(*) FROM pragma_table_info('MediaChunksFts') WHERE name = 'ChunkId';"));
        Assert.Equal(1L, ScalarLong(check, "SELECT count(*) FROM pragma_table_info('MediaChunksFts') WHERE name = 'SnippetText';"));
        Assert.Equal("legacy.pdf", ScalarString(check, "SELECT FileName FROM MediaChunks LIMIT 1;"));

        // Writes made after the migration are kept in step by the new triggers.
        var asset2 = new MediaAsset { FileName = "fresh.pdf", FilePath = @"C:\legacy\fresh.pdf", MediaType = MediaType.Pdf, LastModifiedUtc = DateTime.UtcNow };
        var chunk2 = new MediaChunk { AssetId = asset2.Id, Modality = ModalityType.Text, ChunkIndex = 0, SnippetText = "zulu fresh" };
        await repo.SaveAssetWithChunksAsync(asset2, new[] { chunk2 });
        Assert.Equal(chunk2.Id, Assert.Single(await repo.SearchFtsAsync("zulu")).ChunkId);
    }

    [Fact]
    public async Task SaveAssetsWithChunksAsync_IsolatesAFailingAsset()
    {
        using var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        var good1 = NewAsset("good1.png");
        var chunk1 = NewChunk(good1, "alpha");
        var bad = NewAsset("bad.png");
        bad.FilePath = null!; // NOT NULL column: this one asset must fail on its own
        var good2 = NewAsset("good2.png");
        var chunk2 = NewChunk(good2, "charlie");

        var failures = await repo.SaveAssetsWithChunksAsync(new[]
        {
            new AssetChunkBatch(good1, new[] { chunk1 }),
            new AssetChunkBatch(bad, new[] { NewChunk(bad, "bravo") }),
            new AssetChunkBatch(good2, new[] { chunk2 }),
        });

        // The bad asset is reported, the batch is one transaction, and its neighbours are unaffected.
        var failure = Assert.Single(failures);
        Assert.Equal(bad.Id, failure.AssetId);
        Assert.False(string.IsNullOrEmpty(failure.Error));
        Assert.Equal(1, repo.LastBulkSaveTransactionCount);

        var stored = (await repo.GetAllAssetsAsync()).Select(a => a.FileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "good1.png", "good2.png" }, stored);

        // The good assets' chunks went through the (kept) full-text triggers, so search still finds them.
        Assert.Equal(chunk1.Id, Assert.Single(await repo.SearchFtsAsync("alpha")).ChunkId);
        Assert.Equal(chunk2.Id, Assert.Single(await repo.SearchFtsAsync("charlie")).ChunkId);
        Assert.Empty(await repo.SearchFtsAsync("bravo"));
    }

    [Fact]
    public async Task SaveAssetsWithChunksAsync_SplitsLongRunsBeyondTheAssetCap()
    {
        using var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        // 320 assets with one chunk each: the 150-asset transaction cap forces ceil(320 / 150) = 3 transactions.
        const int assetCount = 320;
        var items = new List<AssetChunkBatch>(assetCount);
        for (int i = 0; i < assetCount; i++)
        {
            var asset = NewAsset($"{i}.png");
            items.Add(new AssetChunkBatch(asset, new[] { NewChunk(asset, $"snippet {i}") }));
        }

        var failures = await repo.SaveAssetsWithChunksAsync(items);

        Assert.Empty(failures);
        Assert.Equal(3, repo.LastBulkSaveTransactionCount);
        Assert.Equal(assetCount, (await repo.GetAllAssetsAsync()).Count);
        Assert.Equal(assetCount, (await repo.GetAllChunksWithEmbeddingsAsync()).Count);
    }

    [Fact]
    public async Task SaveAssetsWithChunksAsync_SplitsLongRunsBeyondTheChunkCap()
    {
        using var repo = new SqliteMediaRepository(_dbPath);
        await repo.InitializeAsync();

        // 100 assets with 20 chunks each = 2000 chunk rows. The asset count alone would fit one transaction, but the
        // 800-row cap slices it into 40 + 40 + 20 assets.
        const int assetCount = 100;
        const int chunksEach = 20;
        var items = new List<AssetChunkBatch>(assetCount);
        for (int i = 0; i < assetCount; i++)
        {
            var asset = NewAsset($"{i}.bin");
            var chunks = Enumerable.Range(0, chunksEach).Select(c => NewChunk(asset, $"snippet {i}-{c}")).ToList();
            items.Add(new AssetChunkBatch(asset, chunks));
        }

        var failures = await repo.SaveAssetsWithChunksAsync(items);

        Assert.Empty(failures);
        Assert.Equal(3, repo.LastBulkSaveTransactionCount);
        Assert.Equal(assetCount, (await repo.GetAllAssetsAsync()).Count);
        Assert.Equal(assetCount * chunksEach, (await repo.GetAllChunksWithEmbeddingsAsync()).Count);
    }

    private static MediaAsset NewAsset(string name) => new()
    {
        Id = Guid.NewGuid(),
        FileName = name,
        FilePath = @"C:\batch\" + name,
        MediaType = MediaType.Image,
        FileHash = "1_1",
        LastModifiedUtc = DateTime.UtcNow
    };

    private static MediaChunk NewChunk(MediaAsset asset, string snippet) => new()
    {
        Id = Guid.NewGuid(),
        AssetId = asset.Id,
        Modality = ModalityType.Text,
        ChunkIndex = 0,
        SnippetText = snippet,
        Embedding = Array.Empty<float>()
    };

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static string? ScalarString(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }
}
