using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Infrastructure.Core;
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
                if (clip.OriginalDuration <= TimeSpan.Zero)
                {
                    IngestTrace.Video("open-empty", videoPath, "MediaFoundation", 0, detail: $"duration={clip.OriginalDuration}");
                    return null;
                }

                var props = clip.GetVideoEncodingProperties();
                if (props == null || props.Width == 0 || props.Height == 0)
                {
                    IngestTrace.Video("open-no-video-stream", videoPath, "MediaFoundation", 0,
                        detail: $"width={props?.Width} height={props?.Height}");
                    return null; // audio only or unreadable video stream
                }

                var composition = new MediaComposition();
                composition.Clips.Add(clip);
                return new Session(composition, clip.OriginalDuration, (int)props.Width, (int)props.Height, Path.GetFullPath(videoPath));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or FileNotFoundException
                                           or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
            {
                IngestTrace.Video("open-failed", videoPath, "MediaFoundation", 0, ex: ex);
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

        // A decoder that never returns must not block the whole ingest forever: after this long the gate is released
        // even though the abandoned native call may still be running. Reaching this is already a decoder bug.
        private static readonly TimeSpan AbandonedWorkWaitLimit = TimeSpan.FromSeconds(60);

        // After this many hangs in a row the file is treated as poisoned and the remaining segments are skipped.
        // Media Foundation never recovers once it hangs (observed: zero successes after the first hang), so give up fast.
        private const int MaxConsecutiveTimeouts = 1;

        private readonly MediaComposition _composition;
        private readonly int _width, _height;
        private readonly string _videoPath;
        private int _consecutiveTimeouts;
        private volatile bool _disposeRequested;
        private volatile bool _abandonedOutstanding;

        // One trace per process: this limitation applies to every session, and one line per video would be noise.
        private static int _leakTraceLogged;

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
            // Disposal waits for the gate; a frame that starts after the dispose request would use a closed clip.
            if (_disposeRequested) return false;

            // Media Foundation's thumbnail grab is native and shares the process-wide video gate with FFmpeg so the
            // two codec stacks are never driven concurrently (0xC0000374). The section awaits, so it uses VideoGate.
            byte[] bytes;
            long startTicks = Stopwatch.GetTimestamp();
            Task<byte[]>? abandoned = null;
            CancellationTokenSource? timeoutCts = null;
            await NativeDecodeGate.VideoGate.WaitAsync(cancellationToken);
            try
            {
                // The flag can be raised while this call waited for the gate; check again before touching the clip.
                if (_disposeRequested) return false;

                // Poison-file short circuit: a decoder that hangs once usually hangs on every later segment too, and
                // each hang would otherwise cost a full FrameTimeout while the gate (and the whole ingest) is blocked.
                if (_consecutiveTimeouts >= MaxConsecutiveTimeouts)
                {
                    return false;
                }

                timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(FrameTimeout);

                var work = GrabThumbnailAsync(timeoutCts.Token);
                if (await Task.WhenAny(work, Task.Delay(FrameTimeout, timeoutCts.Token)) != work)
                {
                    // Timeout (or user cancel): the native call cannot be aborted. Keep holding VideoGate until it
                    // finishes (see the finally below), otherwise the next video would drive Media Foundation while
                    // this call is still inside it: the concurrent native decode that corrupts the heap (0xC0000374).
                    _consecutiveTimeouts++;
                    abandoned = work;
                    IngestTrace.Video("frame-timeout", _videoPath, "MediaFoundation", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds} consecutiveTimeouts={_consecutiveTimeouts} " +
                                $"cancelled={cancellationToken.IsCancellationRequested}");
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
                if (abandoned != null)
                {
                    // Hold the gate while the abandoned native call is still inside Media Foundation, but never wait forever.
                    try
                    {
                        await abandoned.WaitAsync(AbandonedWorkWaitLimit);
                    }
                    catch (TimeoutException)
                    {
                        // The abandoned call may still fault later; observe it so it does not surface as an unhandled
                        // task exception (the result is unused either way).
                        _ = abandoned.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                        _abandonedOutstanding = true; // Dispose must not touch the composition under that call
                        IngestTrace.Video("frame-abandoned-still-running", _videoPath, "MediaFoundation", ElapsedMs(startTicks),
                            detail: $"waitedMs={(long)AbandonedWorkWaitLimit.TotalMilliseconds}; releasing the video gate anyway");
                    }
                    catch (OperationCanceledException)
                    {
                        // The abandoned call ended by cancellation (a stopped run): expected, no error-log entry.
                    }
                    catch (Exception ex)
                    {
                        // The result is unused, but a fault here explains later decoder failures; keep it in the log.
                        IngestTrace.Video("frame-abandoned-faulted", _videoPath, "MediaFoundation", ElapsedMs(startTicks), ex: ex);
                    }
                }
                // Do not dispose the token source while the abandoned call may still use its token.
                if (abandoned == null || abandoned.IsCompleted) timeoutCts?.Dispose();
                else _ = abandoned.ContinueWith(_ => timeoutCts?.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                NativeDecodeGate.VideoGate.Release();
            }

            SKData? jpeg;
            // NativeDecodeGate is a leaf: synchronous Skia only, no await, no other lock taken after it.
            lock (NativeDecodeGate.Lock)
            {
                using var bitmap = SKBitmap.Decode(bytes);
                if (bitmap == null)
                {
                    IngestTrace.Video("skia-decode-null", _videoPath, "MediaFoundation", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds} bytes={bytes.Length}");
                    return false;
                }

                using var image = SKImage.FromBitmap(bitmap);
                jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            }

            if (jpeg == null)
            {
                IngestTrace.Video("jpeg-encode-null", _videoPath, "MediaFoundation", ElapsedMs(startTicks),
                    detail: $"positionMs={(long)position.TotalMilliseconds}");
                return false;
            }

            try
            {
                if (IngestTrace.FrameDetailEnabled)
                {
                    IngestTrace.Video("frame-write-begin", _videoPath, "MediaFoundation", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds} bytes={jpeg.Size} path={jpegPath}");
                }
                await File.WriteAllBytesAsync(jpegPath, jpeg.ToArray(), cancellationToken);
                if (IngestTrace.FrameDetailEnabled)
                {
                    IngestTrace.Video("frame-write-end", _videoPath, "MediaFoundation", ElapsedMs(startTicks),
                        detail: $"positionMs={(long)position.TotalMilliseconds} path={jpegPath}");
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IngestTrace.Video("frame-write-failed", _videoPath, "MediaFoundation", ElapsedMs(startTicks),
                    detail: $"positionMs={(long)position.TotalMilliseconds} path={jpegPath}", ex: ex);
                throw;
            }
            finally
            {
                jpeg?.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        {
            // Stop new frames from entering before taking the gate.
            _disposeRequested = true;

            // TODO: MediaClip and MediaComposition (Windows.Media.Editing) expose no Close/Dispose, so the native
            // Media Foundation source reader behind the clip cannot be released deterministically here. What we can do
            // is drop our reference to the clip so the WinRT wrapper becomes collectable, and keep the session poisoned
            // (dispose flag + gate) so nothing touches it again. Take the video gate so an abandoned frame grab
            // (SaveFrameAsync holds it until that call ends) cannot still be using the composition.
            NativeDecodeGate.VideoGate.Wait();
            try
            {
                if (_abandonedOutstanding)
                {
                    // The decoder never returned within the bounded wait, so it may still be inside the composition.
                    // Leaving the (GC-collected) wrapper alone is safer than clearing it under a running native call.
                    IngestTrace.Video("dispose-skipped-abandoned", _videoPath, "MediaFoundation", 0,
                        detail: "an abandoned thumbnail grab never finished; leaving the composition untouched");
                    return ValueTask.CompletedTask;
                }

                try
                {
                    _composition.Clips.Clear();
                }
                catch (Exception ex)
                {
                    // A failing teardown is worth a trace (it may explain a later crash), but must not fail the run.
                    IngestTrace.Video("dispose-failed", _videoPath, "MediaFoundation", 0, ex: ex);
                }
            }
            finally
            {
                if (Interlocked.Exchange(ref _leakTraceLogged, 1) == 0)
                {
                    IngestTrace.Video("dispose-leak-todo", _videoPath, "MediaFoundation", 0,
                        detail: "MediaClip/MediaComposition are not IDisposable; the native reader is released only by GC");
                }
                NativeDecodeGate.VideoGate.Release();
            }

            return ValueTask.CompletedTask;
        }

        private static long ElapsedMs(long startTicks) => (long)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);
    }
}
