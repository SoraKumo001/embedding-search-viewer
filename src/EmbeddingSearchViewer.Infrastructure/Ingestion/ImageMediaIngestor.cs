using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Sidecar;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <param name="Items">Assets with their chunks, ready to be stored.</param>
/// <param name="SkippedWithoutEmbedding">Files the embedding service chose not to embed (almost single-colour images); they are not stored.</param>
public sealed record ImageBatchResult(
    List<(MediaAsset Asset, List<MediaChunk> Chunks)> Items,
    List<string> SkippedWithoutEmbedding);

public class ImageMediaIngestor
{
    private readonly IEmbeddingService _embeddingService;

    public ImageMediaIngestor(IEmbeddingService embeddingService)
    {
        _embeddingService = embeddingService;
    }

    public async Task<(MediaAsset Asset, List<MediaChunk> Chunks)> IngestAsync(
        string filePath,
        string cacheDirectory,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var results = await IngestBatchAsync(new[] { filePath }, cacheDirectory, truncateDim, cancellationToken);
        if (results.Count > 0)
        {
            return results[0];
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

        var batchInputs = validFiles.Select(fi => new EmbeddingItemInput(Type: "image", Path: fi.FullName)).ToList();
        var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(batchInputs, truncateDim, cancellationToken);

        var output = new List<(MediaAsset Asset, List<MediaChunk> Chunks)>();
        var skipped = new List<string>();
        for (int i = 0; i < validFiles.Count; i++)
        {
            var fi = validFiles[i];
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
                FileHash = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}",
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

        return new ImageBatchResult(output, skipped);
    }
}
