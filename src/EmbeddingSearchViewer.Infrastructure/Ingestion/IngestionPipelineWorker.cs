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
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

public record IngestionProgress(
    int TotalFiles,
    int ProcessedFiles,
    string CurrentFileName,
    bool IsRunning,
    string? ErrorMessage = null,
    int SkippedFiles = 0,
    string? Summary = null
);

public class IngestionPipelineWorker
{
    private readonly IMediaRepository _repository;
    private readonly PdfMediaIngestor _pdfIngestor;
    private readonly ImageMediaIngestor _imageIngestor;
    private readonly VideoMediaIngestor _videoIngestor;

    /// <summary>Videos are decoded on the CPU and embedded on the GPU; several at once keep both busy.</summary>
    public const int DefaultVideoParallelism = 3;

    private readonly SemaphoreSlim _videoSlots;

    // Saves come from the queue loop and from videos running in parallel; they are written one at a time.
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Stopwatch _runWatch = new();

    /// <summary>Where video indexing time goes (frame extraction vs. embedding); reset at the start of each run.</summary>
    public VideoIngestStats VideoStats { get; } = new();
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
    private bool _isRunning;

    // Guards the "is the queue finished?" decision against producers that start concurrently.
    private readonly object _stateLock = new();
    private int _activeProducers;

    private static readonly HashSet<string> PdfExts = new(StringComparer.OrdinalIgnoreCase) { ".pdf" };
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
    private static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv" };

    public IngestionPipelineWorker(
        IMediaRepository repository,
        IEmbeddingService embeddingService,
        string? cacheDirectory = null,
        IPdfPageExtractor? pdfExtractor = null,
        IVideoSegmentExtractor? videoExtractor = null,
        Func<string>? cacheDirectoryProvider = null,
        int videoParallelism = DefaultVideoParallelism)
    {
        _repository = repository;
        _pdfIngestor = new PdfMediaIngestor(embeddingService, pdfExtractor);
        _imageIngestor = new ImageMediaIngestor(embeddingService);
        _videoIngestor = new VideoMediaIngestor(embeddingService, videoExtractor, VideoStats);
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
        VideoStats.Reset();
        _runWatch.Restart();
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
        NotifyProgress("中断されました");
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

    private static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path);
        return PdfExts.Contains(ext) || ImageExts.Contains(ext) || VideoExts.Contains(ext);
    }

    public async Task QueueDirectoryAsync(string directoryPath, bool recursive = true)
    {
        if (!Directory.Exists(directoryPath)) return;

        BeginProduce();
        try
        {
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*.*", opt))
            {
                if (!IsSupported(file)) continue;
                Interlocked.Increment(ref _totalFiles);
                await _channel.Writer.WriteAsync(file);
            }

            NotifyProgress($"キュー登録完了 ({_totalFiles} 件)");
        }
        finally
        {
            EndProduce();
        }
    }

    /// <summary>Queues a single file (e.g. reported by a folder watcher). Unchanged files are skipped by the ingestors.</summary>
    public async Task QueueFileAsync(string filePath)
    {
        if (!IsSupported(filePath) || !File.Exists(filePath)) return;

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

        // On normal completion _isRunning was already cleared under the lock (a new run may have started since).
        if (cancellationToken.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                _isRunning = false;
            }
        }
        NotifyProgress("すべてのインデックス作成が完了しました", summary: VideoStats.Summary(_runWatch.Elapsed.TotalSeconds));
    }

    /// <summary>Indexes one PDF or video and reports progress; a failure is reported, not thrown.</summary>
    private async Task ProcessNonImageItemAsync(string filePath, CancellationToken cancellationToken)
    {
        string fileName = Path.GetFileName(filePath);
        NotifyProgress($"解析中: {fileName}");
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
            Interlocked.Increment(ref _processedFiles);
            NotifyProgress(fileName);
        }
    }

    private async Task SaveAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            await _repository.SaveAssetWithChunksAsync(asset, chunks, cancellationToken);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task ProcessImageBatchAsync(List<string> filePaths, CancellationToken cancellationToken)
    {
        if (filePaths.Count == 0) return;

        var toProcess = new List<string>();
        foreach (var p in filePaths)
        {
            if (!File.Exists(p)) continue;
            var fi = new FileInfo(p);
            string fileHash = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";

            var existing = await _repository.GetAssetByPathAsync(p, cancellationToken);
            if (existing == null || existing.FileHash != fileHash)
            {
                toProcess.Add(p);
            }
            else
            {
                Interlocked.Increment(ref _processedFiles);
                NotifyProgress(Path.GetFileName(p));
            }
        }

        if (toProcess.Count > 0)
        {
            string currentName = Path.GetFileName(toProcess.First());
            NotifyProgress($"ベクトル計算中: {currentName}");

            try
            {
                var batch = await _imageIngestor.IngestBatchWithSkipsAsync(toProcess, _cacheDirectory, cancellationToken: cancellationToken);
                foreach (var (asset, chunks) in batch.Items)
                {
                    await SaveAsync(asset, chunks, cancellationToken);
                    Interlocked.Increment(ref _processedFiles);
                    NotifyProgress(asset.FileName);
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
    }

    private async Task ProcessSingleNonImageFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return;

        var fi = new FileInfo(filePath);
        string fileHash = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";

        var existing = await _repository.GetAssetByPathAsync(filePath, cancellationToken);
        if (existing != null && existing.FileHash == fileHash)
        {
            return;
        }

        string ext = Path.GetExtension(filePath);
        MediaAsset asset;
        List<MediaChunk> chunks;

        if (PdfExts.Contains(ext))
        {
            (asset, chunks) = await _pdfIngestor.IngestAsync(filePath, _cacheDirectory, cancellationToken: cancellationToken);
        }
        else if (VideoExts.Contains(ext))
        {
            (asset, chunks) = await _videoIngestor.IngestAsync(filePath, _cacheDirectory, cancellationToken: cancellationToken);
        }
        else
        {
            return;
        }

        await SaveAsync(asset, chunks, cancellationToken);
    }

    private void NotifyProgress(string currentFile, string? error = null, string? summary = null)
    {
        ProgressChanged?.Invoke(new IngestionProgress(
            TotalFiles: _totalFiles,
            ProcessedFiles: _processedFiles,
            CurrentFileName: currentFile,
            IsRunning: _isRunning,
            ErrorMessage: error,
            SkippedFiles: _skippedFiles,
            Summary: summary
        ));
    }
}
