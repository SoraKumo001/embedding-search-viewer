using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using SkiaSharp;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class BlankImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_blank_" + Guid.NewGuid().ToString("N"));

    public BlankImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- image factories ----

    private string Save(string name, int w, int h, Action<SKCanvas, SKBitmap> draw)
    {
        var path = Path.Combine(_dir, name);
        using var bmp = new SKBitmap(w, h);
        using (var canvas = new SKCanvas(bmp)) draw(canvas, bmp);
        using var data = SKImage.FromBitmap(bmp).Encode(name.EndsWith(".jpg") ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 92);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private string White(string name = "white.png") => Save(name, 600, 800, (c, _) => c.Clear(SKColors.White));

    /// <summary>A scanned dark page: mean ~50 with a little paper noise (std about 3), like the "23.jpg" of the test library.</summary>
    private string NoisyDark(string name = "dark.png") => Save(name, 600, 800, (_, bmp) =>
    {
        var rnd = new Random(7);
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                int v = Math.Clamp(50 + (int)Math.Round((rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5) * 4.0), 0, 255);
                bmp.SetPixel(x, y, new SKColor((byte)v, (byte)v, (byte)v));
            }
    });

    /// <summary>A mostly white page with a few lines of text-like strokes.</summary>
    private string TextPage(string name = "text.png") => Save(name, 600, 800, (c, _) =>
    {
        c.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 5, IsAntialias = true };
        for (int line = 0; line < 14; line++)
            c.DrawLine(60, 80 + line * 45, 540 - (line % 3) * 90, 80 + line * 45, paint);
    });

    // ---- measurement ----

    [Fact]
    public void MeasureLuma_OfAPlainColour_HasNoVariation()
    {
        var rgb = Enumerable.Repeat((byte)255, 3 * 1000).ToArray();

        var (std, range) = Gemma4ImagePreprocessor.MeasureLuma(rgb);

        Assert.Equal(0, std, 6);
        Assert.Equal(0, range);
        Assert.True(Gemma4ImagePreprocessor.IsBlank(std, range));
    }

    [Fact]
    public void MeasureLuma_OfBlackAndWhiteHalves_IsLargeAndNotBlank()
    {
        var rgb = new byte[3 * 1000];
        for (int i = 0; i < 500 * 3; i++) rgb[i] = 255;

        var (std, range) = Gemma4ImagePreprocessor.MeasureLuma(rgb);

        Assert.InRange(std, 120, 130);
        Assert.Equal(255, range);
        Assert.False(Gemma4ImagePreprocessor.IsBlank(std, range));
    }

    [Theory]
    [InlineData(3.4, 11, true)]     // the quietest blank page found in the test library
    [InlineData(0.0, 0, true)]      // pure white
    [InlineData(10.4, 58, false)]   // the quietest page with real content (a dark page of text)
    [InlineData(4.9, 30, false)]    // low deviation but a wide range: something is on the page
    [InlineData(6.0, 10, false)]    // narrow range but noticeable deviation
    public void IsBlank_SeparatesBlankPagesFromQuietContent(double std, double range, bool expected)
    {
        Assert.Equal(expected, Gemma4ImagePreprocessor.IsBlank(std, range));
    }

    // ---- files ----

    [Fact]
    public void IsNearlyBlank_RecognisesWhiteAndNoisyDarkPages_ButNotPagesWithContent()
    {
        Assert.True(Gemma4ImagePreprocessor.IsNearlyBlank(White()));
        Assert.True(Gemma4ImagePreprocessor.IsNearlyBlank(NoisyDark()));
        Assert.False(Gemma4ImagePreprocessor.IsNearlyBlank(TextPage()));
    }

    [Fact]
    public void IsNearlyBlank_TreatsAnUnreadableFileAsNotBlank()
    {
        var path = Path.Combine(_dir, "broken.jpg");
        File.WriteAllText(path, "not an image");

        Assert.False(Gemma4ImagePreprocessor.IsNearlyBlank(path));
        Assert.False(Gemma4ImagePreprocessor.IsNearlyBlank(Path.Combine(_dir, "missing.png")));
    }

    [Fact]
    public void Prepare_ReportsBlankImagesWithoutPatches_OnlyWhenAsked()
    {
        var white = White();

        var detected = Gemma4ImagePreprocessor.Prepare(white, detectBlank: true);
        var plain = Gemma4ImagePreprocessor.Prepare(white);

        Assert.True(detected.IsNearlyBlank);
        Assert.Empty(detected.Patches);
        Assert.False(plain.IsNearlyBlank);
        Assert.True(plain.NumPatches > 0);
    }

    // ---- ingestion ----

    private sealed class BlankAwareEmbedding : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items
                .Select(i => Path.GetFileName(i.Path!).StartsWith("blank") ? Array.Empty<float>() : Enumerable.Repeat(0.5f, truncateDim).ToArray())
                .ToList());
    }

    [Fact]
    public async Task Ingestor_DoesNotStoreImagesWithoutEmbedding_AndReportsThem()
    {
        var ok = White("page1.png");
        var blank = White("blank_page.png");
        var ingestor = new ImageMediaIngestor(new BlankAwareEmbedding());

        var result = await ingestor.IngestBatchWithSkipsAsync(new[] { ok, blank }, _dir);

        Assert.Equal(new[] { "page1.png" }, result.Items.Select(i => i.Asset.FileName));
        Assert.Equal(new[] { blank }, result.SkippedWithoutEmbedding);
        Assert.All(result.Items, i => Assert.All(i.Chunks, c => Assert.NotEmpty(c.Embedding)));
    }

    [Fact]
    public async Task Worker_SkipsBlankFiles_ButStillReportsCompletionAndTheSkippedCount()
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        File.Copy(White("a.png"), Path.Combine(media, "a.png"));
        File.Copy(White("b.png"), Path.Combine(media, "b.png"));
        File.Copy(White("blank1.png"), Path.Combine(media, "blank1.png"));
        File.Copy(White("blank2.png"), Path.Combine(media, "blank2.png"));

        var repo = new SqliteMediaRepository(Path.Combine(_dir, "t.db"));
        await repo.InitializeAsync();
        var worker = new IngestionPipelineWorker(repo, new BlankAwareEmbedding(), Path.Combine(_dir, "cache"));
        IngestionProgress? last = null;
        worker.ProgressChanged += p => last = p;

        await worker.QueueDirectoryAsync(media);
        for (int i = 0; i < 100 && (last == null || last.IsRunning || last.ProcessedFiles < 4); i++) await Task.Delay(100);

        var stored = (await repo.GetAllAssetsAsync()).Select(a => a.FileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "a.png", "b.png" }, stored);
        Assert.NotNull(last);
        Assert.False(last!.IsRunning);
        Assert.Equal(4, last.TotalFiles);
        Assert.Equal(4, last.ProcessedFiles);
        Assert.Equal(2, last.SkippedFiles);
    }

    // ---- cleaning an existing index ----

    [Fact]
    public async Task Cleanup_FindsOnlyBlankImagesThatStillExist()
    {
        var assets = new[]
        {
            Asset(White("w.png"), MediaType.Image),
            Asset(NoisyDark("d.png"), MediaType.Image),
            Asset(TextPage("t.png"), MediaType.Image),
            Asset(Path.Combine(_dir, "gone.png"), MediaType.Image),   // file no longer exists
            Asset(White("doc.pdf"), MediaType.Pdf),                    // not an image asset
        };

        var blank = await BlankImageCleanup.FindBlankImagesAsync(assets);

        Assert.Equal(new[] { "d.png", "w.png" }, blank.Select(a => a.FileName).OrderBy(x => x));
    }

    private static MediaAsset Asset(string path, MediaType type) => new() { FilePath = path, FileName = Path.GetFileName(path), MediaType = type };

    // ---- the real model (only when installed) ----

    [FactIfModelInstalled]
    public async Task Service_ReturnsNoEmbeddingForBlankImages_InBatches_ButEmbedsThemForQueries()
    {
        using var service = new OnnxDirectMlEmbeddingService();
        var white = White();
        var text = TextPage();

        var batch = await service.GenerateBatchEmbeddingsAsync(new[]
        {
            new EmbeddingItemInput(Type: "image", Path: white),
            new EmbeddingItemInput(Type: "image", Path: text),
        });
        var asQuery = await service.GenerateQueryEmbeddingAsync(white, "image");

        Assert.Empty(batch[0]);
        Assert.Equal(768, batch[1].Length);
        Assert.Equal(768, asQuery.Length); // a blank image used as a search query is still embedded
    }
}
