using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using EmbeddingSearchViewer_App.ViewModels;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class ModelSetupViewModelTests : IDisposable
{
    private sealed class Server : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public Func<string, HttpResponseMessage?>? Override { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath.TrimStart('/');
            var custom = Override?.Invoke(path);
            if (custom != null) return Task.FromResult(custom);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Files[path]) });
        }
    }

    private sealed class StubEngine : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[1]);
        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<float[]>>(Array.Empty<float[]>());
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_vm_dl_" + Guid.NewGuid().ToString("N"));
    private readonly Server _server = new();
    private readonly List<ModelFileInfo> _manifest = new();

    public ModelSetupViewModelTests()
    {
        foreach (var (name, size, seed) in new[] { ("tokenizer.json", 50_000, 1), ("onnx/m.onnx_data", 700_000, 2) })
        {
            var data = new byte[size];
            new Random(seed).NextBytes(data);
            _server.Files[name] = data;
            _manifest.Add(new ModelFileInfo(name, size, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private bool AllFilesPresent() =>
        _manifest.All(f => File.Exists(Path.Combine(_dir, f.Path.Replace('/', Path.DirectorySeparatorChar))));

    private (ModelSetupViewModel Vm, EmbeddingServiceHost Host) Create()
    {
        // The engine "loads" only once every model file is on disk, like the real one.
        var host = new EmbeddingServiceHost(() => AllFilesPresent() ? new StubEngine() : throw new FileNotFoundException("missing"));
        var downloader = new ModelDownloader(new HttpClient(_server), p => "http://test/" + p, retryDelay: TimeSpan.Zero);
        return (new ModelSetupViewModel(downloader, host, _dir, _manifest), host);
    }

    [Fact]
    public void StartsNotInstalled_WithStartButtonAndSize()
    {
        var (vm, _) = Create();

        Assert.False(vm.IsInstalled);
        Assert.True(vm.CanDownload);
        Assert.True(vm.ShowDownloadButton);
        Assert.Equal("ダウンロードを開始", vm.DownloadButtonText);
        Assert.Equal("732 KB", vm.TotalSizeText);
    }

    [Fact]
    public async Task Download_InstallsTheModelAndSwitchesTheEngineOn()
    {
        var (vm, host) = Create();
        Assert.False(host.IsAvailable);

        await vm.DownloadAsync();

        Assert.True(AllFilesPresent());
        Assert.True(host.IsAvailable);
        Assert.True(vm.IsInstalled);
        Assert.False(vm.IsDownloading);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.False(vm.ShowDownloadButton);
        Assert.Contains("完了", vm.StatusText);
    }

    [Fact]
    public async Task Failure_ShowsTheErrorAndARetryThenSucceeds()
    {
        var (vm, host) = Create();
        _server.Override = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        await vm.DownloadAsync();

        Assert.False(vm.IsDownloading);
        Assert.False(vm.IsInstalled);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.Equal("再試行", vm.DownloadButtonText);
        Assert.True(vm.CanDownload);

        _server.Override = null;
        await vm.DownloadAsync();

        Assert.True(vm.IsInstalled);
        Assert.Null(vm.ErrorMessage);
        Assert.True(host.IsAvailable);
    }

    [Fact]
    public void Refresh_OffersToResumeFromPartialFiles()
    {
        var (vm, _) = Create();
        Directory.CreateDirectory(Path.Combine(_dir, "onnx"));
        File.WriteAllBytes(Path.Combine(_dir, "onnx", "m.onnx_data.part"), new byte[350_000]);

        vm.Refresh();

        Assert.False(vm.IsInstalled);
        Assert.Equal("ダウンロードを再開", vm.DownloadButtonText);
        Assert.InRange(vm.ProgressPercent, 40, 50);
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(1_755_000_000L, "1.63 GB")]
    public void FormatBytes_UsesReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, ModelSetupViewModel.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(30, "30秒")]
    [InlineData(95, "1分35秒")]
    [InlineData(7500, "2時間5分")]
    public void FormatDuration_IsHumanReadable(int seconds, string expected)
    {
        Assert.Equal(expected, ModelSetupViewModel.FormatDuration(TimeSpan.FromSeconds(seconds)));
    }
}
