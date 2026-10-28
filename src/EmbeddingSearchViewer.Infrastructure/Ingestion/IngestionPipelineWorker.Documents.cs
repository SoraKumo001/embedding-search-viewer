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
            // The full exception (with the native stack) is what identifies a crash; the UI only gets the message.
            // A stopped run cancels the file in flight: that is expected, so it does not go to the error log.
            if (ex is not OperationCanceledException) IngestTrace.Video("worker-item-failed", filePath, null, 0, ex: ex);
            NotifyProgress(fileName, $"Error processing {fileName}: {ex.Message}");
        }
        finally
        {
            // The video leaves the in-flight map here; its scenes move to the completed total so the sum stays
            // monotonic for the scene speed. Only the run that actually removes the entry adds them (no double count).
            lock (_videoCountLock)
            {
                if (_videoProgress.TryRemove(filePath, out var finishedVideo))
                    Interlocked.Add(ref _videoScenesCompleted, finishedVideo.Done);
            }
            Interlocked.Increment(ref _processedFiles);
            NotifyProgress(fileName);
        }
    }

    private async Task ProcessSingleNonImageFileAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) return;

        var fi = new FileInfo(filePath);
        string fileHash = MediaAsset.ComputeFileHash(fi);

        bool unchanged = await IsUnchangedAsync(filePath, fileHash, cancellationToken);
        if (unchanged)
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
}
