using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Library;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class AssetStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv-stats-" + Guid.NewGuid().ToString("N"));

    public AssetStatsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Repository_CountsIndexedFilesByType()
    {
        var repo = new SqliteMediaRepository(Path.Combine(_dir, "t.db"));
        await repo.InitializeAsync();
        foreach (var (name, type) in new[] { ("a.png", MediaType.Image), ("b.png", MediaType.Image), ("c.pdf", MediaType.Pdf), ("d.mp4", MediaType.Video), ("e.docx", MediaType.Document), ("f.xlsx", MediaType.Document) })
        {
            await repo.SaveAssetWithChunksAsync(new MediaAsset { Id = Guid.NewGuid(), FilePath = Path.Combine(_dir, name), FileName = name, MediaType = type, FileHash = "1_1" }, Array.Empty<MediaChunk>());
        }

        var stats = await repo.GetAssetStatsAsync();

        Assert.Equal(new AssetStats(6, 2, 1, 1, 2), stats);
    }

    [Fact]
    public void AtomicJsonFile_ReportsAFailedSave()
    {
        string blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        (string, Exception)? reported = null;
        AtomicJsonFile.SaveFailed = (p, e) => reported = (p, e);
        try
        {
            bool saved = AtomicJsonFile.Save(Path.Combine(blocker, "sub", "x.json"), new { A = 1 });

            Assert.False(saved);
            Assert.NotNull(reported);
        }
        finally { AtomicJsonFile.SaveFailed = null; }
    }
}
