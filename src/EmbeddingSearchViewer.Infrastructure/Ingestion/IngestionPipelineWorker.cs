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
    int QuickFiles = 0 // of the processed files, those that needed no work (already indexed, or left out): they say nothing about the speed
);

public class IngestionPipelineWorker
{
    private readonly IMediaRepository _repository;
    private readonly IEmbeddingService _embeddingService;
    private readonly PdfMediaIngestor _pdfIngestor;
    private readonly OfficeMediaIngestor _officeIngestor;
    private readonly ImageMediaIngestor _imageIngestor;
    private readonly VideoMediaIngestor _videoIngestor;

    /// <summary>Videos are decoded on the CPU and embedded on the GPU; several at once keep both busy.</summary>
    public const int DefaultVideoParallelism = 3;

    private readonly SemaphoreSlim _videoSlots;

    // Saves come from the queue loop and from videos running in parallel; they are written one at a time.
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Stopwatch _runWatch = new();

    /// <summary>
    /// Time since the first file that needed work started. Unlike <see cref="_runWatch"/> (the whole run:
    /// folder enumeration, hash lookups and already-indexed checks included), this matches the
    /// <see cref="IngestionEta"/> basis, which only counts files that needed work. Started lazily by
    /// <see cref="EnsureWorkStarted"/>; never started when a run has nothing to do.
    /// </summary>
    private readonly Stopwatch _workWatch = new();
    private readonly object _workLock = new();
    private bool _workStarted;

    private readonly Func<int> _videoSegmentSeconds;
    private readonly ConcurrentDictionary<string, (int Done, int Total)> _videoProgress = new(StringComparer.OrdinalIgnoreCase);
    private long _lastVideoNotifyTicks;

    /// <summary>Seconds between two notifications that only report how far a video has come.</summary>
    private const double VideoProgressIntervalSeconds = 0.5;

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
        Func<MediaTypeSelection>? mediaTypes = null)
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
        _videoIngestor = new VideoMediaIngestor(
            embeddingService, videoExtractor, VideoStats, Onnx.EmbeddingModelLocation.ModelTag(Onnx.EmbeddingModelLocation.DefaultPrecision));
        _videoSlots = new SemaphoreSlim(Math.Max(1, videoParallelism));
        string fixedCacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer",
            "cache"
        );
        _cacheDirectoryProvider = cacheDirectoryProvider ?? (() => fixedCacheDirectory);
        Directory.CreateDirectory(_cacheDirectory);
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
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
        VideoStats.Reset();
        StageStats.Reset();
        _knownHashes = null; // what is in the index is read again for each run
        _runWatch.Restart();
        lock (_workLock)
        {
            _workStarted = false;
            _workWatch.Reset();
        }
        _isRunning = true;
        _workerCts = new CancellationTokenSource();
        var token = _workerCts.Token;
        _processingTask = Task.Run(() => ProcessQueueAsync(token));
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

    /// <summary>Marks a producer as active and makes sure the consumer is running.</summary>
    private void BeginProduce()
    {
        lock (_stateLock)
        {
            _activeProducers++;
            StartLocked();
        }
    }

    /// <summary>Marks a producer as finished and wakes the consumer so it can decide whether the queue is done.</summary>
    private void EndProduce()
    {
        lock (_stateLock)
        {
            _activeProducers--;
        }
        _channel.Writer.TryWrite(string.Empty);
    }

    public async Task QueueDirectoryAsync(string directoryPath, bool recursive = true)
    {
        if (!Directory.Exists(directoryPath)) return;

        var selection = _mediaTypes();
        BeginProduce();
        var cancellationToken = _workerCts?.Token ?? CancellationToken.None; // "stop" also ends the search for files
        try
        {
            // Walking a big folder tree takes a long time and the caller is usually the UI thread, so it is done on
            // another thread. Folders that cannot be read are skipped instead of ending the whole search.
            var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true, AttributesToSkip = 0 };
            string? failure = await Task.Run(() =>
            {
                try
                {
                    // Files that are not in the index yet go first: on a big library the re-check of every known file
                    // (even though each check is fast) would otherwise delay the files the user is waiting for.
                    // The first pass streams the unknown files straight into the queue; the second pass re-checks the
                    // rest. Only the new files of this call are remembered (a handful), never the whole tree.
                    var known = TryLoadKnownPaths(cancellationToken);
                    var queuedNew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (known != null)
                    {
                        EnqueuePass(directoryPath, options, selection, cancellationToken,
                            f => !known.ContainsKey(f), queuedNew);
                        if (cancellationToken.IsCancellationRequested) return null;
                        EnqueuePass(directoryPath, options, selection, cancellationToken,
                            f => known.ContainsKey(f) || !queuedNew.Contains(f), null);
                    }
                    else
                    {
                        // The index could not be read: fall back to the old single pass (everything is re-checked).
                        EnqueuePass(directoryPath, options, selection, cancellationToken, _ => true, null);
                    }
                    return null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // A folder that disappears or a drive that goes away halfway must not end the app (or the other
                    // folders): what was found so far is still indexed.
                    return ex.Message;
                }
            }, CancellationToken.None);

            if (failure != null) NotifyProgress(Path.GetFileName(directoryPath), $"{directoryPath}: {failure}");
            else NotifyProgress(Loc.T("Ingest.Queued", _totalFiles));
        }
        finally
        {
            EndProduce();
        }
    }

    /// <summary>
    /// The paths already in the index at the start of this run, or null when the index could not be read
    /// (the caller then queues everything). Cancellation is reported by returning null as well: the run is
    /// stopping anyway, so there is nothing to prioritize.
    /// </summary>
    private ConcurrentDictionary<string, string>? TryLoadKnownPaths(CancellationToken cancellationToken)
    {
        try
        {
            return KnownHashesAsync().WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or System.Data.Common.DbException)
        {
            return null;
        }
    }

    /// <summary>
    /// One walk over the folder tree, queueing every selected file that <paramref name="enqueue"/> accepts.
    /// Accepted new files are recorded in <paramref name="queuedNew"/> when it is not null.
    /// </summary>
    private void EnqueuePass(string directoryPath, EnumerationOptions options, MediaTypeSelection selection,
        CancellationToken cancellationToken, Func<string, bool> enqueue, HashSet<string>? queuedNew)
    {
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", options))
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (!SupportedMediaTypes.IsSelected(file, selection)) continue;
            if (!enqueue(file)) continue;
            queuedNew?.Add(file);
            Interlocked.Increment(ref _totalFiles);
            _channel.Writer.TryWrite(file); // unbounded: always succeeds
        }
    }

    /// <summary>Queues a single file (e.g. reported by a folder watcher). Unchanged files are skipped by the ingestors.</summary>
    public async Task QueueFileAsync(string filePath)
    {
        if (!SupportedMediaTypes.IsSelected(filePath, _mediaTypes()) || !File.Exists(filePath)) return;

        BeginProduce();
        try
        {
            Interlocked.Increment(ref _totalFiles);
            await _channel.Writer.WriteAsync(filePath);
        }
        finally
        {
            EndProduce();
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        var inFlightVideos = new List<Task>();

        // The vec0 insert trigger is suspended for the run and the vectors are back-filled in one pass at the end:
        // maintaining the derived index one chunk at a time during ingestion is the expensive part. The delete and
        // full-text triggers stay in place, so removals and search stay consistent throughout.
        try { await _repository.BeginBulkIngestAsync(cancellationToken); } catch { }

        while (!cancellationToken.IsCancellationRequested)
        {
            inFlightVideos.RemoveAll(t => t.IsCompleted);

            if (!_channel.Reader.TryPeek(out _))
            {
                bool finished = false;
                lock (_stateLock)
                {
                    if (_activeProducers == 0 && inFlightVideos.Count == 0 && !_channel.Reader.TryPeek(out _))
                    {
                        _isRunning = false;
                        finished = true;
                    }
                }
                if (finished) break;

                try
                {
                    if (inFlightVideos.Count > 0)
                    {
                        // Videos are still being processed: wake up when one finishes, and look at the queue now and then.
                        await Task.WhenAny(Task.WhenAny(inFlightVideos), Task.Delay(500, cancellationToken));
                    }
                    else if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            // Drain batch of items
            var imageBatch = new List<string>();

            while (_channel.Reader.TryRead(out var filePath))
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (string.IsNullOrEmpty(filePath)) continue; // wake-up marker

                string ext = Path.GetExtension(filePath);
                if (ImageExts.Contains(ext))
                {
                    imageBatch.Add(filePath);
                    // Process images in GPU-optimized batches of 16 while maintaining real-time UI feedback
                    if (imageBatch.Count >= 16)
                    {
                        await ProcessImageBatchAsync(imageBatch, cancellationToken);
                        imageBatch.Clear();
                    }
                }
                else
                {
                    // Flush pending images before non-image item
                    if (imageBatch.Count > 0)
                    {
                        await ProcessImageBatchAsync(imageBatch, cancellationToken);
                        imageBatch.Clear();
                    }

                    if (VideoExts.Contains(ext))
                    {
                        // Wait for a free slot, then let the video run while the queue moves on.
                        try
                        {
                            await _videoSlots.WaitAsync(cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        string videoPath = filePath;
                        inFlightVideos.Add(Task.Run(async () =>
                        {
                            try { await ProcessNonImageItemAsync(videoPath, cancellationToken); }
                            finally { _videoSlots.Release(); }
                        }, CancellationToken.None));
                    }
                    else
                    {
                        await ProcessNonImageItemAsync(filePath, cancellationToken);
                    }
                }
            }

            // Flush remaining images
            if (imageBatch.Count > 0)
            {
                await ProcessImageBatchAsync(imageBatch, cancellationToken);
                imageBatch.Clear();
            }
        }

        // Videos still running (only after a cancellation) must be done before the run counts as stopped.
        try { await Task.WhenAll(inFlightVideos); } catch { }

        // Bring the suspended vec0 index up to date in one pass (a no-op when the extension is unavailable or the run
        // was cancelled; the next start repairs it, since InitializeAsync also back-fills).
        try { await _repository.EndBulkIngestAsync(cancellationToken); } catch { }

        // On normal completion _isRunning was already cleared under the lock (a new run may have started since).
        if (cancellationToken.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                _isRunning = false;
            }
        }
        NotifyProgress(Loc.T("Ingest.AllDone"), summary: BuildCompletionSummary());

        // Same console as the [EmbeddingStats] lines: one line per run with the embed/save split, so a 10k run can be
        // measured without a debugger (manual procedure on IngestionStageStats). The shape is language-independent.
        if (StageStats.HasSamples) Console.WriteLine($"[StageStats] {StageStats.Describe()}");
    }

    /// <summary>
    /// The line shown when a run ends. The video totals are exactly what they were; the embed/save probe is appended
    /// as its own clause (and stands alone when no video was processed), so neither set of numbers changes meaning.
    /// </summary>
    private string BuildCompletionSummary()
    {
        string video = VideoStats.Summary(WallSeconds, ActiveSeconds);
        string stages = StageStats.Summary();
        if (stages.Length == 0) return video;
        return video.Length == 0 ? stages : video + stages;
    }

    /// <summary>Whole run including enumeration and already-indexed checks (the 6th arg of Ingest.VideoStats, unchanged).</summary>
    private double WallSeconds => _runWatch.Elapsed.TotalSeconds;

    /// <summary>
    /// Time since the first file that needed work started; 0 when nothing needed work.
    /// Same basis as <see cref="IngestionEta"/> (files counted in QuickFiles excluded).
    /// </summary>
    private double ActiveSeconds
    {
        get { lock (_workLock) return _workStarted ? _workWatch.Elapsed.TotalSeconds : 0; }
    }

    /// <summary>Starts the real-work clock once, at the first file that is not skipped as already indexed.</summary>
    private void EnsureWorkStarted()
    {
        lock (_workLock)
        {
            if (_workStarted) return;
            _workStarted = true;
            _workWatch.Restart();
        }
    }

    /// <summary>Indexes one PDF or video and reports progress; a failure is reported, not thrown.</summary>
    private async Task ProcessNonImageItemAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        NotifyProgress(Loc.T("Ingest.Analyzing", fileName));
        try
        {
            await ProcessSingleNonImageFileAsync(filePath, cancellationToken);
        }
        catch (Exception ex)
        {
            NotifyProgress(fileName, $"Error processing {fileName}: {ex.Message}");
        }
        finally
        {
            _videoProgress.TryRemove(filePath, out _);
            Interlocked.Increment(ref _processedFiles);
            NotifyProgress(fileName);
        }
    }

    private void OnVideoProgress(string filePath, int done, int total)
    {
        _videoProgress[filePath] = (done, total);

        // Segments come several per second; the screen only needs to follow a few times a second.
        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastVideoNotifyTicks);
        bool finished = done >= total;
        if (!finished && (now - last) < VideoProgressIntervalSeconds * Stopwatch.Frequency) return;
        if (!finished && Interlocked.CompareExchange(ref _lastVideoNotifyTicks, now, last) != last) return;

        NotifyProgress(Loc.T("Ingest.Analyzing", Path.GetFileName(filePath)));
    }

    /// <summary>One line per video that is being processed: how many of its segments are done.</summary>
    private string? BuildVideoDetail()
    {
        var active = _videoProgress.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (active.Count == 0) return null;

        var lines = active.Take(4).Select(kv =>
        {
            var (done, total) = kv.Value;
            int percent = total > 0 ? done * 100 / total : 0;
            return Loc.T("Ingest.VideoProgress", Path.GetFileName(kv.Key), done, total, percent);
        });
        string text = string.Join("\n", lines);
        return active.Count > 4 ? text + "\n" + Loc.T("Ingest.VideoProgress.More", active.Count - 4) : text;
    }

    private async Task SaveAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            await _repository.SaveAssetWithChunksAsync(asset, chunks, cancellationToken);
            if (_knownHashes is { IsCompletedSuccessfully: true } loaded) loaded.Result[asset.FilePath] = asset.FileHash;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Stores a batch of assets in as few transactions as possible. The repository isolates a single failing asset, so
    /// the rest of the batch still lands; this returns which ones failed (empty when all were stored).
    /// </summary>
    private async Task<IReadOnlyList<AssetSaveFailure>> SaveBatchAsync(IReadOnlyList<AssetChunkBatch> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return Array.Empty<AssetSaveFailure>();

        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var failures = await _repository.SaveAssetsWithChunksAsync(items, cancellationToken);

            // Only the assets that actually landed belong in the hash cache; a failed one is retried on the next run.
            if (_knownHashes is { IsCompletedSuccessfully: true } loaded)
            {
                var failedIds = failures.Count > 0 ? new HashSet<Guid>(failures.Select(f => f.AssetId)) : null;
                foreach (var item in items)
                {
                    if (failedIds != null && failedIds.Contains(item.Asset.Id)) continue;
                    loaded.Result[item.Asset.FilePath] = item.Asset.FileHash;
                }
            }
            return failures;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // The hash of every file that was indexed before this run (plus those saved during it), read with one query. Checking
    // each file with its own query takes minutes for a library of hundreds of thousands of files.
    private Task<ConcurrentDictionary<string, string>>? _knownHashes;

    private Task<ConcurrentDictionary<string, string>> KnownHashesAsync()
    {
        lock (_stateLock)
        {
            if (_knownHashes is { IsFaulted: true } or { IsCanceled: true }) _knownHashes = null; // try again
            return _knownHashes ??= Task.Run(async () =>
                new ConcurrentDictionary<string, string>(await _repository.GetAssetHashesAsync(), StringComparer.Ordinal));
        }
    }

    /// <summary>True when the file is in the index with the same size and modification time.</summary>
    private async Task<bool> IsUnchangedAsync(string filePath, string fileHash, CancellationToken cancellationToken)
    {
        var known = await KnownHashesAsync().WaitAsync(cancellationToken);
        return known.TryGetValue(filePath, out var indexed) && indexed == fileHash;
    }

    private async Task ProcessImageBatchAsync(List<string> filePaths, CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0) return;

        int batchNumber = Interlocked.Increment(ref _imageBatchCount);

        var toProcess = new List<string>();
        foreach (var p in filePaths)
        {
            FileInfo fi;
            string fileHash;
            try
            {
                if (!File.Exists(p))
                {
                    Interlocked.Increment(ref _processedFiles); // gone since the search: nothing to do, but it was counted
                    continue;
                }
                fi = new FileInfo(p);
                fileHash = MediaAsset.ComputeFileHash(fi);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Interlocked.Increment(ref _processedFiles); // a file that cannot even be looked at is left out of this run
                continue;
            }

            // A file that failed on every retry in an earlier run (and has not changed since) is left out.
            if (_exclusions != null && _exclusions.IsExcluded(p, fi.Length, fi.LastWriteTimeUtc.Ticks))
            {
                Interlocked.Increment(ref _processedFiles);
                Interlocked.Increment(ref _excludedFiles);
                Interlocked.Increment(ref _quickFiles);
                NotifyProgress(Path.GetFileName(p));
                continue;
            }

            if (!await IsUnchangedAsync(p, fileHash, cancellationToken))
            {
                toProcess.Add(p);
            }
            else
            {
                Interlocked.Increment(ref _processedFiles);
                Interlocked.Increment(ref _quickFiles);
                NotifyProgress(Path.GetFileName(p));
            }
        }

        if (toProcess.Count > 0)
        {
            EnsureWorkStarted(); // first file that needs work: folder enumeration and skipped checks stay out of the active time
            string currentName = Path.GetFileName(toProcess.First());
            NotifyProgress(Loc.T("Ingest.Embedding", currentName));

            try
            {
                long embedStart = Stopwatch.GetTimestamp();
                var batch = await _imageIngestor.IngestBatchWithSkipsAsync(toProcess, _cacheDirectory, cancellationToken: cancellationToken);
                StageStats.AddEmbed(Stopwatch.GetTimestamp() - embedStart, batch.Items.Count);

                // Save the whole batch in as few transactions as possible; one failing asset is isolated by the
                // repository, so its neighbours still land.
                var items = batch.Items.Select(i => new AssetChunkBatch(i.Asset, i.Chunks)).ToList();
                long saveStart = Stopwatch.GetTimestamp();
                var failures = await SaveBatchAsync(items, cancellationToken);
                StageStats.AddSave(Stopwatch.GetTimestamp() - saveStart, items.Count);
                Dictionary<Guid, string>? failed = null;
                foreach (var f in failures) (failed ??= new())[f.AssetId] = f.Error;

                foreach (var (asset, _) in batch.Items)
                {
                    Interlocked.Increment(ref _processedFiles);
                    NotifyProgress(asset.FileName,
                        failed != null && failed.TryGetValue(asset.Id, out var error)
                            ? $"Error processing {asset.FileName}: {error}"
                            : null);
                }

                // Files that could not be read even after the retries are most likely damaged: leave them out of the
                // index, remember them so that the next run does not try them again, and say which ones.
                if (batch.FailedFiles.Count > 0)
                {
                    Interlocked.Add(ref _processedFiles, batch.FailedFiles.Count);
                    ExcludeFailedFiles(batch.FailedFiles);
                    NotifyProgress(currentName, DescribeFailures(batch.FailedFiles, willExclude: _exclusions != null));
                }

                // Skipped files (blank pages) are done as far as progress is concerned, but are not stored.
                if (batch.SkippedWithoutEmbedding.Count > 0)
                {
                    Interlocked.Add(ref _processedFiles, batch.SkippedWithoutEmbedding.Count);
                    Interlocked.Add(ref _skippedFiles, batch.SkippedWithoutEmbedding.Count);
                    NotifyProgress(Path.GetFileName(batch.SkippedWithoutEmbedding[^1]));
                }
            }
            catch (Exception ex)
            {
                NotifyProgress(currentName, $"Batch error: {ex.Message}");
                // Fallback: increment processed counter
                Interlocked.Add(ref _processedFiles, toProcess.Count);
                NotifyProgress(currentName);
            }
        }
        else if (filePaths.Count > 0)
        {
            NotifyProgress(Path.GetFileName(filePaths.Last()));
        }

        if (batchNumber % EmbeddingStatsLogEveryBatches == 0)
        {
            LogEmbeddingStats(batchNumber);
        }
    }

    /// <summary>Every this many image batches, one diagnostics line with the embedding stage timings is logged.</summary>
    internal const int EmbeddingStatsLogEveryBatches = 10;

    /// <summary>
    /// Diagnostic only: prints where the time of image embedding goes. Reads the timings from the ONNX engine when it is
    /// the active one, and does nothing otherwise (test doubles and the unavailable engine carry no stats).
    /// </summary>
    private void LogEmbeddingStats(int batchNumber)
    {
        string? line = _embeddingService switch
        {
            OnnxDirectMlEmbeddingService onnx => onnx.DescribeStats(),
            EmbeddingServiceHost host => host.DescribeEmbeddingStats(),
            _ => null
        };
        if (line != null) Console.WriteLine($"[EmbeddingStats] after image batch {batchNumber}: {line}");
    }

    /// <summary>Names the first few files that were left out (file names only, to keep the message short), then counts the rest.</summary>
    internal static string DescribeFailures(IReadOnlyList<ImageFailure> failures, bool willExclude = false)
    {
        const int Shown = 3;
        var parts = failures.Take(Shown).Select(f => $"{Path.GetFileName(f.Path)} ({f.Error})");
        string text = Loc.T("Ingest.Failed.Files", string.Join(Loc.T("Common.ListSeparator"), parts));
        if (failures.Count > Shown) text += Loc.T("Ingest.Failed.More", failures.Count - Shown);
        return willExclude
            ? text + Loc.T("Ingest.Failed.WillExclude")
            : text + Loc.T("Ingest.Failed.RetryNext");
    }

    private void ExcludeFailedFiles(IReadOnlyList<ImageFailure> failures)
    {
        if (_exclusions == null) return;

        foreach (var failure in failures)
        {
            try
            {
                var info = new FileInfo(failure.Path);
                if (!info.Exists) continue;
                _exclusions.Add(new ExcludedFile(failure.Path, info.Length, info.LastWriteTimeUtc.Ticks, failure.Error, DateTime.UtcNow));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not being able to look at the file again is no reason to fail the run.
            }
        }
    }

    private async Task ProcessSingleNonImageFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return;

        var fi = new FileInfo(filePath);
        string fileHash = MediaAsset.ComputeFileHash(fi);

        if (await IsUnchangedAsync(filePath, fileHash, cancellationToken))
        {
            Interlocked.Increment(ref _quickFiles);
            return;
        }

        string ext = Path.GetExtension(filePath);
        MediaAsset asset;
        List<MediaChunk> chunks;

        if (!PdfExts.Contains(ext) && !OfficeExts.Contains(ext) && !VideoExts.Contains(ext))
        {
            return; // not a kind this worker handles: no work done, so the active clock stays stopped
        }
        EnsureWorkStarted(); // first non-skipped file: enumeration and QuickFiles checks stay out of the active time

        if (PdfExts.Contains(ext))
        {
            (asset, chunks) = await _pdfIngestor.IngestAsync(filePath, _cacheDirectory, cancellationToken: cancellationToken);
        }
        else if (OfficeExts.Contains(ext))
        {
            (asset, chunks) = await _officeIngestor.IngestAsync(filePath, cancellationToken: cancellationToken);
        }
        else if (VideoExts.Contains(ext))
        {
            // Overlap of 3 s as before; shorter segments get less so that the step stays positive.
            int segmentSeconds = Math.Clamp(_videoSegmentSeconds(), 5, 300);
            int overlapSeconds = Math.Min(3, segmentSeconds / 4);
            (asset, chunks) = await _videoIngestor.IngestAsync(
                filePath, _cacheDirectory, segmentSeconds, overlapSeconds,
                onProgress: (done, total) => OnVideoProgress(filePath, done, total),
                cancellationToken: cancellationToken,
                maxSegments: _videoAutoSegment() ? AutoMaxVideoSegments : 0);
        }
        else
        {
            return; // unreachable: unknown kinds returned above, kept so a future kind cannot silently embed nothing
        }

        long saveStart = Stopwatch.GetTimestamp();
        await SaveAsync(asset, chunks, cancellationToken);
        StageStats.AddSave(Stopwatch.GetTimestamp() - saveStart, 1); // the non-image side of the probe (video/PDF/Office)
    }

    /// <summary>Routine progress reports are sent at most this often (seconds); errors and the final report always are.</summary>
    private const double ProgressIntervalSeconds = 0.1;

    private long _lastProgressTicks;

    private void NotifyProgress(string currentFile, string? error = null, string? summary = null)
    {
        // Files that are already indexed are checked at thousands per second. One report per file would flood the UI
        // thread, which handles each of them, and the window would stop responding. The screen only needs a few a second.
        if (error == null && summary == null && _isRunning)
        {
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastProgressTicks);
            if (now - last < ProgressIntervalSeconds * Stopwatch.Frequency) return;
            if (Interlocked.CompareExchange(ref _lastProgressTicks, now, last) != last) return;
        }

        ProgressChanged?.Invoke(new IngestionProgress(
            TotalFiles: _totalFiles,
            ProcessedFiles: _processedFiles,
            CurrentFileName: currentFile,
            IsRunning: _isRunning,
            ErrorMessage: error,
            SkippedFiles: _skippedFiles,
            Summary: summary,
            Detail: BuildVideoDetail(),
            ExcludedFiles: _excludedFiles,
            QuickFiles: Volatile.Read(ref _quickFiles),
            InFlightFiles: _videoProgress.Values.Sum(p => p.Total > 0 ? Math.Min(1.0, (double)p.Done / p.Total) : 0)
        ));
    }
}
