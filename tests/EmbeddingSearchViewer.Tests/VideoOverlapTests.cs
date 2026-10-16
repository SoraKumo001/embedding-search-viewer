using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>Frames are embedded while the decoder is still working, and several videos are indexed at once.</summary>
public sealed class VideoOverlapTests : IDisposable
{
    /// <summary>Embedding = the segment number (from "s{n}.jpg") in every slot; "blank*" files are not embedded.</summary>
    private sealed class FrameEmbedding : IEmbeddingService
    {
        private readonly object _lock = new();
        public List<List<string>> Groups { get; } = new();
        public Func<IReadOnlyList<string>, Task>? OnGroup { get; set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[truncateDim]);

        public async Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
        {
            var list = items.ToList();
            var names = list.Select(i => Path.GetFileName(i.ThumbnailPath ?? i.Path ?? string.Empty)).ToList();
            lock (_lock) Groups.Add(names);
            if (OnGroup != null) await OnGroup(names);

            return names.Select(n => n.StartsWith("blank") ? Array.Empty<float>() : Enumerable.Repeat(Number(n), truncateDim).ToArray()).ToList();
        }

        private static float Number(string name) => name.StartsWith("s") && int.TryParse(name[1..name.IndexOf('.')], out int n) ? n + 1 : 0.5f;
    }

    /// <summary>Reports segments through the callback after an optional pause, like a decoder working through a video.</summary>
    private sealed class StreamingExtractor : IStreamingVideoSegmentExtractor
    {
        private readonly VideoSegmentContent[] _segments;
        public Func<int, CancellationToken, Task>? BeforeSegment { get; set; }
        public bool Cancelled { get; private set; }

        public StreamingExtractor(params VideoSegmentContent[] segments) { _segments = segments; }

        public Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds, CancellationToken cancellationToken = default)
            => ExtractAsync(videoPath, outputDirectory, segmentSeconds, overlapSeconds, null, cancellationToken);

        public async Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds,
            Action<VideoSegmentContent>? onSegmentReady, CancellationToken cancellationToken = default)
        {
            try
            {
                for (int i = 0; i < _segments.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (BeforeSegment != null) await BeforeSegment(i, cancellationToken);
                    if (_segments[i].ThumbnailPath != null) onSegmentReady?.Invoke(_segments[i]);
                }
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            return _segments;
        }
    }

    /// <summary>The same segments, handed over only when everything is extracted (what the old code did).</summary>
    private sealed class BatchExtractor : IVideoSegmentExtractor
    {
        private readonly VideoSegmentContent[] _segments;
        public BatchExtractor(params VideoSegmentContent[] segments) { _segments = segments; }

        public Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VideoSegmentContent>>(_segments);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_ovl_" + Guid.NewGuid().ToString("N"));

    public VideoOverlapTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "dummy");
        return path;
    }

    private static VideoSegmentContent[] Segments(int count, int blankIndex = -1, int noFrameIndex = -1) =>
        Enumerable.Range(0, count)
            .Select(i => new VideoSegmentContent(i, i * 12000, i * 12000 + 15000,
                i == noFrameIndex ? null : $@"C:\c\{(i == blankIndex ? "blank" : "s")}{(i == blankIndex ? "" : i.ToString())}.jpg"))
            .ToArray();

    [Fact]
    public async Task Streaming_GivesTheSameResultAsExtractingEverythingFirst()
    {
        var segments = Segments(20, blankIndex: 4, noFrameIndex: 9);
        var video = Touch("movie.mp4");

        var (assetA, chunksA) = await new VideoMediaIngestor(new FrameEmbedding(), new StreamingExtractor(segments)).IngestAsync(video, _dir);
        var (assetB, chunksB) = await new VideoMediaIngestor(new FrameEmbedding(), new BatchExtractor(segments)).IngestAsync(video, _dir);

        Assert.Equal(assetB.ThumbnailPath, assetA.ThumbnailPath);
        Assert.Equal(assetB.DurationOrPages, assetA.DurationOrPages);
        Assert.Equal(chunksB.Count, chunksA.Count);
        Assert.Equal(18, chunksA.Count); // 20 minus the blank frame and the segment without a frame
        for (int i = 0; i < chunksA.Count; i++)
        {
            Assert.Equal(chunksB[i].ChunkIndex, chunksA[i].ChunkIndex);
            Assert.Equal(chunksB[i].StartTimeMs, chunksA[i].StartTimeMs);
            Assert.Equal(chunksB[i].EndTimeMs, chunksA[i].EndTimeMs);
            Assert.Equal(chunksB[i].SnippetText, chunksA[i].SnippetText);
            Assert.Equal(chunksB[i].PreviewImagePath, chunksA[i].PreviewImagePath);
            Assert.Equal(chunksB[i].Embedding, chunksA[i].Embedding);
        }
        Assert.Equal(chunksA.Select(c => c.ChunkIndex).OrderBy(x => x), chunksA.Select(c => c.ChunkIndex));
    }

    [Fact]
    public async Task Streaming_EmbedsFramesWhileTheDecoderIsStillWorking()
    {
        var embedding = new FrameEmbedding();
        var firstEmbedded = new TaskCompletionSource();
        embedding.OnGroup = _ =>
        {
            firstEmbedded.TrySetResult();
            return Task.CompletedTask;
        };

        var extractor = new StreamingExtractor(Segments(3))
        {
            // The decoder cannot deliver frame 1 before frame 0 has been embedded; sequential code would wait forever.
            BeforeSegment = async (i, _) =>
            {
                if (i == 1) await firstEmbedded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };

        var (_, chunks) = await new VideoMediaIngestor(embedding, extractor).IngestAsync(Touch("movie.mp4"), _dir);

        Assert.Equal(3, chunks.Count);
        Assert.True(embedding.Groups.Count >= 2, "frames should have been embedded in more than one group");
    }

    [Fact]
    public async Task Streaming_GroupsAreBoundedAndTheOrderIsKept()
    {
        var embedding = new FrameEmbedding();
        var (_, chunks) = await new VideoMediaIngestor(embedding, new StreamingExtractor(Segments(30))).IngestAsync(Touch("movie.mp4"), _dir);

        Assert.Equal(30, chunks.Count);
        Assert.All(embedding.Groups, g => Assert.InRange(g.Count, 1, VideoMediaIngestor.EmbedGroupSize));
        Assert.Equal(Enumerable.Range(0, 30), chunks.Select(c => c.ChunkIndex));
    }

    [Fact]
    public async Task Streaming_StopsDecodingWhenEmbeddingFails()
    {
        var embedding = new FrameEmbedding { OnGroup = _ => throw new InvalidOperationException("GPU lost") };
        var extractor = new StreamingExtractor(Segments(5))
        {
            BeforeSegment = async (i, token) => { if (i >= 1) await Task.Delay(Timeout.Infinite, token); }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new VideoMediaIngestor(embedding, extractor).IngestAsync(Touch("movie.mp4"), _dir));

        Assert.True(extractor.Cancelled, "the decoder should be told to stop");
    }

    [Fact]
    public async Task Streaming_FallsBackToTheFileName_WhenNoFrameWasRead()
    {
        var (asset, chunks) = await new VideoMediaIngestor(new FrameEmbedding(), new StreamingExtractor()).IngestAsync(Touch("movie.webm"), _dir);

        var chunk = Assert.Single(chunks);
        Assert.Equal("movie.webm", chunk.SnippetText);
        Assert.Null(asset.ThumbnailPath);
    }

    [Fact]
    public async Task Stats_CountVideosSegmentsAndTime()
    {
        var ingestor = new VideoMediaIngestor(new FrameEmbedding(), new StreamingExtractor(Segments(4)));

        await ingestor.IngestAsync(Touch("a.mp4"), _dir);
        await ingestor.IngestAsync(Touch("b.mp4"), _dir);

        Assert.Equal(2, ingestor.Stats.Videos);
        Assert.Equal(8, ingestor.Stats.Segments);
        Assert.Contains("動画 2 本", ingestor.Stats.Summary(1.0));
        ingestor.Stats.Reset();
        Assert.Equal(string.Empty, ingestor.Stats.Summary(1.0));
    }

    [Fact]
    public async Task SegmentExtractor_ReportsFramesInOrder_ButNothingFromADecoderThatYieldsNone()
    {
        var reported = new List<int>();
        var noFrames = new SingleSource(() => new Session(TimeSpan.FromSeconds(30), _ => false));
        var partial = new SingleSource(() => new Session(TimeSpan.FromSeconds(30), call => call != 1));

        var result = await new VideoSegmentExtractor(new[] { noFrames, partial })
            .ExtractAsync(@"C:\v\clip.mp4", _dir, 15, 3, s => reported.Add(s.Index));

        Assert.Equal(new[] { 0, 2 }, reported);
        Assert.Equal(3, result.Count);
    }

    private sealed class Session : IVideoFrameSession
    {
        private readonly Func<int, bool> _ok;
        private int _calls;
        public Session(TimeSpan duration, Func<int, bool> ok) { Duration = duration; _ok = ok; }
        public TimeSpan Duration { get; }

        public Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            if (!_ok(_calls++)) return Task.FromResult(false);
            File.WriteAllBytes(jpegPath, new byte[] { 0xFF, 0xD8, 0xFF });
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleSource : IVideoFrameSource
    {
        private readonly Func<IVideoFrameSession?> _open;
        public SingleSource(Func<IVideoFrameSession?> open) { _open = open; }
        public string Name => "fake";
        public Task<IVideoFrameSession?> OpenAsync(string videoPath, CancellationToken cancellationToken) => Task.FromResult(_open());
    }

    // ---- through the worker ----

    /// <summary>Holds each video until <c>Needed</c> videos are being extracted at once (or gives up after 10 s).</summary>
    private sealed class RendezvousExtractor : IVideoSegmentExtractor
    {
        private int _active;
        private int _peak;
        private readonly TaskCompletionSource _enough = new();
        private readonly int _needed;

        public RendezvousExtractor(int needed) { _needed = needed; }
        public int Peak => Volatile.Read(ref _peak);

        public async Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds, CancellationToken cancellationToken = default)
        {
            int now = Interlocked.Increment(ref _active);
            int peak;
            while ((peak = Volatile.Read(ref _peak)) < now && Interlocked.CompareExchange(ref _peak, now, peak) != peak) { }
            if (now >= _needed) _enough.TrySetResult();

            try
            {
                await Task.WhenAny(_enough.Task, Task.Delay(TimeSpan.FromSeconds(10), cancellationToken));
                await Task.Delay(50, cancellationToken); // let the others pile up so the peak can exceed the limit if it were broken
                return new[] { new VideoSegmentContent(0, 0, 5000, $@"C:\c\s{Path.GetFileNameWithoutExtension(videoPath).Length}.jpg") };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private async Task<(IngestionProgress Last, IReadOnlyList<MediaAsset> Assets, RendezvousExtractor Extractor)> RunWorkerAsync(int videos, int parallelism, int needed)
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        for (int i = 0; i < videos; i++) File.WriteAllText(Path.Combine(media, $"clip{i}.mp4"), "x");

        var repo = new SqliteMediaRepository(Path.Combine(_dir, "t.db"));
        await repo.InitializeAsync();
        var extractor = new RendezvousExtractor(needed);
        var worker = new IngestionPipelineWorker(repo, new FrameEmbedding(), Path.Combine(_dir, "cache"), videoExtractor: extractor, videoParallelism: parallelism);
        IngestionProgress? last = null;
        worker.ProgressChanged += p => last = p;

        await worker.QueueDirectoryAsync(media);
        for (int i = 0; i < 200 && (last == null || last.IsRunning || last.ProcessedFiles < videos); i++) await Task.Delay(100);

        return (last!, await repo.GetAllAssetsAsync(), extractor);
    }

    [Fact]
    public async Task Worker_IndexesSeveralVideosAtOnce_UpToTheLimit()
    {
        var (last, assets, extractor) = await RunWorkerAsync(videos: 6, parallelism: 2, needed: 2);

        Assert.False(last.IsRunning);
        Assert.Equal(6, last.ProcessedFiles);
        Assert.Equal(6, assets.Count);
        Assert.Equal(2, extractor.Peak);
        Assert.Contains("動画 6 本", last.Summary);
    }

    [Fact]
    public async Task Worker_WithOneSlot_StillIndexesVideosOneByOne()
    {
        var (last, assets, extractor) = await RunWorkerAsync(videos: 3, parallelism: 1, needed: 1);

        Assert.False(last.IsRunning);
        Assert.Equal(3, assets.Count);
        Assert.Equal(1, extractor.Peak);
    }
}
