using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

public class VideoMediaIngestor
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVideoSegmentExtractor _extractor;

    /// <summary>Frames are embedded in groups of this size while the decoder keeps working on the next ones.</summary>
    internal const int EmbedGroupSize = 8;

    public VideoIngestStats Stats { get; }

    /// <param name="extractor">Defaults to the Windows media stack only; the app adds FFmpeg as a second decoder.</param>
    public VideoMediaIngestor(IEmbeddingService embeddingService, IVideoSegmentExtractor? extractor = null, VideoIngestStats? stats = null)
    {
        _embeddingService = embeddingService;
        _extractor = extractor ?? new VideoSegmentExtractor(new IVideoFrameSource[] { new MediaFoundationFrameSource() });
        Stats = stats ?? new VideoIngestStats();
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
        long videoStart = Stopwatch.GetTimestamp();

        IReadOnlyList<VideoSegmentContent> segments;
        List<MediaChunk> chunks;
        if (_extractor is IStreamingVideoSegmentExtractor streaming)
        {
            (segments, chunks) = await ExtractAndEmbedStreamingAsync(
                streaming, asset, fi.Name, videoCacheDir, segmentDurationSec, overlapSec, truncateDim, cancellationToken);
        }
        else
        {
            long t0 = Stopwatch.GetTimestamp();
            segments = await _extractor.ExtractAsync(filePath, videoCacheDir, segmentDurationSec, overlapSec, cancellationToken);
            Stats.AddExtract(Stopwatch.GetTimestamp() - t0);

            var frames = segments.Where(s => s.ThumbnailPath != null).ToList();
            chunks = frames.Count > 0 ? await EmbedSegmentsAsync(asset, fi.Name, frames, truncateDim, cancellationToken) : new List<MediaChunk>();
        }

        var withFrames = segments.Where(s => s.ThumbnailPath != null).ToList();
        if (withFrames.Count > 0)
        {
            asset.ThumbnailPath = withFrames[0].ThumbnailPath;
            asset.DurationOrPages = segments.Max(s => s.EndMs);

            if (chunks.Count > 0)
            {
                Stats.AddVideo(Stopwatch.GetTimestamp() - videoStart, withFrames.Count);
                return (asset, chunks);
            }
        }

        // No decoder could read the file (or every frame was blank): keep it findable by its name.
        return (asset, new List<MediaChunk> { await NameOnlyChunkAsync(asset, fi.Name, truncateDim, cancellationToken) });
    }

    /// <summary>
    /// Decodes frames and embeds them at the same time: the decoder reports each frame as it is written, and a second loop
    /// embeds them in groups while the next frames are still being decoded. The chunks come out in segment order, exactly
    /// as when all frames are extracted first.
    /// </summary>
    private async Task<(IReadOnlyList<VideoSegmentContent> Segments, List<MediaChunk> Chunks)> ExtractAndEmbedStreamingAsync(
        IStreamingVideoSegmentExtractor extractor, MediaAsset asset, string fileName, string videoCacheDir,
        int segmentDurationSec, int overlapSec, int truncateDim, CancellationToken cancellationToken)
    {
        var ready = Channel.CreateUnbounded<VideoSegmentContent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var extraction = Task.Run(async () =>
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                return await extractor.ExtractAsync(
                    asset.FilePath, videoCacheDir, segmentDurationSec, overlapSec, s => ready.Writer.TryWrite(s), cts.Token);
            }
            catch (Exception ex)
            {
                ready.Writer.TryComplete(ex);
                throw;
            }
            finally
            {
                Stats.AddExtract(Stopwatch.GetTimestamp() - t0);
                ready.Writer.TryComplete();
            }
        }, CancellationToken.None);

        var chunks = new List<MediaChunk>();
        try
        {
            var group = new List<VideoSegmentContent>(EmbedGroupSize);
            while (await ready.Reader.WaitToReadAsync(cts.Token))
            {
                while (group.Count < EmbedGroupSize && ready.Reader.TryRead(out var segment)) group.Add(segment);
                if (group.Count == 0) continue;

                // Embed what is ready now; frames decoded meanwhile queue up for the next group.
                chunks.AddRange(await EmbedSegmentsAsync(asset, fileName, group, truncateDim, cts.Token));
                group.Clear();
            }
        }
        catch
        {
            cts.Cancel(); // stop decoding for a video whose embedding failed (or that was cancelled)
            try { await extraction; } catch { }
            throw;
        }

        var segments = await extraction;
        return (segments, chunks);
    }

    private async Task<List<MediaChunk>> EmbedSegmentsAsync(
        MediaAsset asset, string fileName, IReadOnlyList<VideoSegmentContent> segments, int truncateDim, CancellationToken cancellationToken)
    {
        var inputs = segments
            .Select(s => new EmbeddingItemInput(
                Type: "video_segment", VideoPath: asset.FilePath, ThumbnailPath: s.ThumbnailPath, StartMs: s.StartMs, EndMs: s.EndMs))
            .ToList();

        long embedStart = Stopwatch.GetTimestamp();
        var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(inputs, truncateDim, cancellationToken);
        Stats.AddEmbed(Stopwatch.GetTimestamp() - embedStart);

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
