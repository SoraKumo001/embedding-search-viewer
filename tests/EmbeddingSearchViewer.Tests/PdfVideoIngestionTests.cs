using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using SkiaSharp;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public class VideoSegmentPlanTests
{
    private static IReadOnlyList<(int, int)> Plan(double seconds, int segment = 15, int overlap = 3) =>
        VideoSegmentExtractor.PlanSegments(TimeSpan.FromSeconds(seconds), segment, overlap);

    [Fact]
    public void Plan_CutsOverlappingSegments()
    {
        Assert.Equal(new[] { (0, 15000), (12000, 27000), (24000, 30000) }, Plan(30));
    }

    [Fact]
    public void Plan_ShortVideoIsOneSegmentOfItsOwnLength()
    {
        Assert.Equal(new[] { (0, 10000) }, Plan(10));
    }

    [Fact]
    public void Plan_StopsAtTheEndOfTheVideoWithoutAddingASegmentTheLastOneAlreadyCovers()
    {
        Assert.Equal(new[] { (0, 15000), (12000, 25500) }, Plan(25.5));
        Assert.Equal(new[] { (0, 15000), (12000, 24500) }, Plan(24.5));
    }

    [Fact]
    public void Plan_DropsATrailingSliverUnderOneSecond_ButKeepsOneOfAtLeastOneSecond()
    {
        Assert.Equal(new[] { (0, 10000), (10000, 20000) }, Plan(20.5, segment: 10, overlap: 0));
        Assert.Equal(new[] { (0, 10000), (10000, 20000), (20000, 21500) }, Plan(21.5, segment: 10, overlap: 0));
    }

    [Fact]
    public void Plan_WithoutUsableStepFallsBackToNonOverlappingSegments()
    {
        Assert.Equal(new[] { (0, 10000), (10000, 20000) }, Plan(20, segment: 10, overlap: 10));
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(-5, 15)]
    [InlineData(30, 0)]
    public void Plan_IsEmptyForNoDurationOrNoSegmentLength(double seconds, int segment)
    {
        Assert.Empty(Plan(seconds, segment));
    }
}

public sealed class VideoSegmentExtractorTests : IDisposable
{
    private sealed class FakeSession : IVideoFrameSession
    {
        private readonly Func<int, bool> _frameOk;
        private int _calls;
        public FakeSession(TimeSpan duration, Func<int, bool> frameOk) { Duration = duration; _frameOk = frameOk; }
        public TimeSpan Duration { get; }
        public List<double> RequestedSeconds { get; } = new();

        public Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            RequestedSeconds.Add(position.TotalSeconds);
            if (!_frameOk(_calls++)) return Task.FromResult(false);
            File.WriteAllBytes(jpegPath, new byte[] { 0xFF, 0xD8, 0xFF });
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSource : IVideoFrameSource
    {
        private readonly Func<IVideoFrameSession?> _open;
        public FakeSource(string name, Func<IVideoFrameSession?> open) { Name = name; _open = open; }
        public string Name { get; }
        public int Opened { get; private set; }

        public Task<IVideoFrameSession?> OpenAsync(string videoPath, CancellationToken cancellationToken)
        {
            Opened++;
            return Task.FromResult(_open());
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_vid_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Task<IReadOnlyList<VideoSegmentContent>> Run(params IVideoFrameSource[] sources) =>
        new VideoSegmentExtractor(sources).ExtractAsync(@"C:\v\clip.mp4", _dir, 15, 3);

    [Fact]
    public async Task UsesTheFirstSourceThatWorks_AndGrabsTheMiddleOfEachSegment()
    {
        var session = new FakeSession(TimeSpan.FromSeconds(30), _ => true);
        var first = new FakeSource("first", () => session);
        var second = new FakeSource("second", () => throw new InvalidOperationException("must not be used"));

        var segments = await Run(first, second);

        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.True(File.Exists(s.ThumbnailPath)));
        Assert.Equal(new[] { 7.5, 19.5, 27.0 }, session.RequestedSeconds);
        Assert.Equal(0, second.Opened);
        Assert.EndsWith("clip_seg_0001.jpg", segments[1].ThumbnailPath);
    }

    [Fact]
    public async Task FallsBackToTheNextSource_WhenTheFirstCannotOpenTheFile()
    {
        var first = new FakeSource("first", () => null);
        var second = new FakeSource("second", () => new FakeSession(TimeSpan.FromSeconds(20), _ => true));

        var segments = await Run(first, second);

        Assert.Equal(2, segments.Count);
        Assert.Equal(1, first.Opened);
        Assert.Equal(1, second.Opened);
    }

    [Fact]
    public async Task FallsBackToTheNextSource_WhenTheFirstThrowsOrOpensButProducesNoFrame()
    {
        var throwing = new FakeSource("throwing", () => throw new System.Runtime.InteropServices.COMException("unsupported codec"));
        var noFrames = new FakeSource("noFrames", () => new FakeSession(TimeSpan.FromSeconds(20), _ => false));
        var good = new FakeSource("good", () => new FakeSession(TimeSpan.FromSeconds(20), _ => true));

        var segments = await Run(throwing, noFrames, good);

        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.NotNull(s.ThumbnailPath));
        Assert.Equal(1, good.Opened);
    }

    [Fact]
    public async Task KeepsASourceThatDeliversSomeFrames_AndLeavesTheFailedOnesWithoutThumbnail()
    {
        var partial = new FakeSource("partial", () => new FakeSession(TimeSpan.FromSeconds(30), call => call != 1));
        var other = new FakeSource("other", () => throw new InvalidOperationException("must not be used"));

        var segments = await Run(partial, other);

        Assert.Equal(new bool[] { true, false, true }, segments.Select(s => s.ThumbnailPath != null));
        Assert.Equal(0, other.Opened);
    }

    [Fact]
    public async Task ReturnsNothing_WhenNoSourceCanReadTheVideo()
    {
        var segments = await Run(new FakeSource("a", () => null), new FakeSource("b", () => new FakeSession(TimeSpan.Zero, _ => true)));

        Assert.Empty(segments);
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var extractor = new VideoSegmentExtractor(new[] { new FakeSource("a", () => new FakeSession(TimeSpan.FromSeconds(30), _ => true)) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(@"C:\v\clip.mp4", _dir, 15, 3, cts.Token));
    }
}

public sealed class PdfVideoIngestorTests : IDisposable
{
    /// <summary>Returns an empty embedding (= "not embedded") for any image whose file name starts with "blank".</summary>
    private sealed class StubEmbedding : IEmbeddingService
    {
        public List<EmbeddingItemInput> Seen { get; } = new();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
        {
            var list = items.ToList();
            Seen.AddRange(list);
            return Task.FromResult<IReadOnlyList<float[]>>(list.Select(i =>
            {
                var name = Path.GetFileName(i.Path ?? i.ThumbnailPath ?? string.Empty);
                return name.StartsWith("blank") ? Array.Empty<float>() : Enumerable.Repeat(0.25f, truncateDim).ToArray();
            }).ToList());
        }
    }

    private sealed class FakePdfExtractor : IPdfPageExtractor
    {
        private readonly Func<PdfExtraction> _result;
        public FakePdfExtractor(Func<PdfExtraction> result) { _result = result; }
        public Task<PdfExtraction> ExtractAsync(string pdfPath, string outputDirectory, CancellationToken cancellationToken = default) => Task.FromResult(_result());
    }

    private sealed class FakeVideoExtractor : IVideoSegmentExtractor
    {
        private readonly IReadOnlyList<VideoSegmentContent> _segments;
        public FakeVideoExtractor(params VideoSegmentContent[] segments) { _segments = segments; }
        public Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds, CancellationToken cancellationToken = default) => Task.FromResult(_segments);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_ing_" + Guid.NewGuid().ToString("N"));

    public PdfVideoIngestorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "dummy");
        return path;
    }

    // ---- PDF ----

    [Fact]
    public async Task Pdf_CreatesAnImageChunkPerPage_AndATextChunkWhenThePageHasText()
    {
        var pdf = Touch("doc.pdf");
        var extraction = new PdfExtraction(3, new[]
        {
            new PdfPageContent(1, @"C:\c\p1.png", "This page has more than twenty characters of text."),
            new PdfPageContent(2, @"C:\c\p2.png", ""),
            new PdfPageContent(3, @"C:\c\p3.png", "short"),
        });
        var ingestor = new PdfMediaIngestor(new StubEmbedding(), new FakePdfExtractor(() => extraction));

        var (asset, chunks) = await ingestor.IngestAsync(pdf, _dir);

        Assert.Equal(MediaType.Pdf, asset.MediaType);
        Assert.Equal(3, asset.DurationOrPages);
        Assert.Equal(@"C:\c\p1.png", asset.ThumbnailPath);
        Assert.Equal(new[] { ModalityType.PageImage, ModalityType.Text, ModalityType.PageImage, ModalityType.PageImage },
            chunks.Select(c => c.Modality));
        Assert.Equal(new[] { 1, 1, 2, 3 }, chunks.Select(c => c.PageNumber!.Value));
        Assert.Equal(new[] { 0, 1, 2, 3 }, chunks.Select(c => c.ChunkIndex));
        Assert.All(chunks, c => Assert.NotEmpty(c.Embedding));
        Assert.Equal(@"C:\c\p1.png", chunks[1].PreviewImagePath);
    }

    [Fact]
    public async Task Pdf_DoesNotStoreBlankPages()
    {
        var extraction = new PdfExtraction(2, new[]
        {
            new PdfPageContent(1, @"C:\c\blank_1.png", ""),
            new PdfPageContent(2, @"C:\c\page_2.png", ""),
        });
        var ingestor = new PdfMediaIngestor(new StubEmbedding(), new FakePdfExtractor(() => extraction));

        var (_, chunks) = await ingestor.IngestAsync(Touch("doc.pdf"), _dir);

        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].PageNumber);
    }

    [Fact]
    public async Task Pdf_FallsBackToTheFileName_WhenUnreadableOrWhenEveryPageIsBlank()
    {
        var unreadable = new PdfMediaIngestor(new StubEmbedding(), new FakePdfExtractor(() => throw new InvalidDataException("password")));
        var allBlank = new PdfMediaIngestor(new StubEmbedding(), new FakePdfExtractor(() =>
            new PdfExtraction(1, new[] { new PdfPageContent(1, @"C:\c\blank.png", "") })));

        foreach (var ingestor in new[] { unreadable, allBlank })
        {
            var (_, chunks) = await ingestor.IngestAsync(Touch("doc.pdf"), _dir);

            var chunk = Assert.Single(chunks);
            Assert.Equal("doc.pdf", chunk.SnippetText);
            Assert.Equal(ModalityType.Text, chunk.Modality);
            Assert.NotEmpty(chunk.Embedding);
        }
    }

    // ---- video ----

    [Fact]
    public async Task Video_CreatesAChunkPerSegmentWithAFrame()
    {
        var extractor = new FakeVideoExtractor(
            new VideoSegmentContent(0, 0, 15000, @"C:\c\s0.jpg"),
            new VideoSegmentContent(1, 12000, 27000, null),            // no frame could be read
            new VideoSegmentContent(2, 24000, 30000, @"C:\c\s2.jpg"));
        var ingestor = new VideoMediaIngestor(new StubEmbedding(), extractor);

        var (asset, chunks) = await ingestor.IngestAsync(Touch("movie.mp4"), _dir);

        Assert.Equal(MediaType.Video, asset.MediaType);
        Assert.Equal(30000, asset.DurationOrPages);
        Assert.Equal(@"C:\c\s0.jpg", asset.ThumbnailPath);
        Assert.Equal(new[] { 0, 2 }, chunks.Select(c => c.ChunkIndex));
        Assert.Equal(new[] { 0, 24000 }, chunks.Select(c => c.StartTimeMs!.Value));
        Assert.Equal(new[] { 15000, 30000 }, chunks.Select(c => c.EndTimeMs!.Value));
        Assert.Equal("movie.mp4 (Clip 3)", chunks[1].SnippetText);
        Assert.All(chunks, c => Assert.Equal(ModalityType.VideoSegment, c.Modality));
    }

    [Fact]
    public async Task Video_DoesNotStoreBlankFrames()
    {
        var extractor = new FakeVideoExtractor(
            new VideoSegmentContent(0, 0, 15000, @"C:\c\blank_0.jpg"),   // e.g. a black intro
            new VideoSegmentContent(1, 12000, 27000, @"C:\c\s1.jpg"));
        var ingestor = new VideoMediaIngestor(new StubEmbedding(), extractor);

        var (_, chunks) = await ingestor.IngestAsync(Touch("movie.mp4"), _dir);

        Assert.Equal(new[] { 1 }, chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public async Task Video_FallsBackToTheFileName_WhenNoFrameCouldBeRead()
    {
        var ingestor = new VideoMediaIngestor(new StubEmbedding(), new FakeVideoExtractor());

        var (asset, chunks) = await ingestor.IngestAsync(Touch("movie.webm"), _dir);

        var chunk = Assert.Single(chunks);
        Assert.Equal("movie.webm", chunk.SnippetText);
        Assert.Equal(0, chunk.StartTimeMs);
        Assert.Null(asset.ThumbnailPath);
        Assert.NotEmpty(chunk.Embedding);
    }

    // ---- through the worker ----

    [Fact]
    public async Task Worker_IndexesPdfAndVideoFilesWithoutAnyPythonSidecar()
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(media, "book.pdf"), "x");
        File.WriteAllText(Path.Combine(media, "clip.mp4"), "x");
        File.WriteAllText(Path.Combine(media, "clip2.webm"), "x");

        var repo = new SqliteMediaRepository(Path.Combine(_dir, "t.db"));
        await repo.InitializeAsync();
        var worker = new IngestionPipelineWorker(
            repo, new StubEmbedding(), Path.Combine(_dir, "cache"),
            pdfExtractor: new FakePdfExtractor(() => new PdfExtraction(1, new[] { new PdfPageContent(1, @"C:\c\p1.png", "A page with enough text to be searchable.") })),
            videoExtractor: new FakeVideoExtractor(new VideoSegmentContent(0, 0, 5000, @"C:\c\s0.jpg")));
        IngestionProgress? last = null;
        worker.ProgressChanged += p => last = p;

        await worker.QueueDirectoryAsync(media);
        for (int i = 0; i < 100 && (last == null || last.IsRunning || last.ProcessedFiles < 3); i++) await Task.Delay(100);

        var assets = (await repo.GetAllAssetsAsync()).ToDictionary(a => a.FileName);
        Assert.Equal(new[] { "book.pdf", "clip.mp4", "clip2.webm" }, assets.Keys.OrderBy(x => x));
        Assert.Equal(MediaType.Pdf, assets["book.pdf"].MediaType);
        Assert.Equal(MediaType.Video, assets["clip2.webm"].MediaType);

        var chunks = await repo.GetAllChunksWithEmbeddingsAsync();
        Assert.Equal(2, chunks.Count(c => c.AssetId == assets["book.pdf"].Id));   // page image + text
        Assert.Single(chunks, c => c.AssetId == assets["clip.mp4"].Id);
        Assert.False(last!.IsRunning);
    }
}

/// <summary>The real Windows components (PDF renderer, Media Foundation); no model needed.</summary>
[Trait("Requires", "WindowsMedia")]   // CI runs "--filter Requires!=WindowsMedia": server images may lack these components
public sealed class WindowsMediaExtractionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_win_" + Guid.NewGuid().ToString("N"));

    public WindowsMediaExtractionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- PDF ----

    /// <summary>A minimal valid PDF (US Letter pages with one line of Helvetica text each).</summary>
    private static byte[] MakePdf(params string[] pageTexts)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
        var kids = new List<string>();
        int fontId = 3 + pageTexts.Length * 2;
        for (int i = 0; i < pageTexts.Length; i++)
        {
            int pageId = 3 + i * 2, contentId = pageId + 1;
            kids.Add($"{pageId} 0 R");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentId} 0 R /Resources << /Font << /F1 {fontId} 0 R >> >> >>");
            string stream = $"BT /F1 28 Tf 72 700 Td ({pageTexts[i]}) Tj ET";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pageTexts.Length} >>";

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public async Task PdfExtractor_RendersEachPageAt150Dpi_AndReadsItsText()
    {
        var pdf = Path.Combine(_dir, "sample.pdf");
        File.WriteAllBytes(pdf, MakePdf("First page about apples", "Second page about oranges"));

        var result = await new WindowsPdfPageExtractor().ExtractAsync(pdf, Path.Combine(_dir, "out"));

        Assert.Equal(2, result.TotalPages);
        Assert.Equal(new[] { 1, 2 }, result.Pages.Select(p => p.PageNumber));
        Assert.Contains("apples", result.Pages[0].Text);
        Assert.Contains("oranges", result.Pages[1].Text);
        Assert.EndsWith("page_0001.png", result.Pages[0].ImagePath);

        using var bitmap = SKBitmap.Decode(result.Pages[0].ImagePath);
        Assert.NotNull(bitmap);
        Assert.True(bitmap.Width is >= 1270 and <= 1280 && bitmap.Height is >= 1645 and <= 1655,
            $"expected about 1275x1650 (8.5 x 11 in at 150 dpi) but was {bitmap.Width}x{bitmap.Height}");
    }

    [Fact]
    public async Task PdfExtractor_PageImagesAreNotBlank_SoTheyGetEmbedded()
    {
        var pdf = Path.Combine(_dir, "sample.pdf");
        File.WriteAllBytes(pdf, MakePdf("A page that has text on it"));

        var result = await new WindowsPdfPageExtractor().ExtractAsync(pdf, Path.Combine(_dir, "out"));

        Assert.False(EmbeddingSearchViewer.Infrastructure.Onnx.Gemma4ImagePreprocessor.IsNearlyBlank(result.Pages[0].ImagePath));
    }

    [Fact]
    public async Task PdfExtractor_ReportsAnUnreadableFile()
    {
        var path = Path.Combine(_dir, "broken.pdf");
        File.WriteAllText(path, "this is not a pdf");

        await Assert.ThrowsAsync<InvalidDataException>(() => new WindowsPdfPageExtractor().ExtractAsync(path, Path.Combine(_dir, "out")));
    }

    // ---- video ----

    private static Windows.UI.Color Rgb(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(255, r, g, b);

    private async Task<string> MakeColourVideoAsync(string name, params (Windows.UI.Color Colour, double Seconds)[] parts)
    {
        var composition = new MediaComposition();
        foreach (var (colour, seconds) in parts) composition.Clips.Add(MediaClip.CreateFromColor(colour, TimeSpan.FromSeconds(seconds)));

        var path = Path.Combine(_dir, name);
        var folder = await StorageFolder.GetFolderFromPathAsync(_dir);
        var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        var failure = await composition.RenderToFileAsync(file, MediaTrimmingPreference.Fast, MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Vga));
        Assert.Equal(Windows.Media.Transcoding.TranscodeFailureReason.None, failure);
        return path;
    }

    private static (int R, int G, int B) CentrePixel(string jpegPath)
    {
        using var bitmap = SKBitmap.Decode(jpegPath);
        var c = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
        return (c.Red, c.Green, c.Blue);
    }

    [Fact]
    public async Task MediaFoundation_GrabsTheFrameInTheMiddleOfEachSegment()
    {
        var video = await MakeColourVideoAsync("rgb.mp4", (Rgb(255, 0, 0), 4), (Rgb(0, 255, 0), 4), (Rgb(0, 0, 255), 4));
        var extractor = new VideoSegmentExtractor(new IVideoFrameSource[] { new MediaFoundationFrameSource() });

        // 12 s video, 5 s segments starting every 4 s: middles at 2.5 s (red), 6.5 s (green) and 10 s (blue)
        var segments = await extractor.ExtractAsync(video, Path.Combine(_dir, "frames"), segmentSeconds: 5, overlapSeconds: 1);

        Assert.Equal(3, segments.Count);
        Assert.All(segments, s => Assert.NotNull(s.ThumbnailPath));
        var (r0, g0, b0) = CentrePixel(segments[0].ThumbnailPath!);
        var (r1, g1, b1) = CentrePixel(segments[1].ThumbnailPath!);
        var (r2, g2, b2) = CentrePixel(segments[2].ThumbnailPath!);
        Assert.True(r0 > 150 && g0 < 100 && b0 < 100, $"segment 0 should be red but was ({r0},{g0},{b0})");
        Assert.True(g1 > 150 && r1 < 100 && b1 < 100, $"segment 1 should be green but was ({r1},{g1},{b1})");
        Assert.True(b2 > 150 && r2 < 100 && g2 < 100, $"segment 2 should be blue but was ({r2},{g2},{b2})");
        Assert.Equal(new[] { 0, 4000, 8000 }, segments.Select(s => s.StartMs));
        Assert.Equal(new[] { 5000, 9000 }, segments.Take(2).Select(s => s.EndMs));
        Assert.InRange(segments[2].EndMs, 11900, 12100); // the encoder rounds the length to a frame boundary
    }

    [Fact]
    public async Task MediaFoundation_DeclinesAFileItCannotRead()
    {
        var path = Path.Combine(_dir, "not_a_video.mp4");
        File.WriteAllText(path, "definitely not a video");

        var session = await new MediaFoundationFrameSource().OpenAsync(path, CancellationToken.None);

        Assert.Null(session);
    }
}
