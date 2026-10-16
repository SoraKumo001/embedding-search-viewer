using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using SkiaSharp;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>Runs only when the EmbeddingGemma 2 ONNX files are installed on this machine.</summary>
public sealed class FactIfModelInstalledAttribute : FactAttribute
{
    public FactIfModelInstalledAttribute()
    {
        if (EmbeddingModelLocation.Find() == null)
        {
            Skip = "EmbeddingGemma 2 model files are not installed.";
        }
    }
}

public class Gemma4ImagePreprocessorTests
{
    [Theory]
    [InlineData(1000, 1000, 768, 768)]     // square image fills the budget: 48x48 patches
    [InlineData(2000, 1000, 1104, 528)]    // portrait keeps its aspect ratio (multiples of 48)
    [InlineData(1000, 2000, 528, 1104)]    // landscape
    [InlineData(64, 64, 768, 768)]         // small images are scaled up to the budget
    public void GetTargetSize_PreservesAspectRatioInMultiplesOf48(int h, int w, int expectedH, int expectedW)
    {
        var (th, tw) = Gemma4ImagePreprocessor.GetTargetSize(h, w);

        Assert.Equal((expectedH, expectedW), (th, tw));
        Assert.Equal(0, th % 48);
        Assert.Equal(0, tw % 48);
        Assert.True((th / 16) * (tw / 16) <= Gemma4ImagePreprocessor.MaxPatches);
    }

    [Fact]
    public void GetTargetSize_ExtremeAspectRatioStillProducesAtLeastOneBlock()
    {
        var (th, tw) = Gemma4ImagePreprocessor.GetTargetSize(10, 4000);

        Assert.True(th >= 48 && tw >= 48);
        Assert.True((th / 16) * (tw / 16) <= Gemma4ImagePreprocessor.MaxPatches);
    }

    [Fact]
    public void Patchify_LaysPixelsOutPerPatchAsRowColumnChannel_AndPadsWithMinusOne()
    {
        // 32x32 image = 2x2 patches. Pixel value encodes its position: R=x, G=y, B=7.
        const int size = 32;
        var rgb = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 3;
                rgb[i] = (byte)x; rgb[i + 1] = (byte)y; rgb[i + 2] = 7;
            }

        var p = Gemma4ImagePreprocessor.Patchify(rgb, size, size, maxPatches: 6);

        Assert.Equal(6 * 768, p.Patches.Length);
        const float s = 1f / 255f;
        // patch 0 = top-left; its pixel (dy=1, dx=2) sits at offset (1*16 + 2) * 3
        int o = (1 * 16 + 2) * 3;
        Assert.Equal(2 * s, p.Patches[o], 6);
        Assert.Equal(1 * s, p.Patches[o + 1], 6);
        Assert.Equal(7 * s, p.Patches[o + 2], 6);
        // patch 1 = top-right (starts at x=16)
        Assert.Equal(16 * s, p.Patches[768], 6);
        // patch 2 = bottom-left (starts at y=16)
        Assert.Equal(16 * s, p.Patches[2 * 768 + 1], 6);

        Assert.Equal(new long[] { 0, 0, 1, 0, 0, 1, 1, 1, -1, -1, -1, -1 }, p.PositionIds);
        Assert.All(p.Patches.Skip(4 * 768), v => Assert.Equal(0f, v));
        Assert.Equal(0, p.NumSoftTokens); // 4 patches / 9
    }

    [Fact]
    public void Patchify_CountsSoftTokensAsPatchesDividedByNine()
    {
        var rgb = new byte[48 * 96 * 3];

        var p = Gemma4ImagePreprocessor.Patchify(rgb, 48, 96);

        Assert.Equal(2, p.NumSoftTokens); // 3 x 6 = 18 patches, pooled 3x3 -> 18 / 9 tokens
    }
}

public class OnnxDirectMlEmbeddingServiceTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "esv_img_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string SolidImage(string name, SKColor color, int w = 320, int h = 240)
    {
        Directory.CreateDirectory(_tmp);
        var path = Path.Combine(_tmp, name);
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(color);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>An image with real structure (bands and a circle), so it is not treated as blank.</summary>
    private string PatternImage(string name)
    {
        Directory.CreateDirectory(_tmp);
        var path = Path.Combine(_tmp, name);
        using var bmp = new SKBitmap(320, 240);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Crimson, IsAntialias = true };
            canvas.DrawRect(0, 0, 320, 80, paint);
            paint.Color = SKColors.RoyalBlue;
            canvas.DrawCircle(160, 150, 60, paint);
        }
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static void AssertNormalized(float[] v)
    {
        Assert.InRange(MathF.Sqrt(TensorPrimitives.SumOfSquares(v)), 0.98f, 1.02f);
    }

    [FactIfModelInstalled]
    public async Task Text_GeneratesNormalized768Vector_AndSupportsMatryoshkaTruncation()
    {
        using var service = new OnnxDirectMlEmbeddingService();

        Assert.True(await service.IsReadyAsync());
        var full = await service.GenerateQueryEmbeddingAsync("家", "text", 768);
        var small = await service.GenerateQueryEmbeddingAsync("赤い車", "text", 256);

        Assert.Equal(768, full.Length);
        Assert.Equal(256, small.Length);
        AssertNormalized(full);
        AssertNormalized(small);
    }

    [FactIfModelInstalled]
    public async Task Text_RelatedSentencesAreCloserThanUnrelated()
    {
        using var service = new OnnxDirectMlEmbeddingService();

        var house1 = await service.GenerateQueryEmbeddingAsync("住宅の設計図");
        var house2 = await service.GenerateQueryEmbeddingAsync("家の間取り");
        var space = await service.GenerateQueryEmbeddingAsync("宇宙ステーションの軌道");

        float related = TensorPrimitives.CosineSimilarity(house1, house2);
        float unrelated = TensorPrimitives.CosineSimilarity(house1, space);
        Assert.True(related > unrelated, $"related {related} should exceed unrelated {unrelated}");
    }

    [FactIfModelInstalled]
    public async Task Image_EmbeddingIsNormalized_AndMatchesItsColourInTextQueries()
    {
        using var service = new OnnxDirectMlEmbeddingService();
        var red = SolidImage("red.png", SKColors.Red);
        var blue = SolidImage("blue.png", SKColors.Blue);

        var redVec = await service.GenerateQueryEmbeddingAsync(red, "image");
        var blueVec = await service.GenerateQueryEmbeddingAsync(blue, "image");
        var redQuery = await service.GenerateQueryEmbeddingAsync("a solid red image");
        var blueQuery = await service.GenerateQueryEmbeddingAsync("a solid blue image");

        Assert.Equal(768, redVec.Length);
        AssertNormalized(redVec);
        AssertNormalized(blueVec);
        Assert.True(TensorPrimitives.CosineSimilarity(redVec, redQuery) > TensorPrimitives.CosineSimilarity(blueVec, redQuery),
            "the red image should match the 'red' query better than the blue image does");
        Assert.True(TensorPrimitives.CosineSimilarity(blueVec, blueQuery) > TensorPrimitives.CosineSimilarity(redVec, blueQuery),
            "the blue image should match the 'blue' query better than the red image does");
    }

    [FactIfModelInstalled]
    public async Task Image_EmbeddingDoesNotDependOnTheFileName()
    {
        using var service = new OnnxDirectMlEmbeddingService();
        var a = SolidImage("cat.png", SKColors.Green);
        var b = SolidImage("totally_different_name.png", SKColors.Green);

        var va = await service.GenerateQueryEmbeddingAsync(a, "image");
        var vb = await service.GenerateQueryEmbeddingAsync(b, "image");

        Assert.True(TensorPrimitives.CosineSimilarity(va, vb) > 0.999f);
    }

    [FactIfModelInstalled]
    public async Task Batch_MixesTextAndImagesAndKeepsInputOrder()
    {
        using var service = new OnnxDirectMlEmbeddingService();
        var red = PatternImage("pattern.png");

        var results = await service.GenerateBatchEmbeddingsAsync(new[]
        {
            new EmbeddingItemInput(Type: "text", Title: "ドキュメント1", Text: "人工知能と機械学習の最新動向"),
            new EmbeddingItemInput(Type: "image", Path: red),
            new EmbeddingItemInput(Type: "text", Title: "ドキュメント3", Text: "美味しいカレーライスのレシピ"),
            new EmbeddingItemInput(Type: "video_segment", ThumbnailPath: red),
        });

        Assert.Equal(4, results.Count);
        Assert.All(results, r => { Assert.Equal(768, r.Length); AssertNormalized(r); });
        // the two red images (items 1 and 3) are identical; the texts are not
        Assert.True(TensorPrimitives.CosineSimilarity(results[1], results[3]) > 0.999f);
        Assert.True(TensorPrimitives.CosineSimilarity(results[0], results[2]) < 0.99f);
    }

    [FactIfModelInstalled]
    public async Task Image_WithAlphaAndOddSize_IsAccepted()
    {
        using var service = new OnnxDirectMlEmbeddingService();
        var path = SolidImage("odd.png", new SKColor(10, 200, 30, 128), w: 123, h: 457);

        var vec = await service.GenerateQueryEmbeddingAsync(path, "image");

        Assert.Equal(768, vec.Length);
        AssertNormalized(vec);
    }

    [FactIfModelInstalled]
    public async Task ConcurrentCallsFromManyThreads_AllSucceed()
    {
        using var service = new OnnxDirectMlEmbeddingService();
        var red = SolidImage("red.png", SKColors.Red);

        var tasks = Enumerable.Range(0, 24).Select(i => i % 4 == 0
            ? service.GenerateQueryEmbeddingAsync(red, "image")
            : service.GenerateQueryEmbeddingAsync($"query number {i}"));
        var all = await Task.WhenAll(tasks);

        Assert.All(all, v => { Assert.Equal(768, v.Length); AssertNormalized(v); });
        Assert.True(service.IsVisionOnGpu, "concurrent use must not push the vision encoder off the GPU");
        Assert.True(service.IsLanguageModelOnGpu, "concurrent use must not push the image language model off the GPU");
    }

    [FactIfModelInstalled]
    public async Task CpuOnly_AndGpuVision_ProduceTheSameImageEmbedding()
    {
        var path = SolidImage("mix.png", new SKColor(200, 80, 40));
        using var cpu = new OnnxDirectMlEmbeddingService(deviceId: -1);
        using var gpu = new OnnxDirectMlEmbeddingService(deviceId: 0);

        var a = await cpu.GenerateQueryEmbeddingAsync(path, "image");
        var b = await gpu.GenerateQueryEmbeddingAsync(path, "image");

        Assert.False(cpu.IsVisionOnGpu);
        Assert.True(TensorPrimitives.CosineSimilarity(a, b) > 0.999f);
    }

    [Fact]
    public void PlanTextRuns_KeepsEveryRunWithinTheBudget_AndCoversEachSequenceOnce()
    {
        var lengths = new[] { 900, 12, 40, 1024, 7, 300, 300, 301, 64, 64, 64, 64, 1000, 20 };

        var runs = OnnxDirectMlEmbeddingService.PlanTextRuns(lengths, maxTokensPerRun: 2048, maxSequencesPerRun: 4);

        Assert.Equal(Enumerable.Range(0, lengths.Length), runs.SelectMany(r => r).OrderBy(i => i));
        foreach (var run in runs)
        {
            Assert.InRange(run.Length, 1, 4);
            Assert.True(run.Length * run.Max(i => lengths[i]) <= 2048 || run.Length == 1, "padded size must stay within the budget");
        }
    }

    [Fact]
    public void PlanTextRuns_PutsSimilarLengthsTogether()
    {
        var runs = OnnxDirectMlEmbeddingService.PlanTextRuns(new[] { 10, 1000, 12, 990, 11, 1010 }, maxTokensPerRun: 4096, maxSequencesPerRun: 3);

        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { 0, 2, 4 }, runs[0].OrderBy(i => i)); // the three short ones
    }

    [Fact]
    public void PlanTextRuns_OfNothing_IsEmpty()
    {
        Assert.Empty(OnnxDirectMlEmbeddingService.PlanTextRuns(Array.Empty<int>()));
    }

    [FactIfModelInstalled]
    public async Task LargeTextBatch_DoesNotRunOutOfMemory_AndMatchesTheIndividualResults()
    {
        using var service = new OnnxDirectMlEmbeddingService();
        // 96 documents of very different lengths, as a long PDF would produce (several runs of the language model)
        var items = Enumerable.Range(0, 96).Select(i => new EmbeddingItemInput(
            Type: "text", Title: "book.pdf", Text: string.Join(" ", Enumerable.Repeat($"page {i} sentence", 1 + i * 7 % 90)))).ToList();

        var batch = await service.GenerateBatchEmbeddingsAsync(items);
        var single = await service.GenerateBatchEmbeddingsAsync(new[] { items[17], items[83] });

        Assert.Equal(96, batch.Count);
        Assert.All(batch, v => Assert.Equal(768, v.Length));
        Assert.True(TensorPrimitives.CosineSimilarity(batch[17], single[0]) > 0.999f);
        Assert.True(TensorPrimitives.CosineSimilarity(batch[83], single[1]) > 0.999f);
    }

    [Theory]
    [InlineData(1, 64, 32)]
    [InlineData(64, 64, 32)]
    [InlineData(65, 128, 32)]
    [InlineData(128, 128, 32)]
    [InlineData(200, 256, 16)]
    [InlineData(512, 512, 8)]
    [InlineData(513, 1024, 4)]
    [InlineData(1024, 1024, 4)]
    [InlineData(5000, 1024, 4)]   // never produced (texts are capped at 1024 tokens), but must not throw
    public void BucketFor_PicksTheSmallestFixedShapeThatFits(int tokens, int expectedLength, int expectedRows)
    {
        Assert.Equal((expectedLength, expectedRows), OnnxDirectMlEmbeddingService.BucketFor(tokens));
    }

    [Fact]
    public void EveryBucketStaysWithinTheTokenBudgetOfTheCpuRuns()
    {
        foreach (var length in new[] { 1, 64, 100, 128, 256, 400, 512, 1000, 1024 })
        {
            var (paddedLength, rows) = OnnxDirectMlEmbeddingService.BucketFor(length);
            Assert.True(paddedLength * rows <= OnnxDirectMlEmbeddingService.MaxTokensPerTextRun, $"{paddedLength} x {rows}");
        }
    }

    [FactIfModelInstalled]
    public async Task DocumentBatchesOnTheGpu_MatchTheCpuResults_ForAllLengths()
    {
        using var gpu = new OnnxDirectMlEmbeddingService(deviceId: 0);
        using var cpu = new OnnxDirectMlEmbeddingService(deviceId: -1);
        // short titles up to long pages, so that every bucket is used and the last run of a bucket is a partial one
        var texts = Enumerable.Range(0, 70).Select(i =>
            $"title: doc{i} | text: " + string.Join(" ", Enumerable.Repeat($"sentence {i} about topic {i % 7}", 1 + i * 13 % 160))).ToList();

        var onGpu = gpu.EncodeTextBatch(texts, useGpu: true);
        var onCpu = cpu.EncodeTextBatch(texts);

        Assert.True(gpu.IsLanguageModelOnGpu, "the batch should have run on the GPU");
        Assert.Equal(texts.Count, onGpu.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            Assert.Equal(768, onGpu[i].Length);
            Assert.True(TensorPrimitives.CosineSimilarity(onGpu[i], onCpu[i]) > 0.999f, $"text {i} differs between GPU and CPU");
        }
        await Task.CompletedTask;
    }
}
