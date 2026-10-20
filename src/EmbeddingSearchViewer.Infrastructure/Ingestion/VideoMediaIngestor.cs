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
using EmbeddingSearchViewer.Infrastructure.Core;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

public class VideoMediaIngestor
{
    private readonly IEmbeddingService _embeddingService;
    private readonly IVideoSegmentExtractor _extractor;

    /// <summary>Frames are embedded in groups of this size while the decoder keeps working on the next ones.</summary>
    internal const int EmbedGroupSize = 8;

    /// <summary>
    /// Written just before decoding starts and removed once it finishes. Its presence means the previous decode of the
    /// same video may have killed the process (FFmpeg is native code), so the next run uses Media Foundation only.
    /// The FFmpeg worker (<see cref="ChildProcessFrameSource"/>) writes the same file when its child dies abnormally;
    /// such a marker carries <see cref="CrashMarkerField"/> and is kept by <see cref="ClearDecoderMarker"/>.
    /// </summary>
    internal const string DecoderMarkerFileName = "ffmpeg.active";

    /// <summary>Marks a marker that stands for a decode that died (as opposed to the one this run wrote itself).</summary>
    internal const string CrashMarkerField = "crashed=";

    public VideoIngestStats Stats { get; }

    private readonly string _checkpointTag;

    /// <summary>
    /// Read per video: true when a leftover decoder marker is to be ignored (the diagnostics store's
    /// <c>IgnoreDecoderMarkers</c>). Defaults to false, the historical behaviour.
    /// </summary>
    private readonly Func<bool> _ignoreDecoderMarkers;

    /// <param name="extractor">Defaults to the Windows media stack only; the app adds FFmpeg as a second decoder.</param>
    /// <param name="checkpointTag">Identifies the embedding model; saved partial work of another model is never reused.</param>
    /// <param name="ignoreDecoderMarkers">Defaults to false: a marker from an earlier FFmpeg crash is honoured once.</param>
    public VideoMediaIngestor(IEmbeddingService embeddingService, IVideoSegmentExtractor? extractor = null, VideoIngestStats? stats = null, string checkpointTag = "", Func<bool>? ignoreDecoderMarkers = null)
    {
        _embeddingService = embeddingService;
        _extractor = extractor ?? new VideoSegmentExtractor(new IVideoFrameSource[] { new MediaFoundationFrameSource() });
        Stats = stats ?? new VideoIngestStats();
        _checkpointTag = checkpointTag;
        _ignoreDecoderMarkers = ignoreDecoderMarkers ?? (() => false);
    }

    public async Task<(MediaAsset Asset, List<MediaChunk> Chunks)> IngestAsync(
        string filePath,
        string cacheDirectory,
        int segmentDurationSec = 15,
        int overlapSec = 3,
        int truncateDim = 768,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default,
        int maxSegments = 0)
    {
        var fi = new FileInfo(filePath);
        var asset = new MediaAsset
        {
            Id = Guid.NewGuid(),
            FilePath = filePath,
            FileName = fi.Name,
            MediaType = MediaType.Video,
            FileSizeBytes = fi.Length,
            FileHash = MediaAsset.ComputeFileHash(fi),
            LastModifiedUtc = fi.LastWriteTimeUtc
        };

        // The folder is named after the file and the settings, not the new asset id, so that a later run of the same
        // video finds the frames and the checkpoint of an interrupted one.
        string key = VideoCheckpoint.KeyFor(filePath, fi.Length, fi.LastWriteTimeUtc.Ticks, segmentDurationSec, overlapSec, truncateDim, _checkpointTag, maxSegments);
        string videoCacheDir = Path.Combine(cacheDirectory, "video", key);
        var checkpoint = new VideoCheckpoint(videoCacheDir, truncateDim);
        long videoStart = Stopwatch.GetTimestamp();

        // A marker left next to the frames means an earlier run of this video died while FFmpeg was decoding it. It is
        // honoured once (Windows media stack only) and then consumed, so a stale marker costs one slower run at most.
        string markerPath = Path.Combine(videoCacheDir, DecoderMarkerFileName);
        bool ignoreMarkers = _ignoreDecoderMarkers();
        bool markerPresent = !ignoreMarkers && File.Exists(markerPath);
        IVideoSegmentExtractor extractor = _extractor;
        if (markerPresent)
        {
            extractor = new VideoSegmentExtractor(new IVideoFrameSource[] { new MediaFoundationFrameSource() });
            IngestTrace.Video("marker-honored", filePath, "MediaFoundation", 0,
                detail: $"marker={markerPath}; decoding with Media Foundation only");

            // The crashed marker has now been honoured. It is replaced by this run's own marker so that a run which
            // ends normally clears it again (the next run may use FFmpeg), while a run that dies during the decode
            // still leaves it behind.
            WriteDecoderMarker(markerPath, filePath);
        }
        else
        {
            WriteDecoderMarker(markerPath, filePath);
        }

        IReadOnlyList<VideoSegmentContent> segments;
        List<MediaChunk> chunks;
        if (extractor is IStreamingVideoSegmentExtractor streaming)
        {
            (segments, chunks) = await ExtractAndEmbedStreamingAsync(
                streaming, asset, fi.Name, videoCacheDir, markerPath, checkpoint, segmentDurationSec, overlapSec, truncateDim, maxSegments, onProgress, cancellationToken);
        }
        else
        {
            long t0 = Stopwatch.GetTimestamp();
            segments = await extractor.ExtractAsync(filePath, videoCacheDir, segmentDurationSec, overlapSec, cancellationToken);
            ClearDecoderMarker(markerPath, filePath); // the decode finished: the next run may use FFmpeg again
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
                checkpoint.Delete(); // done: the result goes to the caller, nothing is left to resume
                return (asset, chunks);
            }
        }
        checkpoint.Delete();

        // No decoder could read the file (or every frame was blank): keep it findable by its name.
        IngestTrace.Video("video-name-only", asset.FilePath, null, ElapsedMs(videoStart),
            detail: $"segments={segments.Count} frames={withFrames.Count} chunks={chunks.Count}; storing a name-only chunk");
        return (asset, new List<MediaChunk> { await NameOnlyChunkAsync(asset, fi.Name, truncateDim, cancellationToken) });
    }

    /// <summary>
    /// Decodes frames and embeds them at the same time: the decoder reports each frame as it is written, and a second loop
    /// embeds them in groups while the next frames are still being decoded. The chunks come out in segment order, exactly
    /// as when all frames are extracted first. Finished segments are written to a checkpoint, so a run that is interrupted
    /// continues with the remaining segments.
    /// </summary>
    private async Task<(IReadOnlyList<VideoSegmentContent> Segments, List<MediaChunk> Chunks)> ExtractAndEmbedStreamingAsync(
        IStreamingVideoSegmentExtractor extractor, MediaAsset asset, string fileName, string videoCacheDir, string markerPath, VideoCheckpoint checkpoint,
        int segmentDurationSec, int overlapSec, int truncateDim, int maxSegments, Action<int, int>? onProgress, CancellationToken cancellationToken)
    {
        // Segments finished by an interrupted run. One whose frame is gone cannot be previewed, so it is done again.
        var embeddings = checkpoint.Load();
        foreach (var index in embeddings.Keys.ToList())
        {
            if (embeddings[index].Length > 0 && !File.Exists(VideoSegmentExtractor.FramePath(videoCacheDir, asset.FilePath, index)))
            {
                embeddings.Remove(index);
            }
        }
        var alreadyDone = embeddings.Keys.ToHashSet();
        long streamingStart = Stopwatch.GetTimestamp();
        IngestTrace.Video("ingest-begin", asset.FilePath, null, 0,
            detail: $"key={Path.GetFileName(videoCacheDir)} checkpoints={embeddings.Count} alreadyDone={alreadyDone.Count} " +
                    $"segmentSeconds={segmentDurationSec} overlapSeconds={overlapSec} maxSegments={maxSegments}");

        var ready = Channel.CreateUnbounded<VideoSegmentContent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var extraction = Task.Run(async () =>
        {
            long t0 = Stopwatch.GetTimestamp();
            try
            {
                return await extractor.ExtractAsync(
                    asset.FilePath, videoCacheDir, segmentDurationSec, overlapSec, s => ready.Writer.TryWrite(s), alreadyDone, onProgress, maxSegments, cts.Token);
            }
            catch (Exception ex)
            {
                if (ex is not OperationCanceledException) IngestTrace.Video("extract-threw", asset.FilePath, null, ElapsedMs(t0), ex: ex);
                ready.Writer.TryComplete(ex);
                throw;
            }
            finally
            {
                Stats.AddExtract(Stopwatch.GetTimestamp() - t0);
                ready.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            var group = new List<VideoSegmentContent>(EmbedGroupSize);
            while (await ready.Reader.WaitToReadAsync(cts.Token))
            {
                while (group.Count < EmbedGroupSize && ready.Reader.TryRead(out var segment)) group.Add(segment);
                if (group.Count == 0) continue;

                // Embed what is ready now; frames decoded meanwhile queue up for the next group.
                var embedded = await EmbedFramesAsync(asset, group, truncateDim, cts.Token);
                var records = new List<(int, float[])>(group.Count);
                for (int i = 0; i < group.Count; i++)
                {
                    var embedding = i < embedded.Count ? embedded[i] : Array.Empty<float>();
                    embeddings[group[i].Index] = embedding;
                    records.Add((group[i].Index, embedding));
                }
                checkpoint.Append(records);
                group.Clear();
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                IngestTrace.Video("consumer-failed", asset.FilePath, null, ElapsedMs(streamingStart),
                    detail: $"embeddings={embeddings.Count}", ex: ex);
            }
            cts.Cancel(); // stop decoding for a video whose embedding failed (or that was cancelled)
            bool extracted = false;
            try { await extraction; extracted = true; } catch { }
            // The embedding failed, but a decode that ran to the end still clears the marker: the danger being tracked
            // is a decoder crash, not an embedding error.
            if (extracted) ClearDecoderMarker(markerPath, asset.FilePath);
            throw;
        }

        var segments = await extraction;
        ClearDecoderMarker(markerPath, asset.FilePath); // the decode finished: the next run may use FFmpeg again
        var ordered = segments.Where(s => s.ThumbnailPath != null && embeddings.ContainsKey(s.Index)).ToList();
        var chunks = BuildChunks(asset, fileName, ordered, ordered.Select(s => embeddings[s.Index]).ToList());
        IngestTrace.Video("ingest-done", asset.FilePath, null, ElapsedMs(streamingStart),
            detail: $"segments={segments.Count} frames={segments.Count(s => s.ThumbnailPath != null)} chunks={chunks.Count} checkpoints={embeddings.Count}");
        return (segments, chunks);
    }

    private async Task<IReadOnlyList<float[]>> EmbedFramesAsync(
        MediaAsset asset, IReadOnlyList<VideoSegmentContent> segments, int truncateDim, CancellationToken cancellationToken)
    {
        var inputs = segments
            .Select(s => new EmbeddingItemInput(
                Type: EmbeddingModalities.VideoSegment, VideoPath: asset.FilePath, ThumbnailPath: s.ThumbnailPath, StartMs: s.StartMs, EndMs: s.EndMs))
            .ToList();

        long embedStart = Stopwatch.GetTimestamp();
        var embeddings = await _embeddingService.GenerateBatchEmbeddingsAsync(inputs, truncateDim, cancellationToken);
        Stats.AddEmbed(Stopwatch.GetTimestamp() - embedStart);
        return embeddings;
    }

    private static List<MediaChunk> BuildChunks(
        MediaAsset asset, string fileName, IReadOnlyList<VideoSegmentContent> segments, IReadOnlyList<float[]> embeddings)
    {
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

    private async Task<List<MediaChunk>> EmbedSegmentsAsync(
        MediaAsset asset, string fileName, IReadOnlyList<VideoSegmentContent> segments, int truncateDim, CancellationToken cancellationToken)
        => BuildChunks(asset, fileName, segments, await EmbedFramesAsync(asset, segments, truncateDim, cancellationToken));

    private static long ElapsedMs(long startTicks) => (long)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);

    /// <summary>
    /// Announces that a decode is starting. Only the existence of the file is read later; the content is diagnostic.
    /// Failing to write it only costs the crash detection, never the video.
    /// </summary>
    private static void WriteDecoderMarker(string markerPath, string videoPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, $"pid={Environment.ProcessId} started={DateTimeOffset.Now:O}{Environment.NewLine}");
            IngestTrace.Video("marker-written", videoPath, null, 0, detail: $"marker={markerPath}");
        }
        catch (Exception ex)
        {
            IngestTrace.Video("marker-write-failed", videoPath, null, 0, detail: $"marker={markerPath}", ex: ex);
        }
    }

    /// <summary>
    /// Consumes the marker after a decode that returned: the next run of this video may use FFmpeg again. A marker the
    /// worker's child left behind when it died is kept: the decode did not return, it crashed, and the next run has to
    /// go straight to Media Foundation (the semantics the marker had when the app itself died in FFmpeg).
    /// </summary>
    private static void ClearDecoderMarker(string markerPath, string videoPath)
    {
        try
        {
            if (!File.Exists(markerPath)) return;

            string? content = null;
            try { content = File.ReadAllText(markerPath); } catch (IOException) { }
            if (content != null && content.Contains(CrashMarkerField, StringComparison.Ordinal))
            {
                IngestTrace.Video("marker-kept-after-crash", videoPath, null, 0, detail: $"marker={markerPath}");
                return;
            }

            File.Delete(markerPath);
            IngestTrace.Video("marker-cleared", videoPath, null, 0, detail: $"marker={markerPath}");
        }
        catch (Exception ex)
        {
            IngestTrace.Video("marker-clear-failed", videoPath, null, 0, detail: $"marker={markerPath}", ex: ex);
        }
    }

    private async Task<MediaChunk> NameOnlyChunkAsync(MediaAsset asset, string fileName, int truncateDim, CancellationToken cancellationToken)
    {
        var input = new EmbeddingItemInput(Type: EmbeddingModalities.Text, Title: fileName, Text: $"Video movie file: {fileName}");
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
