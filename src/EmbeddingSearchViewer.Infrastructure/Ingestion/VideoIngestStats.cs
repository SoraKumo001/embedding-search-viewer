using System.Diagnostics;
using EmbeddingSearchViewer.Core.Localization;
using System.Threading;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// Where the time of video indexing goes. Extraction (decoding frames, CPU) and embedding (GPU) of one video overlap,
/// so their sum can exceed the time the video took; the difference is the time saved by overlapping.
/// </summary>
public sealed class VideoIngestStats
{
    private long _videos;
    private long _segments;
    private long _extractTicks;
    private long _embedTicks;
    private long _videoTicks;

    public long Videos => Interlocked.Read(ref _videos);
    public long Segments => Interlocked.Read(ref _segments);
    public double ExtractSeconds => ToSeconds(Interlocked.Read(ref _extractTicks));
    public double EmbedSeconds => ToSeconds(Interlocked.Read(ref _embedTicks));

    /// <summary>Total time spent per video, from the start of a video to the end of its embedding (summed over videos).</summary>
    public double VideoSeconds => ToSeconds(Interlocked.Read(ref _videoTicks));

    private static double ToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    internal void AddExtract(long ticks) => Interlocked.Add(ref _extractTicks, ticks);
    internal void AddEmbed(long ticks) => Interlocked.Add(ref _embedTicks, ticks);

    internal void AddVideo(long ticks, int segments)
    {
        Interlocked.Increment(ref _videos);
        Interlocked.Add(ref _segments, segments);
        Interlocked.Add(ref _videoTicks, ticks);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _videos, 0);
        Interlocked.Exchange(ref _segments, 0);
        Interlocked.Exchange(ref _extractTicks, 0);
        Interlocked.Exchange(ref _embedTicks, 0);
        Interlocked.Exchange(ref _videoTicks, 0);
    }

    /// <summary>One line for the UI, or an empty string when no video was processed.</summary>
    public string Summary(double elapsedSeconds)
    {
        if (Videos == 0) return string.Empty;
        return Loc.T("Ingest.VideoStats", Videos, Segments, ExtractSeconds, EmbedSeconds, VideoSeconds, elapsedSeconds);
    }
}
