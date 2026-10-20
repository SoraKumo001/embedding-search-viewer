using System;
using System.Globalization;
using System.Threading;

namespace EmbeddingSearchViewer.Infrastructure.Core;

/// <summary>
/// Diagnostic trace for the ingestion path. Infrastructure code only writes to <see cref="Sink"/>; the app connects it
/// to <c>CrashLog</c> at startup. The sink is deliberately a plain delegate so that this assembly never references the
/// app, and tests can capture the lines without touching the log file.
/// </summary>
public static class IngestTrace
{
    /// <summary>Receives (line, exception). The app sets this to write to the crash log; null disables tracing.</summary>
    public static Action<string, Exception?>? Sink { get; set; }

    /// <summary>
    /// The segment currently being decoded. Set by <see cref="Ingestion.VideoSegmentExtractor"/> around a frame grab so
    /// that frame-level messages can name the segment without changing <see cref="Ingestion.IVideoFrameSession"/>.
    /// AsyncLocal keeps concurrent videos from overwriting each other's value.
    /// </summary>
    public static readonly AsyncLocal<int?> CurrentSegment = new();

    /// <summary>
    /// Per-frame write tracing (write begin/end and frame timing). Off by default: a normal video produces one line per
    /// segment otherwise, which is too much for the error log. The store's <c>FrameTracing</c> switch is injected here
    /// as <see cref="FrameDetailProvider"/>; without a provider the last value written to
    /// <see cref="FrameDetailEnabled"/> is used (false, the default).
    /// </summary>
    public static Func<bool>? FrameDetailProvider { get; set; }

    /// <summary>
    /// Whether the per-frame lines are written. Reads <see cref="FrameDetailProvider"/> (the diagnostics store) when it
    /// is set, so a change applies to the next frame; the backing field is volatile for the provider-less case.
    /// </summary>
    public static bool FrameDetailEnabled
    {
        get => FrameDetailProvider?.Invoke() ?? _frameDetailEnabled;
        set => _frameDetailEnabled = value;
    }

    private static volatile bool _frameDetailEnabled;

    /// <summary>Writes one line, then the exception's full text (through the sink). Never throws.</summary>
    public static void Write(string message, Exception? ex = null)
    {
        try
        {
            Sink?.Invoke($"[{DateTime.Now:HH:mm:ss.fff}] [thread={Environment.CurrentManagedThreadId}] {message}", ex);
        }
        catch (Exception)
        {
            // Tracing must never be what fails the ingest.
        }
    }

    /// <summary>
    /// One video-related line. Includes the fields needed to identify a crash: time, thread, video path, decoder,
    /// segment index and elapsed milliseconds.
    /// </summary>
    public static void Video(string stage, string videoPath, string? decoder, long elapsedMs,
        int? segmentIndex = null, string? detail = null, Exception? ex = null)
    {
        int? segment = segmentIndex ?? CurrentSegment.Value;
        string segmentText = segment.HasValue ? segment.Value.ToString(CultureInfo.InvariantCulture) : "-";
        string line = $"video:{stage} decoder={decoder ?? "-"} segment={segmentText} elapsedMs={elapsedMs} video={videoPath}";
        if (!string.IsNullOrEmpty(detail)) line += " " + detail;
        Write(line, ex);
    }
}
