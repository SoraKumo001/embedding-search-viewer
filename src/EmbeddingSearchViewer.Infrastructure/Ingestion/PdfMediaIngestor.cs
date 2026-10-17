using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

public class PdfMediaIngestor
{
    private const int MinTextLengthForTextChunk = 20;

    private readonly IEmbeddingService _embeddingService;
    private readonly IPdfPageExtractor _extractor;

    public PdfMediaIngestor(IEmbeddingService embeddingService, IPdfPageExtractor? extractor = null)
    {
        _embeddingService = embeddingService;
        _extractor = extractor ?? new WindowsPdfPageExtractor();
    }

    public async Task<(MediaAsset Asset, List<MediaChunk> Chunks)> IngestAsync(
        string filePath,
        string cacheDirectory,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var fi = new FileInfo(filePath);
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            FileName = fi.Name,
            MediaType = MediaType.Pdf,
            FileSizeBytes = fi.Length,
            FileHash = MediaAsset.ComputeFileHash(fi),
            LastModifiedUtc = fi.LastWriteTimeUtc
        };

        string pdfCacheDir = Path.Combine(cacheDirectory, "pdf", asset.Id.ToString());

        PdfExtraction? extraction = null;
        try
        {
            extraction = await _extractor.ExtractAsync(filePath, pdfCacheDir, cancellationToken);
        }
        catch (InvalidDataException)
        {
            // Corrupt or password protected: the file is still findable by its name.
        }

        if (extraction is { Pages.Count: > 0 })
        {
            asset.DurationOrPages = extraction.TotalPages;
            asset.ThumbnailPath = extraction.Pages[0].ImagePath;

            var chunks = await EmbedPagesAsync(asset, fi.Name, extraction, truncateDim, cancellationToken);
            if (chunks.Count > 0) return (asset, chunks);
        }

        return (asset, new List<MediaChunk> { await NameOnlyChunkAsync(asset, fi.Name, truncateDim, cancellationToken) });
    }

    /// <summary>One image chunk per page, plus a text chunk for pages that have a text layer.</summary>
    private async Task<List<MediaChunk>> EmbedPagesAsync(
        MediaAsset asset, string fileName, PdfExtraction extraction, int truncateDim, CancellationToken cancellationToken)
    {
        var inputs = new List<EmbeddingItemInput>();
        var metas = new List<(ModalityType Modality, PdfPageContent Page)>();

        foreach (var page in extraction.Pages)
        {
            inputs.Add(new EmbeddingItemInput(Type: EmbeddingModalities.Image, Path: page.ImagePath));
            metas.Add((ModalityType.PageImage, page));

            if (page.Text.Length > MinTextLengthForTextChunk)
            {
                inputs.Add(new EmbeddingItemInput(Type: EmbeddingModalities.Text, Title: fileName, Text: page.Text));
                metas.Add((ModalityType.Text, page));
            }
        }

        var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(inputs, truncateDim, cancellationToken);

        var chunks = new List<MediaChunk>();
        for (int i = 0; i < inputs.Count && i < embeddings.Count; i++)
        {
            // An empty embedding means the service skipped the item (a blank page): do not store it.
            if (embeddings[i].Length == 0) continue;

            var (modality, page) = metas[i];
            chunks.Add(new MediaChunk
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                Modality = modality,
                ChunkIndex = chunks.Count,
                PageNumber = page.PageNumber,
                SnippetText = page.Text,
                PreviewImagePath = page.ImagePath,
                Embedding = embeddings[i]
            });
        }
        return chunks;
    }

    private async Task<MediaChunk> NameOnlyChunkAsync(MediaAsset asset, string fileName, int truncateDim, CancellationToken cancellationToken)
    {
        var input = new EmbeddingItemInput(Type: EmbeddingModalities.Text, Title: fileName, Text: $"Document PDF file: {fileName}");
        var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(new[] { input }, truncateDim, cancellationToken);

        return new MediaChunk
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            Modality = ModalityType.Text,
            ChunkIndex = 0,
            PageNumber = 1,
            SnippetText = fileName,
            Embedding = embeddings.Count > 0 ? embeddings[0] : Array.Empty<float>()
        };
    }
}
