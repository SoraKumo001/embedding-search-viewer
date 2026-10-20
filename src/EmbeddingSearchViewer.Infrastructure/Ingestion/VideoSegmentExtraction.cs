using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Services;
using EmbeddingSearchViewer.Infrastructure.Core;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <param name="Index">0-based segment number.</param>
/// <param name="ThumbnailPath">JPEG of a frame from the middle of the segment, or null if no frame could be read.</param>
public sealed record VideoSegmentContent(int Index, int StartMs, int EndMs, string? ThumbnailPath);

/// <summary>Cuts a video into overlapping segments and grabs one representative frame per segment.</summary>
public interface IVideoSegmentExtractor
{
    Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(
        string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds, CancellationToken cancellationToken = default);
}

/// <summary>
/// An extractor that reports each segment as soon as its frame is on disk, so the caller can embed frames
/// while the decoder is still working on the rest of the video.
/// </summary>
public interface IStreamingVideoSegmentExtractor : IVideoSegmentExtractor
{
    /// <summary>
    /// Same result as <see cref="IVideoSegmentExtractor.ExtractAsync"/>. <paramref name="onSegmentReady"/> is called, in
    /// segment order, for every segment whose frame was read (never for a decoder that is abandoned for yielding no frame).
    /// Segments listed in <paramref name="alreadyDone"/> were finished by an earlier, interrupted run: their frames are
    /// not read again and <paramref name="onSegmentReady"/> is not called for them, but they still appear in the result.
    /// <paramref name="onProgress"/> reports (segments handled, segments in total) after every segment.
    /// When <paramref name="maxSegments"/> is above 0 and the video is so long that it would need more segments, the
    /// segments are made longer (see <see cref="VideoSegmentExtractor.ResolveSegmentSeconds"/>); 0 keeps the given length.
    /// </summary>
    Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(
        string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds,
        Action<VideoSegmentContent>? onSegmentReady, IReadOnlySet<int>? alreadyDone, Action<int, int>? onProgress,
        int maxSegments, CancellationToken cancellationToken = default);
}

/// <summary>An open video from which single frames can be grabbed.</summary>
public interface IVideoFrameSession : IAsyncDisposable
{
    TimeSpan Duration { get; }

    /// <summary>Writes the frame at <paramref name="position"/> as a JPEG. Returns false if the frame could not be read.</summary>
    Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken);
}

/// <summary>One way of decoding video frames (the Windows media stack, FFmpeg, ...).</summary>
public interface IVideoFrameSource
{
    string Name { get; }

    /// <summary>Opens the file, or returns null when this decoder cannot read it.</summary>
    Task<IVideoFrameSession?> OpenAsync(string videoPath, CancellationToken cancellationToken);
}

/// <summary>
/// A frame source that needs the parameters of the extraction before it opens the file. The FFmpeg worker child
/// process gets the segment length, the overlap and the segments to skip on its command line, and it has to write its
/// frames into the video's cache directory; <see cref="IVideoFrameSource.OpenAsync"/> carries none of that.
/// The extractor calls this once per extraction, so every video gets a source of its own (a source shared between
/// videos stays stateless and is used as it is).
/// </summary>
public interface IVideoFrameSourceBinding
{
    IVideoFrameSource BindForExtraction(
        string outputDirectory, int segmentSeconds, int overlapSeconds, IReadOnlySet<int>? alreadyDone, int maxSegments);
}

/// <summary>
/// Plans the segments and grabs their frames. Frame sources are tried in order: the first one that can open the file
/// and produce at least one frame wins, so the preferred decoder can be backed by another one for files it cannot read.
/// </summary>
public sealed class VideoSegmentExtractor : IStreamingVideoSegmentExtractor
{
    private readonly Func<IReadOnlyList<IVideoFrameSource>> _sourcesProvider;

    /// <summary>Where the per-decoder extraction totals go (shared process-wide unless one is injected).</summary>
    private readonly VideoDecoderStats _decoderStats;

    public VideoSegmentExtractor(IEnumerable<IVideoFrameSource> sources, VideoDecoderStats? decoderStats = null)
        : this(() => sources.ToList(), decoderStats)
    {
    }

    /// <summary>
    /// The decoders are asked for on every extraction, so a decoder switched by a setting while the app runs applies to
    /// the videos indexed from then on (the sources are tried in order, see the class remarks).
    /// </summary>
    public VideoSegmentExtractor(Func<IReadOnlyList<IVideoFrameSource>> sourcesProvider, VideoDecoderStats? decoderStats = null)
    {
        _sourcesProvider = sourcesProvider;
        _decoderStats = decoderStats ?? VideoDecoderStats.Shared;
    }

    /// <summary>
    /// Segment boundaries in milliseconds, each <paramref name="segmentSeconds"/> long and starting
    /// (<paramref name="segmentSeconds"/> - <paramref name="overlapSeconds"/>) after the previous one.
    /// A trailing sliver (under one second) that the previous segment already covers is dropped.
    /// </summary>
    public static IReadOnlyList<(int StartMs, int EndMs)> PlanSegments(TimeSpan duration, int segmentSeconds, int overlapSeconds)
        => VideoSegmentPlanner.PlanSegments(duration, segmentSeconds, overlapSeconds);

    /// <summary>
    /// The segment length to use for a video of the given duration: <paramref name="baseSeconds"/>, or, when that would
    /// make more than <paramref name="maxSegments"/> segments, the shortest longer step (30, 60, 120, 180, 300 s) that
    /// fits (the longest one if none does). The result depends only on its arguments, so a resumed run plans the same
    /// segments. <paramref name="maxSegments"/> of 0 or less turns the adjustment off.
    /// </summary>
    public static int ResolveSegmentSeconds(TimeSpan duration, int baseSeconds, int maxSegments)
        => VideoSegmentPlanner.ResolveSegmentSeconds(duration, baseSeconds, maxSegments);

    /// <summary>Where the frame of a segment is written, so a later run can find it again.</summary>
    public static string FramePath(string outputDirectory, string videoPath, int segmentIndex)
        => VideoSegmentPlanner.FramePath(outputDirectory, videoPath, segmentIndex);

    public Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(
        string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds, CancellationToken cancellationToken = default)
        => ExtractAsync(videoPath, outputDirectory, segmentSeconds, overlapSeconds, null, null, null, 0, cancellationToken);

    public async Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(
        string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds,
        Action<VideoSegmentContent>? onSegmentReady, IReadOnlySet<int>? alreadyDone, Action<int, int>? onProgress,
        int maxSegments, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        // Read once per extraction: a change to the decoder setting applies to the videos indexed from then on.
        var sources = _sourcesProvider();

        long videoStart = Stopwatch.GetTimestamp();
        long fileSize = -1;
        try { fileSize = new FileInfo(videoPath).Length; } catch (Exception ex) { IngestTrace.Write($"video:stat-failed video={videoPath}", ex); }
        IngestTrace.Video("extract-begin", videoPath, null, 0,
            detail: $"size={fileSize} segmentSeconds={segmentSeconds} overlapSeconds={overlapSeconds} maxSegments={maxSegments} " +
                    $"decoders={string.Join(',', sources.Select(s => s.Name))}");

        foreach (var configured in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A source that needs the extraction parameters (the FFmpeg worker child) gets an instance of its own;
            // every other source is stateless across videos and is used as it is.
            IVideoFrameSource source = configured is IVideoFrameSourceBinding binding
                ? binding.BindForExtraction(outputDirectory, segmentSeconds, overlapSeconds, alreadyDone, maxSegments)
                : configured;

            long openStart = Stopwatch.GetTimestamp();
            IVideoFrameSession? session;
            try
            {
                session = await source.OpenAsync(videoPath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IngestTrace.Video("open-threw", videoPath, source.Name, ElapsedMs(openStart), ex: ex);
                continue; // this decoder cannot read the file; try the next one
            }
            if (session == null)
            {
                IngestTrace.Video("open-unusable", videoPath, source.Name, ElapsedMs(openStart));
                continue;
            }

            IngestTrace.Video("open-ok", videoPath, source.Name, ElapsedMs(openStart), detail: $"durationMs={(long)session.Duration.TotalMilliseconds}");

            await using (session)
            {
                int effectiveSeconds = ResolveSegmentSeconds(session.Duration, segmentSeconds, maxSegments);
                int effectiveOverlap = VideoSegmentPlanner.ResolveOverlapSeconds(segmentSeconds, overlapSeconds, effectiveSeconds);
                var plan = PlanSegments(session.Duration, effectiveSeconds, effectiveOverlap);
                if (plan.Count == 0)
                {
                    IngestTrace.Video("plan-empty", videoPath, source.Name, ElapsedMs(videoStart),
                        detail: $"durationMs={(long)session.Duration.TotalMilliseconds} effectiveSeconds={effectiveSeconds}");
                    continue;
                }

                IngestTrace.Video("plan-ready", videoPath, source.Name, ElapsedMs(videoStart),
                    detail: $"durationMs={(long)session.Duration.TotalMilliseconds} effectiveSeconds={effectiveSeconds} " +
                            $"effectiveOverlap={effectiveOverlap} segments={plan.Count} alreadyDone={alreadyDone?.Count ?? 0}");

                var segments = new List<VideoSegmentContent>(plan.Count);
                int grabbed = 0;
                int frameIssuesLogged = 0;
                for (int i = 0; i < plan.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (start, end) = plan[i];
                    string path = FramePath(outputDirectory, videoPath, i);

                    if (alreadyDone != null && alreadyDone.Contains(i))
                    {
                        // Finished before the last interruption: keep the segment, but do not decode or embed it again.
                        segments.Add(new VideoSegmentContent(i, start, end, File.Exists(path) ? path : null));
                        grabbed++;
                        onProgress?.Invoke(i + 1, plan.Count);
                        continue;
                    }

                    var middle = TimeSpan.FromMilliseconds((start + end) / 2.0);

                    bool ok;
                    bool issueLogged = false;
                    long frameStart = Stopwatch.GetTimestamp();
                    IngestTrace.CurrentSegment.Value = i;
                    try
                    {
                        ok = await session.SaveFrameAsync(middle, path, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (frameIssuesLogged++ < 5)
                        {
                            IngestTrace.Video("frame-threw", videoPath, source.Name, ElapsedMs(frameStart), i,
                                $"startMs={start} endMs={end}", ex);
                            issueLogged = true;
                        }
                        ok = false;
                    }
                    finally
                    {
                        IngestTrace.CurrentSegment.Value = null;
                    }

                    bool hasFrame = ok && File.Exists(path);
                    if (!hasFrame && !issueLogged && frameIssuesLogged++ < 5)
                    {
                        IngestTrace.Video("frame-missing", videoPath, source.Name, ElapsedMs(frameStart), i,
                            $"ok={ok} exists={File.Exists(path)} startMs={start} endMs={end}");
                    }
                    var segment = new VideoSegmentContent(i, start, end, hasFrame ? path : null);
                    segments.Add(segment);
                    if (hasFrame)
                    {
                        grabbed++;
                        onSegmentReady?.Invoke(segment);
                    }
                    onProgress?.Invoke(i + 1, plan.Count);
                }

                if (grabbed > 0)
                {
                    // Per-decoder totals for the future GET /v1/stats: the whole extraction of this video under the
                    // decoder that produced its frames, next to the count of frames written.
                    _decoderStats.Add(source.Name, Stopwatch.GetTimestamp() - videoStart, grabbed);
                    IngestTrace.Video("extract-ok", videoPath, source.Name, ElapsedMs(videoStart),
                        detail: $"segments={segments.Count} frames={grabbed}");
                    return segments;
                }
                // opened, but not a single frame could be read: let the next decoder try
                IngestTrace.Video("extract-no-frames", videoPath, source.Name, ElapsedMs(videoStart),
                    detail: $"segments={segments.Count}; trying the next decoder");
            }
        }

        IngestTrace.Video("extract-failed", videoPath, null, ElapsedMs(videoStart),
            detail: $"decoders={sources.Count} size={fileSize}; no decoder produced a frame");
        return Array.Empty<VideoSegmentContent>();
    }

    private static long ElapsedMs(long startTicks) => (long)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);
}
