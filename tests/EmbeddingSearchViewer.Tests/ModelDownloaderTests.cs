using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class ModelDownloaderTests : IDisposable
{
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<(string Path, string? Range)> Requests { get; } = new();
        public bool IgnoreRange { get; set; }
        /// <summary>Per-request hook: return a response to override the default behaviour.</summary>
        public Func<string, int, HttpResponseMessage?>? Override { get; set; }
        /// <summary>When set, a response body stops after this many bytes and then blocks until cancelled.</summary>
        public int? StallAfterBytes { get; set; }

        private readonly Dictionary<string, int> _count = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');
            string? range = request.Headers.Range?.ToString();
            lock (Requests)
            {
                Requests.Add((path, range));
                _count[path] = _count.GetValueOrDefault(path) + 1;
            }

            var custom = Override?.Invoke(path, _count[path]);
            if (custom != null) return Task.FromResult(custom);

            if (!Files.TryGetValue(path, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            long from = 0;
            bool partial = false;
            if (!IgnoreRange && request.Headers.Range?.Ranges.FirstOrDefault()?.From is long f)
            {
                from = f;
                partial = true;
            }

            var slice = data.AsMemory((int)from).ToArray();
            HttpContent content = StallAfterBytes is int stall
                ? new StreamContent(new StallingStream(slice, stall))
                : new ByteArrayContent(slice);
            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
            if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class StallingStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _stallAt;
        private int _pos;
        public StallingStream(byte[] data, int stallAt) { _data = data; _stallAt = stallAt; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos >= _stallAt)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken); // blocks until the download is cancelled
            }
            int n = Math.Min(Math.Min(buffer.Length, _stallAt - _pos), _data.Length - _pos);
            _data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_dl_" + Guid.NewGuid().ToString("N"));
    private readonly FakeServer _server = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] Random(int size, int seed)
    {
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private (List<ModelFileInfo> Manifest, ModelDownloader Downloader) Setup(params (string Path, int Size)[] files)
    {
        var manifest = new List<ModelFileInfo>();
        int seed = 1;
        foreach (var (path, size) in files)
        {
            var data = Random(size, seed++);
            _server.Files[path] = data;
            manifest.Add(new ModelFileInfo(path, size, Sha(data)));
        }
        var downloader = new ModelDownloader(new HttpClient(_server), p => "http://test/" + p, retryDelay: TimeSpan.Zero);
        return (manifest, downloader);
    }

    private sealed class Collector : IProgress<ModelDownloadProgress>
    {
        public List<ModelDownloadProgress> Items { get; } = new();
        public void Report(ModelDownloadProgress value) { lock (Items) Items.Add(value); }
    }

    [Fact]
    public async Task Downloads_VerifiesAndLeavesNoPartFiles_AndReportsCompletion()
    {
        var (manifest, dl) = Setup(("tokenizer.json", 300_000), ("onnx/a.onnx", 5_000), ("onnx/a.onnx_data", 2_500_000));
        var progress = new Collector();

        await dl.DownloadAsync(manifest, _dir, progress);

        foreach (var f in manifest)
        {
            Assert.Equal(_server.Files[f.Path], await File.ReadAllBytesAsync(Path.Combine(_dir, f.Path.Replace('/', Path.DirectorySeparatorChar))));
        }
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.part", SearchOption.AllDirectories));

        var last = progress.Items.Last();
        Assert.Equal(ModelDownloadPhase.Completed, last.Phase);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(1.0, last.Fraction);
        Assert.Contains(progress.Items, p => p.Phase == ModelDownloadPhase.Verifying);
        Assert.True(progress.Items.Select(p => p.BytesDone).SequenceEqual(progress.Items.Select(p => p.BytesDone).OrderBy(x => x)),
            "progress must never go backwards");
    }

    [Fact]
    public async Task ResumesAPartialFileWithARangeRequest()
    {
        var (manifest, dl) = Setup(("onnx/big.onnx_data", 3_000_000));
        var target = Path.Combine(_dir, "onnx", "big.onnx_data");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target + ".part", _server.Files["onnx/big.onnx_data"].AsSpan(0, 1_200_000).ToArray());

        await dl.DownloadAsync(manifest, _dir);

        Assert.Equal(_server.Files["onnx/big.onnx_data"], await File.ReadAllBytesAsync(target));
        Assert.Equal("bytes=1200000-", _server.Requests.Single().Range);
    }

    [Fact]
    public async Task RestartsTheFileWhenTheServerIgnoresTheRange()
    {
        var (manifest, dl) = Setup(("onnx/x.onnx_data", 1_000_000));
        _server.IgnoreRange = true;
        var target = Path.Combine(_dir, "onnx", "x.onnx_data");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target + ".part", new byte[400_000]); // garbage prefix that must not survive

        await dl.DownloadAsync(manifest, _dir);

        Assert.Equal(_server.Files["onnx/x.onnx_data"], await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task RejectsACorruptDownload_AndDeletesIt()
    {
        var (manifest, dl) = Setup(("onnx/c.onnx_data", 500_000));
        var corrupt = _server.Files["onnx/c.onnx_data"].ToArray();
        corrupt[1234] ^= 0xFF;
        _server.Files["onnx/c.onnx_data"] = corrupt; // same size, different content

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => dl.DownloadAsync(manifest, _dir));

        Assert.Contains("チェックサム", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "onnx", "c.onnx_data")));
        Assert.False(File.Exists(Path.Combine(_dir, "onnx", "c.onnx_data.part")));
    }

    [Fact]
    public async Task SkipsFilesThatAreAlreadyComplete()
    {
        var (manifest, dl) = Setup(("a.bin", 10_000), ("b.bin", 20_000));
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "a.bin"), _server.Files["a.bin"]);

        await dl.DownloadAsync(manifest, _dir);

        Assert.Equal(new[] { "b.bin" }, _server.Requests.Select(r => r.Path));
        Assert.Equal(0, ModelDownloader.RemainingBytes(manifest, _dir));
    }

    [Fact]
    public async Task RetriesATransientFailureAndKeepsWhatWasAlreadyDownloaded()
    {
        var (manifest, dl) = Setup(("flaky.bin", 800_000));
        _server.Override = (path, n) => n == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : null;

        await dl.DownloadAsync(manifest, _dir);

        Assert.Equal(_server.Files["flaky.bin"], await File.ReadAllBytesAsync(Path.Combine(_dir, "flaky.bin")));
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task GivesUpAfterRepeatedFailures_WithAReadableError()
    {
        var (manifest, dl) = Setup(("down.bin", 1_000));
        _server.Override = (_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<ModelDownloadException>(() => dl.DownloadAsync(manifest, _dir));

        Assert.Contains("down.bin", ex.Message);
        Assert.Equal(4, _server.Requests.Count);
    }

    [Fact]
    public async Task CancellationKeepsThePartialFile_AndALaterRunResumesIt()
    {
        var (manifest, dl) = Setup(("onnx/slow.onnx_data", 2_000_000));
        var target = Path.Combine(_dir, "onnx", "slow.onnx_data");
        _server.StallAfterBytes = 700_000;
        using var cts = new CancellationTokenSource();

        var run = dl.DownloadAsync(manifest, _dir, cancellationToken: cts.Token);
        for (int i = 0; i < 200 && !(File.Exists(target + ".part") && new FileInfo(target + ".part").Length >= 700_000); i++)
        {
            await Task.Delay(25);
        }
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.False(File.Exists(target));
        Assert.Equal(700_000, new FileInfo(target + ".part").Length);

        _server.StallAfterBytes = null;
        _server.Requests.Clear();
        await dl.DownloadAsync(manifest, _dir);

        Assert.Equal(_server.Files["onnx/slow.onnx_data"], await File.ReadAllBytesAsync(target));
        Assert.Equal("bytes=700000-", _server.Requests.Single().Range);
    }

    [Fact]
    public void RemainingBytes_CountsPartialFilesAsTheirRemainder()
    {
        var (manifest, _) = Setup(("a.bin", 1_000), ("b.bin", 4_000));
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "a.bin.part"), new byte[300]);

        Assert.Equal(700 + 4_000, ModelDownloader.RemainingBytes(manifest, _dir));
    }
}

public class ModelManifestTests
{
    [Theory]
    [InlineData(ModelPrecision.Fp32)]
    [InlineData(ModelPrecision.Fp16)]
    public void Manifest_CoversExactlyTheRequiredFiles_WithChecksums(ModelPrecision precision)
    {
        var manifest = ModelManifest.For(precision);

        Assert.Equal(EmbeddingModelLocation.RequiredFiles(precision), manifest.Select(f => f.Path));
        Assert.All(manifest, f =>
        {
            Assert.True(f.Size > 0);
            Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
        });
    }

    [Fact]
    public void DownloadUrl_IsPinnedToTheRevision()
    {
        var url = EmbeddingModelLocation.DownloadUrl("onnx/model.onnx");

        Assert.Equal($"https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/resolve/{EmbeddingModelLocation.Revision}/onnx/model.onnx", url);
    }

    [Fact]
    public void MissingFiles_ListsWhatIsNotInstalledYet()
    {
        var dir = Path.Combine(Path.GetTempPath(), "esv_missing_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(EmbeddingModelLocation.IsInstalled(dir));
            Assert.Equal(EmbeddingModelLocation.RequiredFiles(EmbeddingModelLocation.DefaultPrecision), EmbeddingModelLocation.MissingFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class EmbeddingServiceHostTests
{
    private sealed class FixedService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[] { 1f });
        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<float[]>>(Array.Empty<float[]>());
    }

    [Fact]
    public async Task StartsUnavailableWhenCreationFails_AndSwitchesToTheEngineOnReload()
    {
        bool modelPresent = false;
        var host = new EmbeddingServiceHost(() => modelPresent
            ? new FixedService()
            : throw new FileNotFoundException("missing model"));

        // The engine is created in the background, so the reason is only known once that is done.
        Assert.False(await host.IsReadyAsync());
        Assert.False(host.IsAvailable);
        Assert.Contains("models", host.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.GenerateQueryEmbeddingAsync("q"));

        int changed = 0;
        host.Changed += () => Interlocked.Increment(ref changed);

        modelPresent = true;
        await host.ReloadAsync();

        Assert.True(host.IsAvailable);
        Assert.Null(host.UnavailableReason);
        Assert.True(await host.IsReadyAsync());
        Assert.Equal(new float[] { 1f }, await host.GenerateQueryEmbeddingAsync("q"));
        Assert.Equal(1, changed);
    }
}
