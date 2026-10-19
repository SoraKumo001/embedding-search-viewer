using System;
using System.IO;
using SkiaSharp;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>Result of preparing one image for the EmbeddingGemma 2 vision encoder.</summary>
/// <param name="NumPatches">Patches that hold image data; the rest of the arrays is zero/-1 padding up to <paramref name="MaxPatches"/>.</param>
/// <param name="IsNearlyBlank">The image is (almost) a single colour; no patches were produced for it.</param>
public sealed record PreparedImage(float[] Patches, long[] PositionIds, int MaxPatches, int PatchDim, int NumSoftTokens, int NumPatches, bool IsNearlyBlank = false);

/// <summary>
/// Port of the reference Gemma4 image processing (transformers.js <c>Gemma4ImageProcessor</c>):
/// aspect-ratio-preserving resize to a multiple of (patch size x pooling kernel), rescale to [0,1],
/// split into 16x16 patches and pad to the fixed patch budget.
/// </summary>
public static class Gemma4ImagePreprocessor
{
    public const int PatchSize = 16;
    public const int PoolingKernelSize = 3;
    public const int MaxSoftTokens = 280;
    public const int Channels = 3;

    // An image whose luma varies less than this is treated as blank (a white page, a plain dark cover, ...).
    // Measured on real scans: blank pages have a deviation of 0 to 3.4 and a 1%-99% range of 0 to 11, while the
    // quietest page with real content (a dark page with text) has 10.4 and 58.
    public const double BlankStdDevMax = 5.0;
    public const double BlankRangeMax = 24.0;

    public static int MaxPatches => MaxSoftTokens * PoolingKernelSize * PoolingKernelSize; // 2520
    public static int PatchDim => PatchSize * PatchSize * Channels;                         // 768

    /// <summary>Target (height, width) so the image fits the patch budget while keeping its aspect ratio.</summary>
    public static (int Height, int Width) GetTargetSize(int height, int width, int patchSize = PatchSize, int maxPatches = -1, int poolingKernelSize = PoolingKernelSize)
    {
        if (maxPatches < 0) maxPatches = MaxPatches;
        double targetPx = (double)maxPatches * patchSize * patchSize;
        double factor = Math.Sqrt(targetPx / ((double)height * width));
        int sideMult = poolingKernelSize * patchSize;

        int targetHeight = (int)(Math.Floor(factor * height / sideMult) * sideMult);
        int targetWidth = (int)(Math.Floor(factor * width / sideMult) * sideMult);

        if (targetHeight == 0 && targetWidth == 0)
        {
            throw new InvalidOperationException($"Image of {width}x{height} cannot be resized to a non-empty multiple of {sideMult}.");
        }

        int maxSideLength = maxPatches / (poolingKernelSize * poolingKernelSize) * sideMult;
        if (targetHeight == 0)
        {
            targetHeight = sideMult;
            targetWidth = Math.Min(width / height * sideMult, maxSideLength);
        }
        else if (targetWidth == 0)
        {
            targetWidth = sideMult;
            targetHeight = Math.Min(height / width * sideMult, maxSideLength);
        }

        return (targetHeight, targetWidth);
    }

    /// <summary>
    /// Splits packed RGB (HWC, values 0-255) into patches laid out (dy, dx, channel) per patch, rescaled to [0,1],
    /// zero-padded to <paramref name="maxPatches"/>. Position ids are (column, row), padded with -1.
    /// </summary>
    public static PreparedImage Patchify(ReadOnlySpan<byte> rgb, int height, int width, int maxPatches = -1)
    {
        if (maxPatches < 0) maxPatches = MaxPatches;
        int patchesH = height / PatchSize;
        int patchesW = width / PatchSize;
        int numPatches = patchesH * patchesW;
        if (numPatches > maxPatches)
        {
            throw new ArgumentException($"{numPatches} patches exceed the budget of {maxPatches}.");
        }

        var patches = new float[maxPatches * PatchDim];
        const float scale = 1f / 255f;
        int o = 0;
        for (int ph = 0; ph < patchesH; ph++)
        {
            for (int pw = 0; pw < patchesW; pw++)
            {
                for (int dy = 0; dy < PatchSize; dy++)
                {
                    int rowOffset = ((ph * PatchSize + dy) * width + pw * PatchSize) * Channels;
                    for (int i = 0; i < PatchSize * Channels; i++)
                    {
                        patches[o++] = rgb[rowOffset + i] * scale;
                    }
                }
            }
        }

        var positions = new long[maxPatches * 2];
        Array.Fill(positions, -1L);
        int idx = 0;
        for (int row = 0; row < patchesH; row++)
        {
            for (int col = 0; col < patchesW; col++)
            {
                positions[idx++] = col;
                positions[idx++] = row;
            }
        }

        return new PreparedImage(patches, positions, maxPatches, PatchDim, numPatches / (PoolingKernelSize * PoolingKernelSize), numPatches);
    }

    /// <summary>
    /// Standard deviation and 1%-99% range of the luma (0-255) of packed RGB pixels.
    /// </summary>
    public static (double StdDev, double Range) MeasureLuma(ReadOnlySpan<byte> rgb)
    {
        long count = rgb.Length / Channels;
        if (count == 0) return (0, 0);

        var histogram = new long[256];
        double sum = 0, sumSquares = 0;
        for (int i = 0; i + 2 < rgb.Length; i += Channels)
        {
            int luma = (rgb[i] * 299 + rgb[i + 1] * 587 + rgb[i + 2] * 114 + 500) / 1000;
            histogram[luma]++;
            sum += luma;
            sumSquares += (double)luma * luma;
        }

        double mean = sum / count;
        double variance = Math.Max(0, sumSquares / count - mean * mean);

        int Percentile(double fraction)
        {
            long target = (long)Math.Ceiling(count * fraction), seen = 0;
            for (int v = 0; v < 256; v++)
            {
                seen += histogram[v];
                if (seen >= target) return v;
            }
            return 255;
        }

        return (Math.Sqrt(variance), Percentile(0.99) - Percentile(0.01));
    }

    public static bool IsBlank(double stdDev, double range) => stdDev < BlankStdDevMax && range < BlankRangeMax;

    /// <summary>
    /// True when the image file is (almost) a single colour. Measured on a small version of the image, so it is cheap;
    /// an image that cannot be decoded is reported as not blank so that the real error surfaces later.
    /// </summary>
    public static bool IsNearlyBlank(string imagePath)
    {
        try
        {
            const int budgetPatches = 144; // about 190x190 pixels: plenty to see structure, fast to compute
            var (rgb, _, _) = LoadResizedRgb(imagePath, budgetPatches);
            var (std, range) = MeasureLuma(rgb);
            return IsBlank(std, range);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Decodes an image file and prepares it for the vision encoder.</summary>
    /// <param name="detectBlank">When true, an (almost) single-colour image is reported with <see cref="PreparedImage.IsNearlyBlank"/> instead of being patchified.</param>
    public static PreparedImage Prepare(string imagePath, bool detectBlank = false)
    {
        var (rgb, targetH, targetW) = LoadResizedRgb(imagePath, MaxPatches);

        if (detectBlank)
        {
            var (std, range) = MeasureLuma(rgb);
            if (IsBlank(std, range))
            {
                return new PreparedImage(Array.Empty<float>(), Array.Empty<long>(), MaxPatches, PatchDim, 0, 0, IsNearlyBlank: true);
            }
        }

        return Patchify(rgb, targetH, targetW);
    }

    /// <summary>
    /// Returns the image as it is meant to be displayed: the EXIF orientation (a phone held sideways stores its photo
    /// rotated and only records how to turn it back) applied by rotating / mirroring the pixels. Returns
    /// <paramref name="source"/> itself when no change is needed, otherwise a new bitmap that the caller owns.
    /// </summary>
    internal static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return source;

        int w = source.Width, h = source.Height;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        // (x, y) of the stored image -> position in the displayed image, as an affine matrix
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),        // mirror horizontally
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),    // rotate 180
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),      // mirror vertically
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),          // transpose
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),        // rotate 90 clockwise
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),    // transverse
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),      // rotate 90 counter-clockwise
            _ => SKMatrix.Identity
        };

        var oriented = new SKBitmap(new SKImageInfo(swap ? h : w, swap ? w : h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(oriented);
        canvas.SetMatrix(matrix);
        // pure flips and quarter turns map pixels one to one, so no filtering is wanted
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        return oriented;
    }

    /// <summary>Decodes without premultiplying alpha, drops alpha (equivalent to PIL's convert("RGB")) and applies the EXIF orientation. The caller owns the result.</summary>
    private static SKBitmap DecodeWithSkia(SKCodec codec, string imagePath)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success || decoded.IsNull)
        {
            throw new InvalidDataException($"Could not decode image: {imagePath}");
        }

        var opaqueStored = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var src = decoded.GetPixelSpan();
        var dst = opaqueStored.GetPixelSpan();
        for (int i = 0; i < src.Length; i += 4)
        {
            dst[i] = src[i];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i + 2];
            dst[i + 3] = 255;
        }

        var oriented = Orient(opaqueStored, codec.EncodedOrigin);
        if (!ReferenceEquals(oriented, opaqueStored)) opaqueStored.Dispose();
        return oriented;
    }

    /// <summary>Decodes, drops alpha, applies the EXIF orientation, and resizes (aspect ratio kept) to at most <paramref name="maxPatches"/> patches. Returns packed RGB.</summary>
    internal static (byte[] Rgb, int Height, int Width) LoadResizedRgb(string imagePath, int maxPatches)
    {
        // NativeDecodeGate is a leaf: synchronous Skia/WIC only, no await, no other lock taken after it.
        lock (NativeDecodeGate.Lock)
        {
            using var data = SKData.Create(imagePath) ?? throw new FileNotFoundException("Image file not found.", imagePath);

            // Skia first; formats it cannot read (TIFF, ...) go through the Windows imaging codecs.
            using var codec = SKCodec.Create(data);
            var opaque = codec != null ? DecodeWithSkia(codec, imagePath) : WicImageDecoder.Decode(imagePath);
            bool ownsOpaque = true;

            var (targetH, targetW) = GetTargetSize(opaque.Height, opaque.Width, maxPatches: maxPatches);

            SKBitmap resized = opaque;
            bool ownsResized = false;
            if (targetH != opaque.Height || targetW != opaque.Width)
            {
                var target = new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Opaque);
                resized = opaque.Resize(target, new SKSamplingOptions(SKCubicResampler.CatmullRom))
                          ?? throw new InvalidOperationException("Image resize failed.");
                ownsResized = true;
            }

            try
            {
                var px = resized.GetPixelSpan();
                var rgb = new byte[targetH * targetW * Channels];
                for (int i = 0, j = 0; i < px.Length; i += 4, j += 3)
                {
                    rgb[j] = px[i];
                    rgb[j + 1] = px[i + 1];
                    rgb[j + 2] = px[i + 2];
                }
                return (rgb, targetH, targetW);
            }
            finally
            {
                if (ownsResized) resized.Dispose();
                if (ownsOpaque) opaque.Dispose();
            }
        }
    }
}
