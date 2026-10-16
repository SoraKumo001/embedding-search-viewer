using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>One file that cannot be read must not take the other files of its batch down with it.</summary>
public sealed class UnreadableImageTests : IDisposable
{
    /// <summary>
    /// Fails a whole batch when it contains a file whose name starts with "bad" (as the real service does), and a single
    /// "bad" file always; "flaky" files fail for the first <see cref="FlakyFailures"/> single attempts, then work.
    /// </summary>
    private sealed class PickyEmbedding : IEmbeddingService
    {
        public int FlakyFailures { get; set; } = 1;
        public Func<string, Exception> BadError { get; set; } = name => new FileNotFoundException("Image file not found.", name);
        private readonly Dictionary<string, int> _flakyAttempts = new();
        public int Calls;

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            var paths = items.Select(i => i.Path ?? string.Empty).ToList();
            bool batch = paths.Count > 1;

            foreach (var path in paths)
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith("bad")) throw BadError(path);
                if (name.StartsWith("flaky"))
                {
                    int seen = _flakyAttempts.GetValueOrDefault(name);
                    // The batch run is the first attempt; singles are the next ones.
                    if (batch || seen < FlakyFailures)
                    {
                        if (!batch) _flakyAttempts[name] = seen + 1;
                        throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<float[]>>(paths.Select(_ => Enumerable.Repeat(0.5f, truncateDim).ToArray()).ToList());
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_bad_" + Guid.NewGuid().ToString("N"));

    public UnreadableImageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string[] Files(params string[] names) => names.Select(n =>
    {
        var path = Path.Combine(_dir, n);
        File.WriteAllText(path, "x");
        return path;
    }).ToArray();

    private static ImageMediaIngestor Ingestor(IEmbeddingService service) => new(service, TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task OneUnreadableFile_OnlyLosesThatFile()
    {
        var files = Files("a.jpg", "b.jpg", "bad.jpg", "c.jpg");

        var result = await Ingestor(new PickyEmbedding()).IngestBatchWithSkipsAsync(files, _dir);

        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, result.Items.Select(i => i.Asset.FileName));
        var failure = Assert.Single(result.FailedFiles);
        Assert.EndsWith("bad.jpg", failure.Path);
        Assert.False(string.IsNullOrEmpty(failure.Error));
        Assert.Empty(result.SkippedWithoutEmbedding);
    }

    [Fact]
    public async Task ATemporarilyLockedFile_IsRetriedAndStillIndexed()
    {
        var files = Files("a.jpg", "flaky.jpg");
        var service = new PickyEmbedding { FlakyFailures = 1 };

        var result = await Ingestor(service).IngestBatchWithSkipsAsync(files, _dir);

        Assert.Equal(new[] { "a.jpg", "flaky.jpg" }, result.Items.Select(i => i.Asset.FileName));
        Assert.Empty(result.FailedFiles);
    }

    [Fact]
    public async Task AFileThatStaysLocked_IsGivenUpAfterTheRetries_AndNamed()
    {
        var files = Files("a.jpg", "flaky.jpg");
        var service = new PickyEmbedding { FlakyFailures = 100 };

        var result = await Ingestor(service).IngestBatchWithSkipsAsync(files, _dir);

        Assert.Equal("a.jpg", Assert.Single(result.Items).Asset.FileName);
        var failure = Assert.Single(result.FailedFiles);
        Assert.EndsWith("flaky.jpg", failure.Path);
        // 1 batch run + 2 singles for a.jpg + 1 + 2 retries for flaky.jpg
        Assert.Equal(1 + 1 + ImageMediaIngestor.MaxAttempts, service.Calls);
    }

    [Fact]
    public async Task AnErrorThatIsNotAboutTheFile_IsNotRetried()
    {
        var files = Files("bad.jpg");
        var service = new PickyEmbedding { BadError = _ => new InvalidDataException("Unsupported or corrupt image") };

        var result = await Ingestor(service).IngestBatchWithSkipsAsync(files, _dir);

        Assert.Equal("Unsupported or corrupt image", Assert.Single(result.FailedFiles).Error);
        Assert.Equal(2, service.Calls); // the batch run, then one single attempt; no retries for a corrupt file
    }

    [Fact]
    public async Task CancellationIsNotTurnedIntoAFileFailure()
    {
        var files = Files("a.jpg", "bad.jpg");
        var service = new PickyEmbedding { BadError = _ => new OperationCanceledException() };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Ingestor(service).IngestBatchWithSkipsAsync(files, _dir));
    }

    [Fact]
    public async Task SingleFileIngest_ReportsWhyItCouldNotBeRead()
    {
        var files = Files("bad.jpg");

        var ex = await Assert.ThrowsAsync<IOException>(() => Ingestor(new PickyEmbedding()).IngestAsync(files[0], _dir));

        Assert.Contains("bad.jpg", ex.Message);
    }

    [Fact]
    public void FailureMessage_NamesTheFirstFilesAndCountsTheRest()
    {
        var failures = Enumerable.Range(1, 5).Select(i => new ImageFailure($@"C:\x\f{i}.jpg", "locked")).ToList();

        string text = IngestionPipelineWorker.DescribeFailures(failures);

        Assert.Contains("f1.jpg (locked)", text);
        Assert.Contains("f3.jpg (locked)", text);
        Assert.DoesNotContain("f4.jpg", text);
        Assert.Contains("ほか 2 件", text);
        Assert.DoesNotContain(@"C:\x", text);
    }

    [Fact]
    public async Task Worker_StoresTheGoodImagesOfABatch_AndReportsTheBadFileByName()
    {
        var media = Path.Combine(_dir, "media");
        Directory.CreateDirectory(media);
        foreach (var n in new[] { "a.jpg", "b.jpg", "bad_one.jpg", "c.jpg" }) File.WriteAllText(Path.Combine(media, n), "x");

        var repo = new SqliteMediaRepository(Path.Combine(_dir, "t.db"));
        await repo.InitializeAsync();
        var worker = new IngestionPipelineWorker(repo, new PickyEmbedding(), Path.Combine(_dir, "cache"));
        var errors = new List<string>();
        IngestionProgress? last = null;
        worker.ProgressChanged += p =>
        {
            last = p;
            if (p.ErrorMessage != null) lock (errors) errors.Add(p.ErrorMessage);
        };

        await worker.QueueDirectoryAsync(media);
        for (int i = 0; i < 100 && (last == null || last.IsRunning || last.ProcessedFiles < 4); i++) await Task.Delay(100);

        var names = (await repo.GetAllAssetsAsync()).Select(a => a.FileName).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "a.jpg", "b.jpg", "c.jpg" }, names);
        Assert.Equal(4, last!.ProcessedFiles);
        Assert.Contains(errors, e => e.Contains("bad_one.jpg") && !e.Contains("Batch error"));
    }
}
