using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFmpegInteropX;
using SkiaSharp;
using Windows.Storage.Streams;
using EmbeddingSearchViewer.Infrastructure.Core;
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
                IngestTrace.Video("open-failed", videoPath, "FFmpeg", 0, ex: ex);
                return null; // not a video FFmpeg can read either
            }

            if (grabber.Duration <= TimeSpan.Zero)
            {
                IngestTrace.Video("open-empty", videoPath, "FFmpeg", 0, detail: $"duration={grabber.Duration}");
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

        // A decoder that never returns must not block the whole ingest forever: after this long the gate is released
        // even though the abandoned native call may still be running. Reaching this is already a decoder bug.
        private static readonly TimeSpan AbandonedWorkWaitLimit = TimeSpan.FromSeconds(60);

        // After this many hangs in a row the file is treated as poisoned and the remaining segments are skipped.
        private const int MaxConsecutiveTimeouts = 3;

        private readonly FrameGrabber _grabber;
        private readonly string _videoPath;
        private int _consecutiveTimeouts;
        private volatile bool _disposeRequested;
        private volatile bool _abandonedOutstanding;

        public Session(FrameGrabber grabber, string videoPath)
        {
            _grabber = grabber;
            _videoPath = videoPath;
            Duration = grabber.Duration;
        }

        public TimeSpan Duration { get; }

        public async Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            // Disposal waits for the gate; a frame that starts after the dispose request would use a dead grabber.
            if (_disposeRequested) return false;

            // FFmpeg's frame decode + WinRT JPEG encode are native and must not run concurrently (0xC0000374).
            // This section awaits, so it needs the async gate; the Skia work below stays under the sync leaf lock.
            byte[]? bytes;
            long startTicks = Stopwatch.GetTimestamp();
            Task<byte[]?>? abandoned = null;
            CancellationTokenSource? timeoutCts = null;
            await NativeDecodeGate.VideoGate.WaitAsync(cancellationToken);
            try
            {
                // The flag can be raised while this call waited for the gate; check again before touching the grabber.
                if (_disposeRequested) return false;

                // Poison-file short circuit: a decoder that hangs once usually hangs on every later segment too, and
                // each hang would otherwise cost a full FrameTimeout while the gate (and the whole ingest) is blocked.
                if (_consecutiveTimeouts >= MaxConsecutiveTimeouts)
                {
                    return false;
                }

                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(FrameTimeout);

                var work = ExtractFrameAsync();
                if (await Task.WhenAny(work, Task.Delay(FrameTimeout, timeoutCts.Token)) != work)
                {
                    // Timeout (or user cancel): the native call cannot be aborted. Keep holding VideoGate until it
                    // finishes (see the finally below), otherwise the next video would drive FFmpeg while this call
                    // is still inside it: the concurrent native decode that corrupts the heap (0xC0000374).
                    _consecutiveTimeouts++;
                    abandoned = work;
                    IngestTrace.Video("frame-timeout", _videoPath, "FFmpeg", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds} consecutiveTimeouts={_consecutiveTimeouts} " +
                                $"cancelled={cancellationToken.IsCancellationRequested}");
                    return false;
                }

                bytes = await work;
                if (bytes == null)
                {
                    IngestTrace.Video("frame-null", _videoPath, "FFmpeg", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds}");
                    return false;
                }
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
                if (abandoned != null)
                {
                    // Hold the gate while the abandoned native call is still inside FFmpeg, but never wait forever.
                    try
                    {
                        await abandoned.WaitAsync(AbandonedWorkWaitLimit);
                    }
                    catch (TimeoutException)
                    {
                        // The abandoned call may still fault later; observe it so it does not surface as an unhandled
                        // task exception (the result is unused either way).
                        _ = abandoned.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        _abandonedOutstanding = true; // Dispose must not free the grabber under that call (see below)
                        IngestTrace.Video("frame-abandoned-still-running", _videoPath, "FFmpeg", ElapsedMs(startTicks),
                            detail: $"waitedMs={(long)AbandonedWorkWaitLimit.TotalMilliseconds}; releasing the video gate anyway");
                    }
                    catch (OperationCanceledException)
                    {
                        // The abandoned call ended by cancellation (a stopped run): expected, no error-log entry.
                    }
                    catch (Exception ex)
                    {
                        // The result is unused, but a fault here explains later decoder failures; keep it in the log.
                        IngestTrace.Video("frame-abandoned-faulted", _videoPath, "FFmpeg", ElapsedMs(startTicks), ex: ex);
                    }
                }
                // Do not dispose the token source while the abandoned call may still use its token.
                if (abandoned == null || abandoned.IsCompleted) timeoutCts?.Dispose();
                else _ = abandoned.ContinueWith(_ => timeoutCts?.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                NativeDecodeGate.VideoGate.Release();
            }

            SKBitmap? bitmap = null;
            SKBitmap? resized = null;
            SKData? jpeg = null;
            // NativeDecodeGate is a leaf: synchronous Skia only, no await, no other lock taken after it.
            lock (NativeDecodeGate.Lock)
            {
                bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null)
                {
                    IngestTrace.Video("skia-decode-null", _videoPath, "FFmpeg", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds} bytes={bytes.Length}");
                    return false;
                }

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

            if (jpeg == null)
            {
                IngestTrace.Video("jpeg-encode-null", _videoPath, "FFmpeg", ElapsedMs(startTicks),
                    detail: $"positionMs={(long)position.TotalMilliseconds}");
                jpeg?.Dispose();
                resized?.Dispose();
                bitmap?.Dispose();
                return false;
            }

            try
            {
                if (IngestTrace.FrameDetailEnabled)
                {
                    IngestTrace.Video("frame-write-begin", _videoPath, "FFmpeg", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds} bytes={jpeg.Size} path={jpegPath}");
                }
                await File.WriteAllBytesAsync(jpegPath, jpeg.ToArray(), cancellationToken);
                if (IngestTrace.FrameDetailEnabled)
                {
                    IngestTrace.Video("frame-write-end", _videoPath, "FFmpeg", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds} path={jpegPath}");
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IngestTrace.Video("frame-write-failed", _videoPath, "FFmpeg", ElapsedMs(startTicks),
                    detail: $"positionMs={(long)position.TotalMilliseconds} path={jpegPath}", ex: ex);
                throw;
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
            // Stop new frames from entering before taking the gate: the wait below may sit behind a frame in progress.
            _disposeRequested = true;

            // Tearing down the grabber also drives FFmpeg's native objects, so it takes the same gate as decode/encode.
            // SaveFrameAsync now holds the gate until an abandoned native call has finished (bounded), so once this
            // wait returns nothing is using _grabber any more. Dispose is synchronous, so the blocking wait is correct
            // here (nothing awaits while the gate is held).
            NativeDecodeGate.VideoGate.Wait();
            try
            {
                if (_abandonedOutstanding)
                {
                    // The decoder never returned within the bounded wait, so it may still be inside the grabber.
                    // Leaking it is safer than freeing it under a native call that is still running (use-after-free).
                    IngestTrace.Video("dispose-skipped-abandoned", _videoPath, "FFmpeg", 0,
                        detail: "an abandoned frame grab never finished; leaving the grabber undisposed");
                    return ValueTask.CompletedTask;
                }

                try
                {
                    _grabber.Dispose();
                }
                catch (Exception ex)
                {
                    // A failing native teardown is worth a trace (it may explain a later crash), but must not fail the run.
                    IngestTrace.Video("dispose-failed", _videoPath, "FFmpeg", 0, ex: ex);
                }
            }
            finally
            {
                NativeDecodeGate.VideoGate.Release();
            }

            return ValueTask.CompletedTask;
        }

        private static long ElapsedMs(long startTicks) => (long)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);
    }
}
