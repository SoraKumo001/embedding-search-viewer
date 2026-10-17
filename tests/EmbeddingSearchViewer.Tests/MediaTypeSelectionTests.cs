using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class MediaTypeSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "esv_types_" + Guid.NewGuid().ToString("N"));

    public MediaTypeSelectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("a.gif", MediaType.Image)]
    [InlineData("a.ICO", MediaType.Image)]
    [InlineData("a.tif", MediaType.Image)]
    [InlineData("a.tiff", MediaType.Image)]
    [InlineData("a.m4v", MediaType.Video)]
    [InlineData("a.ts", MediaType.Video)]
    [InlineData("a.mts", MediaType.Video)]
    [InlineData("a.m2ts", MediaType.Video)]
    [InlineData("a.mpg", MediaType.Video)]
    [InlineData("a.mpeg", MediaType.Video)]
    [InlineData("a.3gp", MediaType.Video)]
    [InlineData("a.ogv", MediaType.Video)]
    [InlineData("a.docx", MediaType.Document)]
    [InlineData("a.XLSX", MediaType.Document)]
    [InlineData("a.pptx", MediaType.Document)]
    [InlineData("a.doc", MediaType.Other)]
    [InlineData("a.heic", MediaType.Other)]
    public void NewFormats_AreResolved(string path, MediaType expected)
    {
        Assert.Equal(expected, SupportedMediaTypes.ResolveMediaType(path));
    }

    [Fact]
    public void IsSelected_FollowsTheChosenTypes()
    {
        var onlyPdf = new MediaTypeSelection(Images: false, Pdfs: true, Videos: false, Documents: false);
        Assert.True(SupportedMediaTypes.IsSelected("a.pdf", onlyPdf));
        Assert.False(SupportedMediaTypes.IsSelected("a.png", onlyPdf));
        Assert.False(SupportedMediaTypes.IsSelected("a.mp4", onlyPdf));
        Assert.False(SupportedMediaTypes.IsSelected("a.txt", MediaTypeSelection.All));
    }

    private sealed class StubEmbeddingService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items.Select(_ => new float[truncateDim]).ToList());
    }

    [Fact]
    public async Task Worker_QueuesOnlyTheChosenTypes()
    {
        string media = Path.Combine(_root, "media");
        Directory.CreateDirectory(media);
        File.WriteAllBytes(Path.Combine(media, "a.png"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(media, "b.mp4"), new byte[] { 1, 2, 3 });

        var repo = new SqliteMediaRepository(Path.Combine(_root, "test.db"));
        await repo.InitializeAsync();
        var worker = new IngestionPipelineWorker(repo, new StubEmbeddingService(), Path.Combine(_root, "cache"),
            mediaTypes: () => new MediaTypeSelection(Images: false, Pdfs: true, Videos: false, Documents: false));

        await worker.QueueDirectoryAsync(media);
        await worker.QueueFileAsync(Path.Combine(media, "a.png"));
        await Task.Delay(500);

        Assert.Empty(await repo.GetAllAssetsAsync());
        Assert.False(worker.IsRunning);
    }

    [Fact]
    public async Task Tiff_IsDecodedThroughWic()
    {
        string path = Path.Combine(_root, "t.tif");
        using (var memory = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, memory);
            var pixels = new byte[4 * 3 * 4]; // 4 x 3, BGRA
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 10; pixels[i + 1] = 120; pixels[i + 2] = 240; pixels[i + 3] = 255; }
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 4, 3, 96, 96, pixels);
            await encoder.FlushAsync();
            memory.Seek(0);
            var bytes = new byte[memory.Size];
            await memory.ReadAsync(bytes.AsBuffer(), (uint)memory.Size, InputStreamOptions.None);
            File.WriteAllBytes(path, bytes);
        }

        var (rgb, height, width) = Gemma4ImagePreprocessor.LoadResizedRgb(path, Gemma4ImagePreprocessor.MaxPatches);

        Assert.True(width > 0 && height > 0);
        Assert.Equal(width * height * 3, rgb.Length);
        Assert.Equal(240, rgb[0]); // red channel of the stored colour
        Assert.Equal(10, rgb[2]);  // blue
    }

    [Fact]
    public void BrokenTiff_IsReportedAsUnsupportedImage()
    {
        string path = Path.Combine(_root, "bad.tif");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });

        Assert.Throws<InvalidDataException>(() => Gemma4ImagePreprocessor.LoadResizedRgb(path, Gemma4ImagePreprocessor.MaxPatches));
    }
}
