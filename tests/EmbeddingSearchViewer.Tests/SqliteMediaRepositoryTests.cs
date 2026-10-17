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
}
