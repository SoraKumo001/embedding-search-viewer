using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <param name="Items">Assets with their chunks, ready to be stored.</param>
/// <param name="SkippedWithoutEmbedding">Files the embedding service chose not to embed (almost single-colour images); they are not stored.</param>
/// <param name="Failed">Files that could not be read or embedded even after retries; they are not stored, so the next run tries them again.</param>
public sealed record ImageBatchResult(
    List<(MediaAsset Asset, List<MediaChunk> Chunks)> Items,
    List<string> SkippedWithoutEmbedding,
    List<ImageFailure>? Failed = null)
{
    public IReadOnlyList<ImageFailure> FailedFiles => Failed ?? (IReadOnlyList<ImageFailure>)Array.Empty<ImageFailure>();
}

public sealed record ImageFailure(string Path, string Error);

public class ImageMediaIngestor
{
    /// <summary>A file that cannot be opened is tried this many times in total before it is given up.</summary>
    internal const int MaxAttempts = 3;

    private readonly IEmbeddingService _embeddingService;
    private readonly TimeSpan _retryDelay;

    /// <param name="retryDelay">Pause before the second attempt (doubled for the third); a locked file is often free a moment later.</param>
    public ImageMediaIngestor(IEmbeddingService embeddingService, TimeSpan? retryDelay = null)
    {
        _embeddingService = embeddingService;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(300);
    }

    public async Task<(MediaAsset Asset, List<MediaChunk> Chunks)> IngestAsync(
        string filePath,
        string cacheDirectory,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var batch = await IngestBatchWithSkipsAsync(new[] { filePath }, cacheDirectory, truncateDim, cancellationToken);
        if (batch.Items.Count > 0)
        {
            return batch.Items[0];
        }
        if (batch.FailedFiles.Count > 0)
        {
            throw new IOException($"Could not read {filePath}: {batch.FailedFiles[0].Error}");
        }
        throw new FileNotFoundException($"File not found: {filePath}");
    }

    public async Task<List<(MediaAsset Asset, List<MediaChunk> Chunks)>> IngestBatchAsync(
        IEnumerable<string> filePaths,
        string cacheDirectory,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
        => (await IngestBatchWithSkipsAsync(filePaths, cacheDirectory, truncateDim, cancellationToken)).Items;

    public async Task<ImageBatchResult> IngestBatchWithSkipsAsync(
        IEnumerable<string> filePaths,
        string cacheDirectory,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var validFiles = new List<FileInfo>();
        foreach (var p in filePaths)
        {
            if (File.Exists(p))
            {
                validFiles.Add(new FileInfo(p));
            }
        }

        if (validFiles.Count == 0) return new ImageBatchResult(new(), new());

        var batchInputs = validFiles.Select(fi => new EmbeddingItemInput(Type: EmbeddingModalities.Image, Path: fi.FullName)).ToList();

        IReadOnlyList<float[]> embeddings;
        var failedIndexes = new Dictionary<int, string>();
        try
        {
            embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(batchInputs, truncateDim, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable file must not take the rest of the batch with it: go file by file, retrying briefly.
            embeddings = await EmbedOneByOneAsync(batchInputs, truncateDim, failedIndexes, ex, cancellationToken);
        }

        var output = new List<(MediaAsset Asset, List<MediaChunk> Chunks)>();
        var skipped = new List<string>();
        for (int i = 0; i < validFiles.Count; i++)
        {
            var fi = validFiles[i];
            if (failedIndexes.ContainsKey(i)) continue;
            var emb = (i < embeddings.Count) ? embeddings[i] : Array.Empty<float>();

            // No embedding (a blank page, or the service returned nothing): storing it would only add noise to every search.
            if (emb.Length == 0)
            {
                skipped.Add(fi.FullName);
                continue;
            }

            var asset = new MediaAsset
            {
                Id = Guid.NewGuid(),
                FilePath = fi.FullName,
                FileName = fi.Name,
                MediaType = MediaType.Image,
                FileSizeBytes = fi.Length,
                FileHash = MediaAsset.ComputeFileHash(fi),
                LastModifiedUtc = fi.LastWriteTimeUtc,
                ThumbnailPath = fi.FullName
            };

            var chunk = new MediaChunk
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                Modality = ModalityType.Image,
                ChunkIndex = 0,
                PreviewImagePath = fi.FullName,
                SnippetText = fi.Name,
                Embedding = emb
            };

            output.Add((asset, new List<MediaChunk> { chunk }));
        }

        var failed = failedIndexes
            .OrderBy(kv => kv.Key)
            .Select(kv => new ImageFailure(validFiles[kv.Key].FullName, kv.Value))
            .ToList();
        return new ImageBatchResult(output, skipped, failed);
    }

    /// <summary>
    /// Embeds the files one at a time after the whole batch failed. Errors that look transient (the file is locked or
    /// momentarily unreadable) are retried; whatever still fails is reported in <paramref name="failed"/> and left out.
    /// </summary>
    private async Task<IReadOnlyList<float[]>> EmbedOneByOneAsync(
        List<EmbeddingItemInput> inputs, int truncateDim, Dictionary<int, string> failed, Exception batchError, CancellationToken cancellationToken)
    {
        var results = new float[inputs.Count][];
        for (int i = 0; i < inputs.Count; i++)
        {
            results[i] = Array.Empty<float>();
            Exception? last = batchError;

            // The batch run counted as the first attempt for every file.
            for (int attempt = 2; attempt <= MaxAttempts + 1; attempt++)
            {
                if (attempt > 2)
                {
                    if (!IsTransient(last!)) break;
                    await Task.Delay(TimeSpan.FromTicks(_retryDelay.Ticks * (attempt - 2)), cancellationToken);
                }

                try
                {
                    var single = await _embeddingService.GenerateBatchEmbeddingsAsync(new[] { inputs[i] }, truncateDim, cancellationToken);
                    results[i] = single.Count > 0 ? single[0] : Array.Empty<float>();
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    last = ex;
                }
            }

            if (last != null) failed[i] = Describe(last);
        }
        return results;
    }

    private static bool IsTransient(Exception ex) =>
        ex is IOException or UnauthorizedAccessException ||
        (ex is AggregateException agg && agg.InnerExceptions.Any(IsTransient));

    private static string Describe(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: > 0 } agg) ex = agg.InnerExceptions[0];
        return ex is FileNotFoundException ? Loc.T("Ingest.FileOpenFailed") : ex.Message;
    }
}
