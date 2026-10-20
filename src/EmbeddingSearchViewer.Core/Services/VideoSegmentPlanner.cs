using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmbeddingSearchViewer.Core.Services;

/// <summary>
/// Where the segments of a video start and end, and where their frames are written. This lives in Core (and not next
/// to the extractor) because the FFmpeg worker child process plans the same video on its own: the parent and the child
/// must come to exactly the same plan, so both call these methods.
/// </summary>
public static class VideoSegmentPlanner
{
    private static readonly int[] SegmentSteps = { 15, 30, 60, 120, 180, 300 };

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

    /// <summary>
    /// The segment length to use for a video of the given duration: <paramref name="baseSeconds"/>, or, when that would
    /// make more than <paramref name="maxSegments"/> segments, the shortest longer step (30, 60, 120, 180, 300 s) that
    /// fits (the longest one if none does). The result depends only on its arguments, so a resumed run plans the same
    /// segments. <paramref name="maxSegments"/> of 0 or less turns the adjustment off.
    /// </summary>
    public static int ResolveSegmentSeconds(TimeSpan duration, int baseSeconds, int maxSegments)
    {
        if (maxSegments <= 0 || baseSeconds <= 0) return baseSeconds;

        int current = baseSeconds;
        foreach (int step in SegmentSteps.Where(s => s > baseSeconds).Prepend(baseSeconds))
        {
            current = step;
            if (PlanSegments(duration, step, Math.Min(3, step / 4)).Count <= maxSegments) break;
        }
        return current;
    }

    /// <summary>
    /// The overlap that goes with a widened segment length: the requested one, or the widest a widened segment allows.
    /// </summary>
    public static int ResolveOverlapSeconds(int baseSeconds, int overlapSeconds, int effectiveSeconds)
        => effectiveSeconds == baseSeconds ? overlapSeconds : Math.Min(3, effectiveSeconds / 4);

    /// <summary>Where the frame of a segment is written, so a later run can find it again.</summary>
    public static string FramePath(string outputDirectory, string videoPath, int segmentIndex)
        => Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(videoPath)}_seg_{segmentIndex:D4}.jpg");
}
