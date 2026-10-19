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
        // Creating (opening the container) and discarding a grabber drives FFmpeg's native objects, so it is serialised
        // with frame decode/encode through the same process-wide video gate (0xC0000374).
        await NativeDecodeGate.VideoGate.WaitAsync(cancellationToken);
        try
        {
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
        }
        finally
        {
            NativeDecodeGate.VideoGate.Release();
        }
        return new Session(grabber, Path.GetFullPath(videoPath));
    }

    private sealed class Session : IVideoFrameSession
    {
        // Measured extraction is well under a second, so 120s is a generous ceiling that only a decoder which never
        // returns can hit; abandoning the work keeps VideoGate (and the whole ingest) from stalling forever.
        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

        // After this many hangs in a row the file is treated as poisoned and the remaining segments are skipped.
        private const int MaxConsecutiveTimeouts = 3;

        private readonly FrameGrabber _grabber;
        private readonly string _videoPath;
        private int _consecutiveTimeouts;

        public Session(FrameGrabber grabber, string videoPath)
        {
            _grabber = grabber;
            _videoPath = videoPath;
            Duration = grabber.Duration;
        }

        public TimeSpan Duration { get; }

        public async Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            // FFmpeg's frame decode + WinRT JPEG encode are native and must not run concurrently (0xC0000374).
            // This section awaits, so it needs the async gate; the Skia work below stays under the sync leaf lock.
            byte[]? bytes;
            await NativeDecodeGate.VideoGate.WaitAsync(cancellationToken);
            try
            {
                // Poison-file short circuit: a decoder that hangs once usually hangs on every later segment too, and
                // each hang would otherwise cost a full FrameTimeout while the gate (and the whole ingest) is blocked.
                if (_consecutiveTimeouts >= MaxConsecutiveTimeouts)
                {
                    return false;
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(FrameTimeout);

                var work = ExtractFrameAsync();
                if (await Task.WhenAny(work, Task.Delay(FrameTimeout, timeoutCts.Token)) != work)
                {
                    // Timeout (or user cancel): the native call cannot be aborted, so abandon its task. Observe any
                    // eventual fault only so it does not surface as an unhandled task exception; the result is unused.
                    _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    _consecutiveTimeouts++;
                    return false;
                }

                bytes = await work;
                if (bytes == null) return false;
                _consecutiveTimeouts = 0;

                async Task<byte[]?> ExtractFrameAsync()
                {
                    using var frame = await _grabber.ExtractVideoFrameAsync(position, exactSeek: false, maxFrameSkip: MaxFrameSkipAfterKeyFrame);
                    if (frame == null) return null;

                    using var stream = new InMemoryRandomAccessStream();
                    await frame.EncodeAsJpegAsync(stream);
                    stream.Seek(0);

                    using var reader = new DataReader(stream);
                    await reader.LoadAsync((uint)stream.Size);
                    var buffer = new byte[stream.Size];
                    reader.ReadBytes(buffer);
                    return buffer;
                }
            }
            finally
            {
                NativeDecodeGate.VideoGate.Release();
            }

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
            // Tearing down the grabber also drives FFmpeg's native objects, so it takes the same gate as decode/encode.
            // Dispose is synchronous, so the blocking wait is correct here (nothing awaits while the gate is held).
            NativeDecodeGate.VideoGate.Wait();
            try
            {
                _grabber.Dispose();
            }
            finally
            {
                NativeDecodeGate.VideoGate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
