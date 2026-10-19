using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFmpegInteropX;
using SkiaSharp;
using Windows.Storage.Streams;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer_App.Services;

/// <summary>
/// Grabs video frames with FFmpeg (FFmpegInteropX), which reads containers and codecs the Windows media stack does not
/// (webm, flv, HEVC without the codec pack, ...). The app tries it first (it measured faster than the Windows media stack
/// on the same files) and falls back to <see cref="MediaFoundationFrameSource"/> when FFmpeg cannot open a file.
/// </summary>
public sealed class FFmpegFrameSource : IVideoFrameSource
{
    private const int MaxFrameWidth = 960;
    private const int MaxFrameSkipAfterKeyFrame = 120;

    public string Name => "FFmpeg";

    public async Task<IVideoFrameSession?> OpenAsync(string videoPath, CancellationToken cancellationToken)
    {
        FrameGrabber grabber;
        try
        {
            grabber = await FrameGrabber.CreateFromFileAsync(Path.GetFullPath(videoPath));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or FileNotFoundException
                                       or UnauthorizedAccessException or InvalidOperationException)
        {
            return null; // not a video FFmpeg can read either
        }

        if (grabber.Duration <= TimeSpan.Zero)
        {
            grabber.Dispose();
            return null;
        }
        return new Session(grabber);
    }

    private sealed class Session : IVideoFrameSession
    {
        private readonly FrameGrabber _grabber;

        public Session(FrameGrabber grabber)
        {
            _grabber = grabber;
            Duration = grabber.Duration;
        }

        public TimeSpan Duration { get; }

        public async Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            using var frame = await _grabber.ExtractVideoFrameAsync(position, exactSeek: true, maxFrameSkip: MaxFrameSkipAfterKeyFrame);
            if (frame == null) return false;

            using var stream = new InMemoryRandomAccessStream();
            await frame.EncodeAsJpegAsync(stream);
            stream.Seek(0);

            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);

            SKBitmap? bitmap = null;
            SKBitmap? resized = null;
            SKData? jpeg = null;
            // NativeDecodeGate is a leaf: synchronous Skia only, no await, no other lock taken after it.
            lock (NativeDecodeGate.Lock)
            {
                bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null) return false;

                // Keep thumbnails small: the embedding model resizes anyway, and 4K frames are wasteful to store.
                var source = bitmap;
                if (bitmap.Width > MaxFrameWidth)
                {
                    int height = Math.Max(2, (int)Math.Round(bitmap.Height * (double)MaxFrameWidth / bitmap.Width));
                    resized = bitmap.Resize(new SKImageInfo(MaxFrameWidth, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                    if (resized != null) source = resized;
                }

                using var image = SKImage.FromBitmap(source);
                jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            }

            try
            {
                await File.WriteAllBytesAsync(jpegPath, jpeg.ToArray(), cancellationToken);
                return true;
            }
            finally
            {
                jpeg?.Dispose();
                resized?.Dispose();
                bitmap?.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        {
            _grabber.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
