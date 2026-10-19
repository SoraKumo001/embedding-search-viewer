using System;
using System.IO;
using SkiaSharp;
using Windows.Graphics.Imaging;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>Reads images that Skia cannot (TIFF, ...) through the Windows imaging codecs (WIC).</summary>
internal static class WicImageDecoder
{
    /// <summary>Returns the first frame as an opaque RGBA bitmap, turned upright by its EXIF orientation. The caller owns the result.</summary>
    public static SKBitmap Decode(string imagePath)
    {
        // NativeDecodeGate is a leaf: synchronous WIC/Skia only, no await, no other lock taken after it.
        lock (NativeDecodeGate.Lock)
        {
            try
            {
                using var stream = File.OpenRead(imagePath);
                var decoder = BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask().GetAwaiter().GetResult();
                var pixels = decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Rgba8, BitmapAlphaMode.Ignore, new BitmapTransform(),
                    ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();

                byte[] bytes = pixels.DetachPixelData();
                var bitmap = new SKBitmap(new SKImageInfo((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight, SKColorType.Rgba8888, SKAlphaType.Opaque));
                if (bytes.Length != bitmap.ByteCount)
                {
                    bitmap.Dispose();
                    throw new InvalidDataException($"Could not decode image: {imagePath}");
                }

                var dst = bitmap.GetPixelSpan();
                bytes.CopyTo(dst);
                for (int i = 3; i < dst.Length; i += 4) dst[i] = 255; // alpha is ignored by the decoder, so its bytes are not defined
                return bitmap;
            }
            catch (Exception ex) when (ex is not (FileNotFoundException or InvalidDataException))
            {
                // WIC reports an unknown or damaged file as a COM error; this is how the ingestors recognise a bad image.
                throw new InvalidDataException($"Unsupported or corrupt image: {imagePath}", ex);
            }
        }
    }
}
