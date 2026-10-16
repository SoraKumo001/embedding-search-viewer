using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

public class VideoMediaIngestor
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVideoSegmentExtractor _extractor;

    /// <param name="extractor">Defaults to the Windows media stack only; the app adds FFmpeg as a second decoder.</param>
    public VideoMediaIngestor(IEmbeddingService embeddingService, IVideoSegmentExtractor? extractor = null)
    {
        _embeddingService = embeddingService;
        _extractor = extractor ?? new VideoSegmentExtractor(new IVideoFrameSource[] { new MediaFoundationFrameSource() });
    }

    public async Task<(MediaAsset Asset, List<MediaChunk> Chunks)> IngestAsync(
        string filePath,
        string cacheDirectory,
        int segmentDurationSec = 15,
        int overlapSec = 3,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var fi = new FileInfo(filePath);
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            FileName = fi.Name,
            MediaType = MediaType.Video,
            FileSizeBytes = fi.Length,
            FileHash = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}",
            LastModifiedUtc = fi.LastWriteTimeUtc
        };

        string videoCacheDir = Path.Combine(cacheDirectory, "video", asset.Id.ToString());
        var segments = await _extractor.ExtractAsync(filePath, videoCacheDir, segmentDurationSec, overlapSec, cancellationToken);
        var withFrames = segments.Where(s => s.ThumbnailPath != null).ToList();

        if (withFrames.Count > 0)
        {
            asset.ThumbnailPath = withFrames[0].ThumbnailPath;
            asset.DurationOrPages = segments.Max(s => s.EndMs);

            var chunks = await EmbedSegmentsAsync(asset, fi.Name, withFrames, truncateDim, cancellationToken);
            if (chunks.Count > 0) return (asset, chunks);
        }

        // No decoder could read the file (or every frame was blank): keep it findable by its name.
        return (asset, new List<MediaChunk> { await NameOnlyChunkAsync(asset, fi.Name, truncateDim, cancellationToken) });
    }

    private async Task<List<MediaChunk>> EmbedSegmentsAsync(
        MediaAsset asset, string fileName, IReadOnlyList<VideoSegmentContent> segments, int truncateDim, CancellationToken cancellationToken)
    {
        var inputs = segments
            .Select(s => new EmbeddingItemInput(
                Type: "video_segment", VideoPath: asset.FilePath, ThumbnailPath: s.ThumbnailPath, StartMs: s.StartMs, EndMs: s.EndMs))
            .ToList();

        var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(inputs, truncateDim, cancellationToken);

        var chunks = new List<MediaChunk>();
        for (int i = 0; i < segments.Count && i < embeddings.Count; i++)
        {
            // Skipped by the service (a black or blank frame): nothing to search for in this segment.
            if (embeddings[i].Length == 0) continue;

            var s = segments[i];
            chunks.Add(new MediaChunk
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                Modality = ModalityType.VideoSegment,
                ChunkIndex = s.Index,
                StartTimeMs = s.StartMs,
                EndTimeMs = s.EndMs,
                PreviewImagePath = s.ThumbnailPath,
                SnippetText = $"{fileName} (Clip {s.Index + 1})",
                Embedding = embeddings[i]
            });
        }
        return chunks;
    }

    private async Task<MediaChunk> NameOnlyChunkAsync(MediaAsset asset, string fileName, int truncateDim, CancellationToken cancellationToken)
    {
        var input = new EmbeddingItemInput(Type: "text", Title: fileName, Text: $"Video movie file: {fileName}");
        var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(new[] { input }, truncateDim, cancellationToken);

        return new MediaChunk
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            Modality = ModalityType.VideoSegment,
            ChunkIndex = 0,
            StartTimeMs = 0,
            EndTimeMs = 1000,
            SnippetText = fileName,
            Embedding = embeddings.Count > 0 ? embeddings[0] : Array.Empty<float>()
        };
    }
}
