using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
/// Plans the segments and grabs their frames. Frame sources are tried in order: the first one that can open the file
/// and produce at least one frame wins, so the preferred decoder can be backed by another one for files it cannot read.
/// </summary>
public sealed class VideoSegmentExtractor : IVideoSegmentExtractor
{
    private readonly IReadOnlyList<IVideoFrameSource> _sources;

    public VideoSegmentExtractor(IEnumerable<IVideoFrameSource> sources)
    {
        _sources = sources.ToList();
    }

    /// <summary>
    /// Segment boundaries in milliseconds, each <paramref name="segmentSeconds"/> long and starting
    /// (<paramref name="segmentSeconds"/> - <paramref name="overlapSeconds"/>) after the previous one.
    /// A trailing sliver (under one second) that the previous segment already covers is dropped.
    /// </summary>
    public static IReadOnlyList<(int StartMs, int EndMs)> PlanSegments(TimeSpan duration, int segmentSeconds, int overlapSeconds)
    {
        var result = new List<(int, int)>();
        if (duration <= TimeSpan.Zero || segmentSeconds <= 0) return result;

        double step = segmentSeconds - overlapSeconds;
        if (step <= 0) step = segmentSeconds;

        double total = duration.TotalSeconds;
        for (double start = 0; start < total; start += step)
        {
            double end = Math.Min(start + segmentSeconds, total);
            if (result.Count > 0 && end - start < 1.0) break;
            result.Add(((int)(start * 1000), (int)(end * 1000)));
            if (end >= total) break;
        }
        return result;
    }

    public async Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(
        string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        string baseName = Path.GetFileNameWithoutExtension(videoPath);

        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IVideoFrameSession? session;
            try
            {
                session = await source.OpenAsync(videoPath, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // this decoder cannot read the file; try the next one
            }
            if (session == null) continue;

            await using (session)
            {
                var plan = PlanSegments(session.Duration, segmentSeconds, overlapSeconds);
                if (plan.Count == 0) continue;

                var segments = new List<VideoSegmentContent>(plan.Count);
                int grabbed = 0;
                for (int i = 0; i < plan.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (start, end) = plan[i];
                    string path = Path.Combine(outputDirectory, $"{baseName}_seg_{i:D4}.jpg");
                    var middle = TimeSpan.FromMilliseconds((start + end) / 2.0);

                    bool ok;
                    try
                    {
                        ok = await session.SaveFrameAsync(middle, path, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        ok = false;
                    }

                    if (ok && File.Exists(path)) grabbed++;
                    segments.Add(new VideoSegmentContent(i, start, end, ok && File.Exists(path) ? path : null));
                }

                if (grabbed > 0) return segments;
                // opened, but not a single frame could be read: let the next decoder try
            }
        }

        return Array.Empty<VideoSegmentContent>();
    }
}
