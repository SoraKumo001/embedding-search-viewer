using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>Indexes the text of Word, PowerPoint and Excel files. One text chunk per slide, sheet part or run of paragraphs.</summary>
public class OfficeMediaIngestor
{
    private const int EmbedBatchSize = 16;

    private readonly IEmbeddingService _embeddingService;

    public OfficeMediaIngestor(IEmbeddingService embeddingService)
    {
        _embeddingService = embeddingService;
    }

    public async Task<(MediaAsset Asset, List<MediaChunk> Chunks)> IngestAsync(
        string filePath,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var fi = new FileInfo(filePath);
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            FileName = fi.Name,
            MediaType = MediaType.Document,
            FileSizeBytes = fi.Length,
            FileHash = MediaAsset.ComputeFileHash(fi),
            LastModifiedUtc = fi.LastWriteTimeUtc
        };

        IReadOnlyList<OfficeTextChunk> pieces = Array.Empty<OfficeTextChunk>();
        try
        {
            pieces = await Task.Run(() => OfficeTextExtractor.Extract(filePath), cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Damaged, encrypted or in a format that is not read: the file is still findable by its name.
        }

        var chunks = new List<MediaChunk>();
        for (int start = 0; start < pieces.Count; start += EmbedBatchSize)
        {
            var batch = pieces.Skip(start).Take(EmbedBatchSize).ToList();
            var inputs = batch.Select(p => new EmbeddingItemInput(Type: EmbeddingModalities.Text, Title: fi.Name, Text: p.Text)).ToList();
            var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(inputs, truncateDim, cancellationToken);

            for (int i = 0; i < batch.Count && i < embeddings.Count; i++)
            {
                if (embeddings[i].Length == 0) continue;
                chunks.Add(new MediaChunk
                {
                    Id = Guid.NewGuid(),
                    AssetId = asset.Id,
                    Modality = ModalityType.Text,
                    ChunkIndex = chunks.Count,
                    PageNumber = batch[i].Number,
                    SnippetText = batch[i].Text,
                    Embedding = embeddings[i]
                });
            }
        }

        if (chunks.Count > 0)
        {
            int lastNumber = pieces.Select(p => p.Number ?? 0).DefaultIfEmpty(0).Max(); // slides or sheets
            asset.DurationOrPages = lastNumber > 0 ? lastNumber : null;
            return (asset, chunks);
        }
        return (asset, new List<MediaChunk> { await NameOnlyChunkAsync(asset, fi.Name, truncateDim, cancellationToken) });
    }

    private async Task<MediaChunk> NameOnlyChunkAsync(MediaAsset asset, string fileName, int truncateDim, CancellationToken cancellationToken)
    {
        var input = new EmbeddingItemInput(Type: EmbeddingModalities.Text, Title: fileName, Text: $"Office document file: {fileName}");
        var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(new[] { input }, truncateDim, cancellationToken);

        return new MediaChunk
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            Modality = ModalityType.Text,
            ChunkIndex = 0,
            SnippetText = fileName,
            Embedding = embeddings.Count > 0 ? embeddings[0] : Array.Empty<float>()
        };
    }
}
