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

public partial class IngestionPipelineWorker
{
    /// <summary>
    /// Time since the first file that needed work started. Unlike <see cref="_runWatch"/> (the whole run:
    /// folder enumeration, hash lookups and already-indexed checks included), this matches the
    /// <see cref="IngestionEta"/> basis, which only counts files that needed work. Started lazily by
    /// <see cref="EnsureWorkStarted"/>; never started when a run has nothing to do.
    /// </summary>
    private readonly Stopwatch _workWatch = new();
    private readonly object _workLock = new();
    private bool _workStarted;

    private readonly ConcurrentDictionary<string, (int Done, int Total)> _videoProgress = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Scenes of videos that are finished, summed over the run. A finished video leaves <see cref="_videoProgress"/>,
    /// so its scenes are added here (once) to keep <see cref="IngestionProgress.VideoDoneScenes"/> monotonic.
    /// </summary>
    private long _videoScenesCompleted;

    // Keeps "move the finished video's scenes to the completed total" and the snapshot taken for a progress report
    // from interleaving: otherwise a report could miss the video in both counts and the scene count would dip.
    private readonly object _videoCountLock = new();

    private long _lastVideoNotifyTicks;

    /// <summary>Seconds between two notifications that only report how far a video has come.</summary>
    private const double VideoProgressIntervalSeconds = 0.5;

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

        double inFlightFiles;
        double videoDoneScenes;
        double videoRemainingScenes;
        lock (_videoCountLock)
        {
            inFlightFiles = _videoProgress.Values.Sum(p => p.Total > 0 ? Math.Min(1.0, (double)p.Done / p.Total) : 0);
            videoDoneScenes = Volatile.Read(ref _videoScenesCompleted) + _videoProgress.Values.Sum(p => (double)p.Done);
            videoRemainingScenes = _videoProgress.Values.Sum(p => (double)Math.Max(0, p.Total - p.Done));
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
            InFlightFiles: inFlightFiles,
            VideoDoneScenes: videoDoneScenes,
            VideoRemainingScenes: videoRemainingScenes
        ));
    }
}
