using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// Per-decoder extraction totals (time spent and frames written) for the diagnostics surface a future
/// <c>GET /v1/stats</c> will expose. The values are raw: seconds derived from <see cref="Stopwatch"/> ticks and a
/// frame count, both summed over the process lifetime until <see cref="Reset"/>.
/// </summary>
/// <remarks>
/// Counters are updated from the ingestion threads while the UI (or a future HTTP lane) reads them, so the backing
/// map is a <see cref="ConcurrentDictionary{TKey,TValue}"/> and snapshots copy it. The time is the whole extraction of
/// a video (open, plan and frame grabs), measured at the <c>extract-ok</c> point, the same basis as
/// <see cref="VideoIngestStats.ExtractSeconds"/> but split by the decoder that produced the frames.
/// </remarks>
public sealed class VideoDecoderStats
{
    /// <summary>
    /// Process-wide counters used by extractors that were not given an instance (the app's decoder list and the
    /// Media-Foundation-only fallback built for a decoder marker). Read by the future stats endpoint.
    /// </summary>
    public static VideoDecoderStats Shared { get; } = new();

    private readonly ConcurrentDictionary<string, (long Ticks, long Frames)> _byDecoder = new(StringComparer.Ordinal);

    /// <summary>Adds one finished extraction for <paramref name="decoder"/>; safe to call from several threads.</summary>
    internal void Add(string decoder, long elapsedTicks, int frames)
    {
        if (elapsedTicks < 0) elapsedTicks = 0;
        if (frames < 0) frames = 0;

        _byDecoder.AddOrUpdate(
            decoder,
            _ => (elapsedTicks, frames),
            (_, previous) => (previous.Ticks + elapsedTicks, previous.Frames + frames));
    }

    /// <summary>
    /// Raw per-decoder totals: seconds of extraction and number of frame files written. A snapshot, so reading it
    /// while a run is in progress is safe. Decoder names are the frame sources' <c>Name</c> ("FFmpeg", "MediaFoundation").
    /// </summary>
    public IReadOnlyDictionary<string, (double Seconds, long Frames)> GetRaw()
    {
        var snapshot = new Dictionary<string, (double Seconds, long Frames)>(StringComparer.Ordinal);
        foreach (var pair in _byDecoder)
        {
            snapshot[pair.Key] = (ToSeconds(pair.Value.Ticks), pair.Value.Frames);
        }
        return snapshot;
    }

    /// <summary>Raw totals for one decoder (seconds, frames); (0, 0) when it has not run.</summary>
    public (double Seconds, long Frames) GetRaw(string decoder)
        => _byDecoder.TryGetValue(decoder, out var value) ? (ToSeconds(value.Ticks), value.Frames) : (0, 0);

    /// <summary>Clears the counters (the next run starts from zero).</summary>
    public void Reset() => _byDecoder.Clear();

    private static double ToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
