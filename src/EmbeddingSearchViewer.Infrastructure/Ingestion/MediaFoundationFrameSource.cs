using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using SkiaSharp;
using Windows.Media.Editing;
using Windows.Storage;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// Grabs frames with the Windows media stack (Media Foundation via <c>Windows.Media.Editing</c>).
/// It needs no extra files and covers the common containers and codecs (mp4/H.264, wmv, avi, mov, and on current
/// Windows mkv). It is the fallback behind FFmpeg in the app, and the only decoder when none is configured.
/// </summary>
public sealed class MediaFoundationFrameSource : IVideoFrameSource
{
    private const int MaxFrameWidth = 960;

    public string Name => "Media Foundation";

    public async Task<IVideoFrameSession?> OpenAsync(string videoPath, CancellationToken cancellationToken)
    {
        // Creating the clip opens Media Foundation's native source reader, so it shares the process-wide video gate
        // with frame decode/encode and the FFmpeg stack (0xC0000374).
        await NativeDecodeGate.VideoGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoPath));
                var clip = await MediaClip.CreateFromFileAsync(file);
                if (clip.OriginalDuration <= TimeSpan.Zero) { return null; }

                var props = clip.GetVideoEncodingProperties();
                if (props == null || props.Width == 0 || props.Height == 0) { return null; } // audio only or unreadable video stream

                var composition = new MediaComposition();
                composition.Clips.Add(clip);
                return new Session(composition, clip.OriginalDuration, (int)props.Width, (int)props.Height, Path.GetFullPath(videoPath));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or FileNotFoundException
                                           or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                return null;
            }
        }
        finally
        {
            NativeDecodeGate.VideoGate.Release();
        }
    }

    private sealed class Session : IVideoFrameSession
    {
        // Measured extraction is well under a second, so 120s is a generous ceiling that only a decoder which never
        // returns can hit; abandoning the work keeps VideoGate (and the whole ingest) from stalling forever.
        private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

        // After this many hangs in a row the file is treated as poisoned and the remaining segments are skipped.
        // Media Foundation never recovers once it hangs (observed: zero successes after the first hang), so give up fast.
        private const int MaxConsecutiveTimeouts = 1;

        private readonly MediaComposition _composition;
        private readonly int _width, _height;
        private readonly string _videoPath;
        private int _consecutiveTimeouts;

        public Session(MediaComposition composition, TimeSpan duration, int width, int height, string videoPath)
        {
            _composition = composition;
            _videoPath = videoPath;
            Duration = duration;
            // Display size without upscaling; the thumbnail API scales to exactly the size we ask for.
            double scale = Math.Min(1.0, (double)MaxFrameWidth / width);
            _width = Math.Max(2, (int)Math.Round(width * scale));
            _height = Math.Max(2, (int)Math.Round(height * scale));
        }

        public TimeSpan Duration { get; }

        public async Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            // Media Foundation's thumbnail grab is native and shares the process-wide video gate with FFmpeg so the
            // two codec stacks are never driven concurrently (0xC0000374). The section awaits, so it uses VideoGate.
            byte[] bytes;
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

                var work = GrabThumbnailAsync(timeoutCts.Token);
                if (await Task.WhenAny(work, Task.Delay(FrameTimeout, timeoutCts.Token)) != work)
                {
                    // Timeout (or user cancel): the native call cannot be aborted, so abandon its task. Observe any
                    // eventual fault only so it does not surface as an unhandled task exception; the result is unused.
                    _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    _consecutiveTimeouts++;
                    return false;
                }

                bytes = await work;
                _consecutiveTimeouts = 0;

                async Task<byte[]> GrabThumbnailAsync(CancellationToken ct)
                {
                    using var stream = await _composition.GetThumbnailAsync(position, _width, _height, VideoFramePrecision.NearestFrame);

                    using var input = stream.AsStreamForRead();
                    using var buffer = new MemoryStream();
                    await input.CopyToAsync(buffer, ct);
                    return buffer.ToArray();
                }
            }
            finally
            {
                NativeDecodeGate.VideoGate.Release();
            }

            SKData? jpeg;
            // NativeDecodeGate is a leaf: synchronous Skia only, no await, no other lock taken after it.
            lock (NativeDecodeGate.Lock)
            {
                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null) return false;

                using var image = SKImage.FromBitmap(bitmap);
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
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
