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
    private void EnqueuePass(string directoryPath, EnumerationOptions options,
        MediaTypeSelection selection, CancellationToken cancellationToken, Func<string, bool> enqueue,
        HashSet<string>? queuedNew)
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
                            catch (OperationCanceledException)
                            {
                                // A stopped run cancels the videos in flight; that is not a failure.
                            }
                            catch (Exception ex)
                            {
                                // ProcessNonImageItemAsync reports failures itself; this only keeps an unexpected one
                                // (and the slot release) from escaping the task unnoticed.
                                IngestTrace.Video("worker-video-task-failed", videoPath, null, 0, ex: ex);
                            }
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
}
