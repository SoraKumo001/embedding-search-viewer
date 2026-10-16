using System;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace EmbeddingSearchViewer.Tests;

public sealed class ExifOrientationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_exif_" + Guid.NewGuid().ToString("N"));

    private readonly ITestOutputHelper _output;

    public ExifOrientationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- the pixel mapping, checked for every orientation ----

    /// <summary>Where the pixel (x, y) of a w x h stored image lands in the displayed image, per EXIF orientation 1-8.</summary>
    private static (int X, int Y) Expected(int origin, int x, int y, int w, int h) => origin switch
    {
        1 => (x, y),
        2 => (w - 1 - x, y),
        3 => (w - 1 - x, h - 1 - y),
        4 => (x, h - 1 - y),
        5 => (y, x),
        6 => (h - 1 - y, x),
        7 => (h - 1 - y, w - 1 - x),
        8 => (y, w - 1 - x),
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    };

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)]
    public void Orient_MovesEveryPixelToTheExpectedPlace(int origin)
    {
        const int w = 5, h = 3;
        using var source = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                source.SetPixel(x, y, new SKColor((byte)(x * 40 + 10), (byte)(y * 80 + 10), 200)); // every pixel is unique

        using var result = Gemma4ImagePreprocessor.Orient(source, (SKEncodedOrigin)origin);

        bool swapped = origin >= 5;
        Assert.Equal(swapped ? h : w, result.Width);
        Assert.Equal(swapped ? w : h, result.Height);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (dx, dy) = Expected(origin, x, y, w, h);
                Assert.Equal(source.GetPixel(x, y), result.GetPixel(dx, dy));
            }
    }

    [Fact]
    public void Orient_WithTheNormalOrientation_ReturnsTheSameBitmapWithoutCopying()
    {
        using var source = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Opaque));

        Assert.Same(source, Gemma4ImagePreprocessor.Orient(source, SKEncodedOrigin.TopLeft));
        Assert.Same(source, Gemma4ImagePreprocessor.Orient(source, SKEncodedOrigin.Default));
    }

    // ---- real files carrying an EXIF orientation ----

    /// <summary>Inserts a minimal EXIF block that only holds the orientation tag right after the JPEG start marker.</summary>
    private static byte[] WithOrientation(byte[] jpeg, int orientation)
    {
        byte[] tiff =
        {
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,                    // big-endian TIFF header, first IFD at offset 8
            0x00, 0x01,                                                        // one entry
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00,   // Orientation, SHORT, 1 value
            0x00, 0x00, 0x00, 0x00                                             // no next IFD
        };
        var payload = new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0 }.Concat(tiff).ToArray();
        int length = payload.Length + 2;
        var segment = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) }.Concat(payload).ToArray();
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();   // after FF D8
    }

    private string SaveJpeg(string name, SKBitmap bitmap, int? orientation)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        var bytes = orientation.HasValue ? WithOrientation(data.ToArray(), orientation.Value) : data.ToArray();
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>A landscape picture: left half red, right half blue.</summary>
    private static SKBitmap RedLeftBlueRight(int w = 160, int h = 80)
    {
        var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Blue);
        using var paint = new SKPaint { Color = SKColors.Red };
        canvas.DrawRect(0, 0, w / 2f, h, paint);
        return bmp;
    }

    private static (byte R, byte G, byte B) Pixel(byte[] rgb, int width, int x, int y)
    {
        int i = (y * width + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    [Fact]
    public void ALandscapeStoredFileMarkedAsRotated_IsLoadedUpright()
    {
        // Orientation 6: the file is stored as a landscape picture but is meant to be shown turned 90 degrees clockwise.
        using var bmp = RedLeftBlueRight();
        var path = SaveJpeg("rotated.jpg", bmp, orientation: 6);

        var (rgb, height, width) = Gemma4ImagePreprocessor.LoadResizedRgb(path, maxPatches: 144);

        Assert.True(height > width, $"expected a portrait picture but got {width}x{height}");
        var top = Pixel(rgb, width, width / 2, height / 4);          // the red left half has become the top half
        var bottom = Pixel(rgb, width, width / 2, height * 3 / 4);
        Assert.True(top.R > 200 && top.B < 60, $"top should be red but was {top}");
        Assert.True(bottom.B > 200 && bottom.R < 60, $"bottom should be blue but was {bottom}");
    }

    [Fact]
    public void WithoutAnOrientationTag_TheFileIsLoadedAsStored()
    {
        using var bmp = RedLeftBlueRight();
        var path = SaveJpeg("plain.jpg", bmp, orientation: null);

        var (rgb, height, width) = Gemma4ImagePreprocessor.LoadResizedRgb(path, maxPatches: 144);

        Assert.True(width > height);
        var left = Pixel(rgb, width, width / 4, height / 2);
        var right = Pixel(rgb, width, width * 3 / 4, height / 2);
        Assert.True(left.R > 200 && left.B < 60, $"left should be red but was {left}");
        Assert.True(right.B > 200 && right.R < 60, $"right should be blue but was {right}");
    }

    // ---- through the model ----

    [FactIfModelInstalled]
    public async Task APhotoStoredSidewaysWithATag_GetsTheSameEmbeddingAsTheUprightPhoto()
    {
        using var upright = new SKBitmap(240, 320);   // portrait picture with a recognisable layout
        using (var canvas = new SKCanvas(upright))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { Color = SKColors.Crimson, IsAntialias = true };
            canvas.DrawRect(0, 0, 240, 90, paint);                   // red band on top
            paint.Color = SKColors.RoyalBlue;
            canvas.DrawCircle(120, 220, 70, paint);                  // blue disc below
        }

        // What a phone writes for a portrait shot taken sideways: pixels rotated 90 degrees counter-clockwise + tag 6.
        using var sideways = new SKBitmap(320, 240);
        using (var canvas = new SKCanvas(sideways))
        {
            canvas.Translate(0, 240);
            canvas.RotateDegrees(-90);
            canvas.DrawBitmap(upright, 0, 0);
        }
        var uprightPath = SaveJpeg("upright.jpg", upright, orientation: null);
        var taggedPath = SaveJpeg("sideways_tagged.jpg", sideways, orientation: 6);
        var untaggedPath = SaveJpeg("sideways_untagged.jpg", sideways, orientation: null);

        using var service = new OnnxDirectMlEmbeddingService();
        var a = await service.GenerateQueryEmbeddingAsync(uprightPath, "image");
        var b = await service.GenerateQueryEmbeddingAsync(taggedPath, "image");
        var c = await service.GenerateQueryEmbeddingAsync(untaggedPath, "image");

        float withTag = TensorPrimitives.CosineSimilarity(a, b);
        float withoutTag = TensorPrimitives.CosineSimilarity(a, c);
        _output.WriteLine($"upright vs tagged sideways: {withTag:F4}; upright vs untagged sideways: {withoutTag:F4}");
        Assert.True(withTag > 0.995f, $"tagged sideways photo should match the upright one but similarity was {withTag}");
        Assert.True(withTag > withoutTag, $"honouring the tag must help: {withTag} vs {withoutTag}");
    }
}
