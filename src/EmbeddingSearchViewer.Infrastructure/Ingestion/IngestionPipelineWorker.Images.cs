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

            // A blank image the embedding service returned nothing for last time (and that has not changed since) is
            // not embedded again. It was never written to the index, so without this memory it would be re-embedded on
            // every run (CPU only, no GPU work): the cause of the "[StageStats] embed ... (0 assets)" repeats.
            if (_emptyEmbeddingSkips.IsSkipped(p, fi.Length, fi.LastWriteTimeUtc.Ticks))
            {
                Interlocked.Increment(ref _processedFiles);
                Interlocked.Increment(ref _skippedFiles);
                Interlocked.Increment(ref _quickFiles);
                NotifyProgress(Path.GetFileName(p));
                continue;
            }
            _emptyEmbeddingSkips.Remove(p); // changed since it was remembered: forget the stale entry and embed it again

            bool unchanged = await IsUnchangedAsync(p, fileHash, cancellationToken);
            if (!unchanged)
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
                long embedElapsed = Stopwatch.GetTimestamp() - embedStart;
                StageStats.AddEmbed(embedElapsed, batch.Items.Count);

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

                // Skipped files (blank pages) are done as far as progress is concerned, but are not stored in the index.
                // Remember them (with their current size and modification time) so the next run skips them instead of
                // embedding them again; a changed file no longer matches and is retried.
                if (batch.SkippedWithoutEmbedding.Count > 0)
                {
                    Interlocked.Add(ref _processedFiles, batch.SkippedWithoutEmbedding.Count);
                    Interlocked.Add(ref _skippedFiles, batch.SkippedWithoutEmbedding.Count);
                    RememberSkippedWithoutEmbedding(batch.SkippedWithoutEmbedding);
                    NotifyProgress(Path.GetFileName(batch.SkippedWithoutEmbedding[^1]));
                }
            }
            catch (Exception ex)
            {
                IngestTrace.Write($"worker:image-batch-failed files={toProcess.Count} first={toProcess.FirstOrDefault()}", ex);
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

    private void RememberSkippedWithoutEmbedding(IReadOnlyList<string> paths)
    {
        var entries = new List<SkippedEmbeddingFile>(paths.Count);
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                entries.Add(new SkippedEmbeddingFile(path, info.Length, info.LastWriteTimeUtc.Ticks, DateTime.UtcNow));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Not being able to look at the file again is no reason to fail the run.
            }
        }
        if (entries.Count > 0) _emptyEmbeddingSkips.AddRange(entries);
    }
}
