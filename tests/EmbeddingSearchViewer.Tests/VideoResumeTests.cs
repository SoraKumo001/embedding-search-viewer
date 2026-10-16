using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>An interrupted video continues where it stopped; progress is reported per video; the segment length is a setting.</summary>
public sealed class VideoResumeTests : IDisposable
{
    /// <summary>Embedding = the segment number from "..._seg_0007.jpg" in every slot, and a record of what it was asked to embed.</summary>
    private sealed class SegmentEmbedding : IEmbeddingService
    {
        public List<int> Embedded { get; } = new();
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
        {
            var numbers = items.Select(i => int.Parse(Regex.Match(i.ThumbnailPath ?? "", @"_seg_(\d+)").Groups[1].Value)).ToList();
            lock (Embedded) Embedded.AddRange(numbers);
            return Task.FromResult<IReadOnlyList<float[]>>(numbers.Select(n => Enumerable.Repeat((float)n + 1, truncateDim).ToArray()).ToList());
        }
    }

    private sealed class Session : IVideoFrameSession
    {
        private readonly Func<int, Task> _beforeFrame;
        private int _calls;
        public Session(TimeSpan duration, Func<int, Task>? beforeFrame = null) { Duration = duration; _beforeFrame = beforeFrame ?? (_ => Task.CompletedTask); }
        public TimeSpan Duration { get; }

        public async Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            await _beforeFrame(_calls++);
            File.WriteAllBytes(jpegPath, new byte[] { 0xFF, 0xD8, 0xFF });
            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Source : IVideoFrameSource
    {
        private readonly Func<IVideoFrameSession> _open;
        public Source(Func<IVideoFrameSession> open) { _open = open; }
        public string Name => "fake";
        public Task<IVideoFrameSession?> OpenAsync(string videoPath, CancellationToken cancellationToken) => Task.FromResult<IVideoFrameSession?>(_open());
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_res_" + Guid.NewGuid().ToString("N"));

    public VideoResumeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Video(string name = "movie.mp4")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "dummy");
        return path;
    }

    private static VideoSegmentExtractor Extractor(TimeSpan duration, Func<int, Task>? beforeFrame = null)
        => new(new IVideoFrameSource[] { new Source(() => new Session(duration, beforeFrame)) });

    private string CheckpointFile() => Directory.EnumerateFiles(Path.Combine(_dir, "cache", "video"), "checkpoint.bin", SearchOption.AllDirectories).Single();

    // ---- resume ----

    [Fact]
    public async Task AnInterruptedVideo_ContinuesWithTheRemainingSegments_AndGivesTheSameResult()
    {
        var video = Video();
        string cache = Path.Combine(_dir, "cache");
        var duration = TimeSpan.FromSeconds(12 * 60); // 60 segments

        // First run: stopped (cancelled) while decoding the 31st frame.
        using var cts = new CancellationTokenSource();
        var first = new SegmentEmbedding();
        var interrupted = Extractor(duration, call => { if (call == 30) cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoMediaIngestor(first, interrupted).IngestAsync(video, cache, cancellationToken: cts.Token));

        var saved = new VideoCheckpoint(Path.GetDirectoryName(CheckpointFile())!, 768).Load();
        Assert.InRange(saved.Count, 1, 30);
        Assert.Equal(saved.Keys.OrderBy(x => x), saved.Keys.OrderBy(x => x).Select((_, i) => i)); // a gap-free start: 0, 1, 2, ...

        // Second run: only what is missing is decoded and embedded.
        var second = new SegmentEmbedding();
        var decoded = new List<int>();
        var resumed = Extractor(duration, call => { decoded.Add(call); return Task.CompletedTask; });
        var (asset, chunks) = await new VideoMediaIngestor(second, resumed).IngestAsync(video, cache);

        Assert.Equal(60 - saved.Count, decoded.Count);
        Assert.DoesNotContain(second.Embedded, n => saved.ContainsKey(n));
        Assert.Equal(Enumerable.Range(saved.Count, 60 - saved.Count), second.Embedded.OrderBy(x => x));
        Assert.Equal(60, chunks.Count);
        Assert.Empty(Directory.EnumerateFiles(cache, "checkpoint.bin", SearchOption.AllDirectories)); // finished: nothing to resume

        // The same as a run that was never interrupted.
        var (cleanAsset, cleanChunks) = await new VideoMediaIngestor(new SegmentEmbedding(), Extractor(duration))
            .IngestAsync(video, Path.Combine(_dir, "cache2"));
        Assert.Equal(cleanChunks.Select(c => c.ChunkIndex), chunks.Select(c => c.ChunkIndex));
        Assert.Equal(cleanChunks.Select(c => c.StartTimeMs), chunks.Select(c => c.StartTimeMs));
        Assert.Equal(cleanChunks.Select(c => c.Embedding[0]), chunks.Select(c => c.Embedding[0]));
        Assert.Equal(cleanAsset.DurationOrPages, asset.DurationOrPages);
        Assert.All(chunks, c => Assert.True(File.Exists(c.PreviewImagePath), "the preview of a resumed segment must still exist"));
    }

    [Fact]
    public async Task ASegmentWhoseFrameIsGone_IsDoneAgain()
    {
        var video = Video();
        string cache = Path.Combine(_dir, "cache");
        var duration = TimeSpan.FromSeconds(60);

        using var cts = new CancellationTokenSource();
        var interrupted = Extractor(duration, call => { if (call == 3) cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoMediaIngestor(new SegmentEmbedding(), interrupted).IngestAsync(video, cache, cancellationToken: cts.Token));

        string dir = Path.GetDirectoryName(CheckpointFile())!;
        var saved = new VideoCheckpoint(dir, 768).Load();
        Assert.NotEmpty(saved);
        File.Delete(VideoSegmentExtractor.FramePath(dir, video, 0));

        var second = new SegmentEmbedding();
        var (_, chunks) = await new VideoMediaIngestor(second, Extractor(duration)).IngestAsync(video, cache);

        Assert.Contains(0, second.Embedded);
        Assert.All(chunks, c => Assert.True(File.Exists(c.PreviewImagePath)));
    }

    [Fact]
    public async Task ASavedCheckpointOfAnotherModelOrSegmentLength_IsNotUsed()
    {
        var video = Video();
        string cache = Path.Combine(_dir, "cache");
        var duration = TimeSpan.FromSeconds(60);

        using var cts = new CancellationTokenSource();
        var interrupted = Extractor(duration, call => { if (call == 3) cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VideoMediaIngestor(new SegmentEmbedding(), interrupted, checkpointTag: "model-a").IngestAsync(video, cache, cancellationToken: cts.Token));

        var otherModel = new SegmentEmbedding();
        await new VideoMediaIngestor(otherModel, Extractor(duration), checkpointTag: "model-b").IngestAsync(video, cache);
        Assert.Equal(Enumerable.Range(0, 5), otherModel.Embedded.OrderBy(x => x)); // everything again

        var otherLength = new SegmentEmbedding();
        await new VideoMediaIngestor(otherLength, Extractor(duration), checkpointTag: "model-a").IngestAsync(video, cache, segmentDurationSec: 30, overlapSec: 3);
        Assert.Equal(Enumerable.Range(0, 3), otherLength.Embedded.OrderBy(x => x));
    }

    // ---- checkpoint file ----

    [Fact]
    public void Checkpoint_RoundTrips_IgnoresATornLastRecord_AndAnotherDimension()
    {
        var dir = Path.Combine(_dir, "ck");
        var ck = new VideoCheckpoint(dir, 4);
        ck.Append(new[] { (0, new[] { 1f, 2f, 3f, 4f }), (1, Array.Empty<float>()) });
        ck.Append(new[] { (2, new[] { 5f, 6f, 7f, 8f }) });

        var loaded = ck.Load();
        Assert.Equal(new[] { 0, 1, 2 }, loaded.Keys.OrderBy(x => x));
        Assert.Equal(new[] { 5f, 6f, 7f, 8f }, loaded[2]);
        Assert.Empty(loaded[1]);

        string file = Path.Combine(dir, "checkpoint.bin");
        using (var fs = new FileStream(file, FileMode.Append)) fs.Write(new byte[] { 3, 0, 0, 0, 4, 0, 0, 0, 0, 0 }); // a record cut off in the middle
        Assert.Equal(new[] { 0, 1, 2 }, ck.Load().Keys.OrderBy(x => x));

        Assert.Empty(new VideoCheckpoint(dir, 768).Load());
        ck.Delete();
        Assert.Empty(ck.Load());
    }

    [Fact]
    public void Key_ChangesWithEverythingThatAffectsTheResult()
    {
        string K(string path = @"C:\v\a.mp4", long len = 10, long ticks = 5, int seg = 15, int ov = 3, int dim = 768, string tag = "m")
            => VideoCheckpoint.KeyFor(path, len, ticks, seg, ov, dim, tag);

        string KMax(int max) => VideoCheckpoint.KeyFor(@"C:\v\a.mp4", 10, 5, 15, 3, 768, "m", max);

        var keys = new[] { K(), K(path: @"C:\v\b.mp4"), K(len: 11), K(ticks: 6), K(seg: 30), K(ov: 2), K(dim: 512), K(tag: "n"), KMax(400), KMax(200) };
        Assert.Equal(keys.Length, keys.Distinct().Count());
        Assert.Equal(K(), KMax(0)); // adjustment off: the same folder as before the setting existed
        Assert.Equal(K(), K());
        Assert.Equal(K(), K(path: @"C:\V\A.MP4")); // Windows paths are case-insensitive
    }

    // ---- progress ----

    [Fact]
    public async Task Extractor_ReportsProgressAfterEverySegment_IncludingSkippedOnes()
    {
        var reports = new List<(int Done, int Total)>();
        var extractor = Extractor(TimeSpan.FromSeconds(60)); // 5 segments

        await extractor.ExtractAsync(@"C:\v\clip.mp4", _dir, 15, 3, null, new HashSet<int> { 0, 1 }, (d, t) => reports.Add((d, t)), 0);

        Assert.Equal(new[] { (1, 5), (2, 5), (3, 5), (4, 5), (5, 5) }, reports);
    }

    [Fact]
    public async Task Worker_ShowsHowFarEachVideoHasCome()
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(media, "long.mp4"), "x");

        var repo = new SqliteMediaRepository(Path.Combine(_dir, "t.db"));
        await repo.InitializeAsync();
        var extractor = Extractor(TimeSpan.FromSeconds(12 * 60), _ => Task.Delay(15)); // 60 segments, a little slow
        var worker = new IngestionPipelineWorker(repo, new SegmentEmbedding(), Path.Combine(_dir, "cache"), videoExtractor: extractor);
        var details = new List<string>();
        IngestionProgress? last = null;
        worker.ProgressChanged += p =>
        {
            last = p;
            if (p.Detail != null) lock (details) details.Add(p.Detail);
        };

        await worker.QueueDirectoryAsync(media);
        for (int i = 0; i < 100 && (last == null || last.IsRunning || last.ProcessedFiles < 1); i++) await Task.Delay(100);

        Assert.NotEmpty(details);
        Assert.All(details, d => Assert.Contains("動画 long.mp4: ", d));
        Assert.Contains(details, d => d.Contains("/ 60 場面"));
        Assert.Null(last!.Detail); // nothing is being processed any more
    }

    // ---- segment length setting ----

    [Theory]
    [InlineData(3600, 15, 400, 15)]     // 1 h: about 300 segments, fits
    [InlineData(7200, 15, 400, 30)]     // 2 h: 15 s would need about 600
    [InlineData(36000, 15, 400, 120)]   // 10 h
    [InlineData(7200, 15, 0, 15)]       // adjustment off
    [InlineData(7200, 60, 400, 60)]     // already coarse enough
    [InlineData(20000, 45, 400, 60)]    // base between steps: next longer step
    [InlineData(360000, 15, 400, 300)]  // nothing fits: the longest step
    public void ResolveSegmentSeconds_WidensOnlyAsFarAsNeeded(int durationSec, int baseSec, int max, int expected)
        => Assert.Equal(expected, VideoSegmentExtractor.ResolveSegmentSeconds(TimeSpan.FromSeconds(durationSec), baseSec, max));

    [Fact]
    public async Task Extractor_KeepsLongVideosWithinTheSegmentLimit()
    {
        var extractor = Extractor(TimeSpan.FromHours(2));

        var limited = await extractor.ExtractAsync(@"C:\v\long.mp4", _dir, 15, 3, null, null, null, 400);
        var unlimited = await extractor.ExtractAsync(@"C:\v\long2.mp4", _dir, 15, 3, null, null, null, 0);

        Assert.InRange(limited.Count, 1, 400);
        Assert.True(unlimited.Count > 400);
    }

    [Fact]
    public async Task Worker_UsesTheConfiguredSegmentLength()
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        File.WriteAllText(Path.Combine(media, "clip.mp4"), "x");

        var repo = new SqliteMediaRepository(Path.Combine(_dir, "t.db"));
        await repo.InitializeAsync();
        var worker = new IngestionPipelineWorker(
            repo, new SegmentEmbedding(), Path.Combine(_dir, "cache"),
            videoExtractor: Extractor(TimeSpan.FromSeconds(60)), videoSegmentSeconds: () => 30);
        IngestionProgress? last = null;
        worker.ProgressChanged += p => last = p;

        await worker.QueueDirectoryAsync(media);
        for (int i = 0; i < 100 && (last == null || last.IsRunning || last.ProcessedFiles < 1); i++) await Task.Delay(100);

        // 30 s segments with 3 s overlap start every 27 s: 0, 27 and 54 (instead of five segments at 15 s).
        Assert.Equal(3, (await repo.GetAllChunksWithEmbeddingsAsync()).Count);
    }

    [Fact]
    public void Setting_DefaultsTo15Seconds_AndIsKeptWhenSaved()
    {
        var store = new LibrarySettingsStore(Path.Combine(_dir, "library.json"));
        Assert.Equal(15, store.Load().VideoSegmentSeconds);

        var settings = store.Load();
        settings.VideoSegmentSeconds = 60;
        settings.Folders.Add(@"C:\v");
        store.Save(settings);

        Assert.Equal(60, store.Load().VideoSegmentSeconds);
    }
}
