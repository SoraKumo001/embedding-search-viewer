using System;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>
/// Diagnostics switches for video ingestion (P2c gate / parallelism experiments and frame tracing), held in one
/// process-wide store so the UI and a future <c>GET /v1/stats</c> lane can change them without touching the ingest
/// code. The environment variables are read once in the constructor and only as the initial value; after that the
/// properties are the single source of truth.
/// </summary>
/// <remarks>
/// The worker reads <see cref="VideoParallelism"/> and <see cref="FfmpegPermit"/> when a run starts
/// (<c>StartLocked</c>), so a change applies from the next run and never reshapes a running one. The decode path reads
/// <see cref="IgnoreDecoderMarkers"/> per video, <see cref="FrameTracing"/> per frame and
/// <see cref="UseFfmpegWorker"/> per video, so those three can be toggled while the app runs. All five are volatile:
/// they are written on the UI thread and read on ingest threads.
/// </remarks>
public sealed class RunDiagnosticsOptions
{
    /// <summary>Lowest video parallelism: strictly one video at a time.</summary>
    public const int MinVideoParallelism = 1;

    /// <summary>Highest video parallelism: the historical default and the only value used unless lowered.</summary>
    public const int MaxVideoParallelism = 3;

    /// <summary>Lowest FFmpeg permit count: the historical strictly serial behaviour.</summary>
    public const int MinFfmpegPermit = 1;

    /// <summary>Highest FFmpeg permit count: the opt-in two-call experiment, never more (heap corruption probe limit).</summary>
    public const int MaxFfmpegPermit = 2;

    private const string VideoParallelismVariable = "ESV_VIDEO_PARALLELISM";
    private const string FfmpegPermitVariable = "ESV_FFMPEG_PERMIT";
    private const string IgnoreDecoderMarkersVariable = "ESV_IGNORE_DECODER_MARKERS";
    private const string FrameTracingVariable = "ESV_INGEST_TRACE_FRAMES";
    private const string FfmpegWorkerVariable = "ESV_FFMPEG_WORKER";

    private volatile int _videoParallelism;
    private volatile int _ffmpegPermit;
    private volatile bool _ignoreDecoderMarkers;
    private volatile bool _frameTracing;
    private volatile bool _useFfmpegWorker;

    /// <summary>Reads the current environment flags as the starting values; every later change is in memory only.</summary>
    public RunDiagnosticsOptions()
    {
        VideoParallelism = ParseInt(VideoParallelismVariable, MaxVideoParallelism);
        FfmpegPermit = ParseInt(FfmpegPermitVariable, MinFfmpegPermit);
        IgnoreDecoderMarkers = IsEnabled(IgnoreDecoderMarkersVariable);
        FrameTracing = IsEnabled(FrameTracingVariable);
        UseFfmpegWorker = IsEnabled(FfmpegWorkerVariable);
    }

    /// <summary>
    /// How many videos are indexed at once. Clamped to <see cref="MinVideoParallelism"/>..<see cref="MaxVideoParallelism"/>;
    /// the default (3) keeps the historical behaviour.
    /// </summary>
    public int VideoParallelism
    {
        get => _videoParallelism;
        set => _videoParallelism = Math.Clamp(value, MinVideoParallelism, MaxVideoParallelism);
    }

    /// <summary>
    /// How many FFmpeg native sections may run at once. Clamped to <see cref="MinFfmpegPermit"/>..<see cref="MaxFfmpegPermit"/>;
    /// the default (1) keeps the historical serial behaviour.
    /// </summary>
    public int FfmpegPermit
    {
        get => _ffmpegPermit;
        set => _ffmpegPermit = Math.Clamp(value, MinFfmpegPermit, MaxFfmpegPermit);
    }

    /// <summary>
    /// When true, a leftover decoder marker is ignored and FFmpeg is used again. Off by default, as before: a marker
    /// means the last FFmpeg decode of that video may have killed the process.
    /// </summary>
    public bool IgnoreDecoderMarkers
    {
        get => _ignoreDecoderMarkers;
        set => _ignoreDecoderMarkers = value;
    }

    /// <summary>
    /// Per-frame write tracing (write begin/end and frame timing) in addition to the per-segment lines. Off by default;
    /// a normal video produces one line per segment otherwise, which is too much for the error log.
    /// </summary>
    public bool FrameTracing
    {
        get => _frameTracing;
        set => _frameTracing = value;
    }

    /// <summary>
    /// When on, FFmpeg runs in a child process (one per video) instead of in the app: FFmpeg's native objects then
    /// cannot be driven concurrently by videos decoded at the same time. Off by default: the in-process decoder with
    /// its process-wide gate stays the default path. Read per video, like the decoder choice itself.
    /// </summary>
    public bool UseFfmpegWorker
    {
        get => _useFfmpegWorker;
        set => _useFfmpegWorker = value;
    }

    private static int ParseInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    private static bool IsEnabled(string name)
        => string.Equals(Environment.GetEnvironmentVariable(name), "1", StringComparison.Ordinal);
}
