using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Core;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

public record IngestionProgress(
    int TotalFiles,
    int ProcessedFiles,
    string CurrentFileName,
    bool IsRunning,
    string? ErrorMessage = null,
    int SkippedFiles = 0,
    string? Summary = null,
    string? Detail = null,
    int ExcludedFiles = 0,
    double InFlightFiles = 0, // how many files' worth of work the videos being processed have already done (for the time estimate)
    int QuickFiles = 0, // of the processed files, those that needed no work (already indexed, or left out): they say nothing about the speed
    double VideoDoneScenes = 0, // scenes of this run's videos that are done (finished videos included): for the time estimate
    double VideoRemainingScenes = 0 // scenes still to embed in the videos being processed: for the time estimate
);

public partial class IngestionPipelineWorker
{
    private readonly IMediaRepository _repository;
    private readonly IEmbeddingService _embeddingService;
    private readonly PdfMediaIngestor _pdfIngestor;
    private readonly OfficeMediaIngestor _officeIngestor;
    private readonly ImageMediaIngestor _imageIngestor;
    private readonly VideoMediaIngestor _videoIngestor;

    /// <summary>Videos are decoded on the CPU and embedded on the GPU; several at once keep both busy.</summary>
    public const int DefaultVideoParallelism = 3;

    /// <summary>Highest FFmpeg permit count the diagnostics store may select (see <see cref="NativeDecodeGate"/>).</summary>
    private const int MaxFfmpegPermit = 2;

    // Rebuilt in StartLocked when the diagnostics store asks for another parallelism: a run keeps the slots it started
    // with, so changing the setting never reshapes work that is in flight.
    private SemaphoreSlim _videoSlots;
    private int _videoParallelism;

    /// <summary>Read at each run start; null keeps the fixed constructor value (tests, non-app callers).</summary>
    private readonly Func<int>? _videoParallelismProvider;

    /// <summary>Read at each run start; null keeps the historical 1 FFmpeg permit.</summary>
    private readonly Func<int>? _ffmpegPermitProvider;

    /// <summary>Read per video by <see cref="VideoMediaIngestor"/>; null keeps the historical false.</summary>
    private readonly Func<bool>? _ignoreDecoderMarkers;

    // Saves come from the queue loop and from videos running in parallel; they are written one at a time.
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Stopwatch _runWatch = new();

    private readonly Func<int> _videoSegmentSeconds;

    /// <summary>Length of the video segments in seconds when the settings do not say otherwise.</summary>
    public const int DefaultVideoSegmentSeconds = 15;

    /// <summary>With auto-adjustment on, a video gets at most this many segments (longer videos get longer segments).</summary>
    public const int AutoMaxVideoSegments = 400;

    private readonly Func<bool> _videoAutoSegment;

    /// <summary>The kinds of files the user chose to index; read once per queued folder or file, not per file.</summary>
    private readonly Func<MediaTypeSelection> _mediaTypes;

    /// <summary>Where video indexing time goes (frame extraction vs. embedding); reset at the start of each run.</summary>
    public VideoIngestStats VideoStats { get; } = new();

    /// <summary>
    /// Where the embed and save time of this run goes (the P5 input); reset at the start of each run. Two timestamps
    /// per call, and its numbers are only ever reported next to <see cref="VideoStats"/>, never merged into it.
    /// </summary>
    public IngestionStageStats StageStats { get; } = new();
    private readonly Func<string> _cacheDirectoryProvider;

    /// <summary>The cache folder of the current profile; created on demand.</summary>
    private string _cacheDirectory
    {
        get
        {
            string dir = _cacheDirectoryProvider();
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>True while the queue is being processed; a profile must not be switched meanwhile.</summary>
    public bool IsRunning
    {
        get { lock (_stateLock) return _isRunning; }
    }

    private readonly Channel<string> _channel;
    private CancellationTokenSource? _workerCts;
    private Task? _processingTask;

    public event Action<IngestionProgress>? ProgressChanged;

    private int _totalFiles;
    private int _processedFiles;
    private int _skippedFiles;
    private int _excludedFiles;
    private int _quickFiles;
    private int _imageBatchCount; // image batches seen this run, used to pace the diagnostics log line

    private readonly IndexExclusionStore? _exclusions;

    // Files the embedding service returned nothing for (blank pages). Remembered so the next run does not re-embed
    // them, without putting them in the search index or in the excluded-files list (they are not failures).
    private readonly EmptyEmbeddingSkipStore _emptyEmbeddingSkips;

    private bool _isRunning;

    // Guards the "is the queue finished?" decision against producers that start concurrently.
    private readonly object _stateLock = new();
    private int _activeProducers;

    private static readonly HashSet<string> PdfExts = SupportedMediaTypes.PdfExtensions;
    private static readonly HashSet<string> ImageExts = SupportedMediaTypes.ImageExtensions;
    private static readonly HashSet<string> VideoExts = SupportedMediaTypes.VideoExtensions;
    private static readonly HashSet<string> OfficeExts = SupportedMediaTypes.OfficeExtensions;

    public IngestionPipelineWorker(
        IMediaRepository repository,
        IEmbeddingService embeddingService,
        string? cacheDirectory = null,
        IPdfPageExtractor? pdfExtractor = null,
        IVideoSegmentExtractor? videoExtractor = null,
        Func<string>? cacheDirectoryProvider = null,
        int videoParallelism = DefaultVideoParallelism,
        Func<int>? videoSegmentSeconds = null,
        IndexExclusionStore? exclusions = null,
        Func<bool>? videoAutoSegment = null,
        Func<MediaTypeSelection>? mediaTypes = null,
        EmptyEmbeddingSkipStore? emptyEmbeddingSkipsStore = null,
        Func<int>? videoParallelismProvider = null,
        Func<int>? ffmpegPermitProvider = null,
        Func<bool>? ignoreDecoderMarkers = null)
    {
        _mediaTypes = mediaTypes ?? (() => MediaTypeSelection.All);
        _exclusions = exclusions;
        _videoAutoSegment = videoAutoSegment ?? (() => false);
        _videoSegmentSeconds = videoSegmentSeconds ?? (() => DefaultVideoSegmentSeconds);
        _repository = repository;
        _embeddingService = embeddingService;
        _pdfIngestor = new PdfMediaIngestor(embeddingService, pdfExtractor);
        _officeIngestor = new OfficeMediaIngestor(embeddingService);
        _imageIngestor = new ImageMediaIngestor(embeddingService);
        _videoParallelismProvider = videoParallelismProvider;
        _ffmpegPermitProvider = ffmpegPermitProvider;
        _ignoreDecoderMarkers = ignoreDecoderMarkers;
        _videoIngestor = new VideoMediaIngestor(
            embeddingService, videoExtractor, VideoStats, Onnx.EmbeddingModelLocation.ModelTag(Onnx.EmbeddingModelLocation.DefaultPrecision),
            ignoreDecoderMarkers);
        _videoParallelism = Math.Clamp(videoParallelism, 1, DefaultVideoParallelism);
        _videoSlots = new SemaphoreSlim(_videoParallelism, _videoParallelism);
        string fixedCacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer",
            "cache"
        );
        _cacheDirectoryProvider = cacheDirectoryProvider ?? (() => fixedCacheDirectory);
        _emptyEmbeddingSkips = emptyEmbeddingSkipsStore ?? new EmptyEmbeddingSkipStore(DefaultEmptyEmbeddingSkipPath);
        Directory.CreateDirectory(_cacheDirectory);
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    }

    /// <summary>
    /// Fallback for callers that do not supply a store (tests, or a worker built outside the app's service provider).
    /// The skip memory lives next to the cache directory (which is per profile), not inside it: clearing the cache must
    /// not make the next run embed every blank image again.
    /// </summary>
    private string DefaultEmptyEmbeddingSkipPath()
    {
        string? directory = Path.GetDirectoryName(_cacheDirectoryProvider());
        if (string.IsNullOrEmpty(directory)) directory = AppDataPaths.RootDirectory;
        return Path.Combine(directory, "empty_embedding_skips.json");
    }

    public void Start()
    {
        lock (_stateLock)
        {
            StartLocked();
        }
    }

    private void StartLocked()
    {
        if (_isRunning) return;
        _totalFiles = 0;
        _processedFiles = 0;
        _skippedFiles = 0;
        _excludedFiles = 0;
        _quickFiles = 0;
        _imageBatchCount = 0;
        Interlocked.Exchange(ref _videoScenesCompleted, 0);
        lock (_videoCountLock) _videoProgress.Clear();
        VideoStats.Reset();
        StageStats.Reset();
        _knownHashes = null; // what is in the index is read again for each run
        _runWatch.Restart();
        ApplyRunDiagnosticsLocked();
        lock (_workLock)
        {
            _workStarted = false;
            _workWatch.Reset();
        }
        _isRunning = true;
        _workerCts = new CancellationTokenSource();
        var token = _workerCts.Token;
        _processingTask = Task.Run(() => ProcessQueueAsync(token));

        // If the queue loop itself faults, the run must not stay "running" forever (which would block profile switches
        // and future starts); the full exception is written to the trace log because nothing else observes this task.
        var processing = _processingTask;
        _ = processing.ContinueWith(t =>
        {
            if (t.Exception?.InnerException is OperationCanceledException) return;
            IngestTrace.Write("worker:queue-faulted", t.Exception);
            lock (_stateLock)
            {
                if (ReferenceEquals(_processingTask, processing)) _isRunning = false;
            }
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>
    /// Applies the diagnostics store to the knobs that must not change mid-run: the video slots and the FFmpeg gate.
    /// Called under <see cref="_stateLock"/> with no run active (StartLocked returns early while one is), so the old
    /// slots have no waiter and the old FFmpeg gate is idle.
    /// </summary>
    private void ApplyRunDiagnosticsLocked()
    {
        int wantedParallelism = Math.Clamp(
            _videoParallelismProvider?.Invoke() ?? _videoParallelism, 1, DefaultVideoParallelism);
        if (wantedParallelism != _videoParallelism || _videoSlots.CurrentCount != _videoParallelism)
        {
            var previous = _videoSlots;
            _videoSlots = new SemaphoreSlim(wantedParallelism, wantedParallelism);
            _videoParallelism = wantedParallelism;
            previous.Dispose(); // no run is active: nothing holds or waits on the old slots
        }

        // The gate is replaced only while idle (TryConfigureFfmpegPermit checks). FFmpeg sections read the property
        // once and release that same instance, so a swap at a run boundary is invisible to them.
        int wantedPermit = Math.Clamp(
            _ffmpegPermitProvider?.Invoke() ?? NativeDecodeGate.DefaultFfmpegPermit, 1, MaxFfmpegPermit);
        if (!NativeDecodeGate.TryConfigureFfmpegPermit(wantedPermit))
        {
            IngestTrace.Write($"worker:ffmpeg-permit-not-applied wanted={wantedPermit}; a decode is still in flight");
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        _workerCts?.Cancel();
        if (_processingTask != null)
        {
            try { await _processingTask; } catch { }
        }
        lock (_stateLock)
        {
            _isRunning = false;
        }
        while (_channel.Reader.TryRead(out _)) { } // drop work left over from the interrupted run
        NotifyProgress(Loc.T("Library.Stopped"));
    }
}
