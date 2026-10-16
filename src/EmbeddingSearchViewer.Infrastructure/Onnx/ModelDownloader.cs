using System;
using EmbeddingSearchViewer.Core.Localization;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

public enum ModelDownloadPhase
{
    Downloading,
    Verifying,
    Completed,
}

/// <param name="CurrentFile">File being worked on (relative path).</param>
/// <param name="FileIndex">1-based index of the current file.</param>
/// <param name="BytesDone">Bytes already on disk across all files (resumed bytes included).</param>
/// <param name="BytesPerSecond">Recent download speed, 0 while unknown.</param>
public sealed record ModelDownloadProgress(
    ModelDownloadPhase Phase,
    string CurrentFile,
    int FileIndex,
    int FileCount,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond)
{
    public double Fraction => BytesTotal <= 0 ? 0 : Math.Min(1.0, (double)BytesDone / BytesTotal);
}

public sealed class ModelDownloadException : Exception
{
    public ModelDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Downloads the model files into a local directory that mirrors the repository layout.
/// Each file is written to <c>name.part</c>, resumed with HTTP Range requests after an interruption,
/// verified against its SHA-256 and only then moved into place, so a complete-looking file is always a verified one.
/// </summary>
public sealed class ModelDownloader
{
    private const int BufferSize = 1 << 20;
    private const int MaxAttempts = 4;
    private const long FreeSpaceMargin = 200L * 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpClient _http;
    private readonly Func<string, string> _urlFor;
    private readonly TimeSpan _retryDelay;

    public ModelDownloader(HttpClient? http = null, Func<string, string>? urlFor = null, TimeSpan? retryDelay = null)
    {
        _http = http ?? CreateDefaultClient();
        _urlFor = urlFor ?? EmbeddingModelLocation.DownloadUrl;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(2);
    }

    private static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbeddingSearchViewer/0.1");
        return client;
    }

    /// <summary>Bytes still to download for these files (finished files count as zero, partial files as their remainder).</summary>
    public static long RemainingBytes(IReadOnlyList<ModelFileInfo> files, string directory) =>
        files.Sum(f =>
        {
            string path = PathOf(directory, f.Path);
            if (IsComplete(path, f)) return 0L;
            long have = File.Exists(path + ".part") ? new FileInfo(path + ".part").Length : 0L;
            return Math.Max(0L, f.Size - have);
        });

    public static bool IsComplete(string path, ModelFileInfo file) => File.Exists(path) && new FileInfo(path).Length == file.Size;

    private static string PathOf(string directory, string relative) =>
        Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

    public async Task DownloadAsync(
        IReadOnlyList<ModelFileInfo> files,
        string directory,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        EnsureFreeSpace(files, directory);

        long total = files.Sum(f => f.Size);
        long doneBeforeFile = 0;
        var tracker = new ProgressTracker(progress, files.Count, total);

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[i];
            string finalPath = PathOf(directory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            tracker.StartFile(file.Path, i + 1, doneBeforeFile);

            if (IsComplete(finalPath, file))
            {
                doneBeforeFile += file.Size;
                tracker.Report(ModelDownloadPhase.Downloading, doneBeforeFile, force: true);
                continue;
            }

            await DownloadFileWithRetriesAsync(file, finalPath, tracker, doneBeforeFile, cancellationToken);
            doneBeforeFile += file.Size;
        }

        tracker.Report(ModelDownloadPhase.Completed, total, force: true);
    }

    private static void EnsureFreeSpace(IReadOnlyList<ModelFileInfo> files, string directory)
    {
        long needed = RemainingBytes(files, directory);
        if (needed <= 0) return;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root)) return;
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed + FreeSpaceMargin)
            {
                throw new ModelDownloadException(
                    Loc.T("ModelDownload.DiskSpace", needed / (1024 * 1024), free / (1024 * 1024), root));
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Free space could not be determined; the download itself will fail with a clear IO error if the disk fills up.
        }
    }

    private async Task DownloadFileWithRetriesAsync(
        ModelFileInfo file, string finalPath, ProgressTracker tracker, long doneBefore, CancellationToken ct)
    {
        string partPath = finalPath + ".part";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(file, partPath, tracker, doneBefore, ct);

                tracker.Report(ModelDownloadPhase.Verifying, doneBefore + file.Size, force: true);
                string actual = await ComputeSha256Async(partPath, ct);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partPath);
                    throw new ModelDownloadException(Loc.T("ModelDownload.ChecksumMismatch", file.Path));
                }

                File.Move(partPath, finalPath, overwrite: true);
                return;
            }
            catch (ModelDownloadException)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                // The partial file is kept, so the next attempt resumes where this one stopped.
                await Task.Delay(TimeSpan.FromTicks(_retryDelay.Ticks * attempt), ct);
            }
            catch (Exception ex) when (IsTransient(ex) && !ct.IsCancellationRequested)
            {
                throw new ModelDownloadException(Loc.T("ModelDownload.Failed", file.Path, ex.Message), ex);
            }
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or IOException or TimeoutException or TaskCanceledException;

    private async Task DownloadOnceAsync(ModelFileInfo file, string partPath, ProgressTracker tracker, long doneBefore, CancellationToken ct)
    {
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;
        if (existing > file.Size)
        {
            TryDelete(partPath);
            existing = 0;
        }
        if (existing == file.Size) return; // fully downloaded earlier, only verification is left

        using var request = new HttpRequestMessage(HttpMethod.Get, _urlFor(file.Path));
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        FileMode mode;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            mode = FileMode.Append;
        }
        else if (response.StatusCode == HttpStatusCode.OK)
        {
            mode = FileMode.Create; // the server ignored the range: start the file over
            existing = 0;
        }
        else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDelete(partPath);
            throw new IOException("The partial download no longer matches the server; restarting the file.");
        }
        else
        {
            response.EnsureSuccessStatusCode();
            throw new HttpRequestException($"Unexpected status {(int)response.StatusCode}");
        }

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(partPath, mode, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);

        var buffer = new byte[BufferSize];
        long written = existing;
        tracker.Report(ModelDownloadPhase.Downloading, doneBefore + written, force: true);

        int read;
        while ((read = await body.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            tracker.Report(ModelDownloadPhase.Downloading, doneBefore + written);
        }

        await output.FlushAsync(ct);
        if (written != file.Size)
        {
            throw new IOException($"{file.Path}: received {written:N0} bytes, expected {file.Size:N0}.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Throttles progress reports and derives a smoothed download speed.</summary>
    private sealed class ProgressTracker
    {
        private readonly IProgress<ModelDownloadProgress>? _progress;
        private readonly int _fileCount;
        private readonly long _total;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private string _file = string.Empty;
        private int _index;
        private long _lastBytes;
        private TimeSpan _lastReport = TimeSpan.MinValue;
        private TimeSpan _lastSpeedSample;
        private long _speedSampleBytes;
        private double _speed;

        public ProgressTracker(IProgress<ModelDownloadProgress>? progress, int fileCount, long total)
        {
            _progress = progress;
            _fileCount = fileCount;
            _total = total;
        }

        public void StartFile(string file, int index, long bytesDone)
        {
            _file = file;
            _index = index;
            _speedSampleBytes = bytesDone;
            _lastSpeedSample = _clock.Elapsed;
        }

        public void Report(ModelDownloadPhase phase, long bytesDone, bool force = false)
        {
            if (_progress == null) return;
            var now = _clock.Elapsed;
            if (!force && now - _lastReport < ProgressInterval) return;

            var window = now - _lastSpeedSample;
            if (window >= TimeSpan.FromSeconds(1))
            {
                double instant = (bytesDone - _speedSampleBytes) / window.TotalSeconds;
                _speed = _speed <= 0 ? instant : _speed * 0.6 + instant * 0.4;
                _lastSpeedSample = now;
                _speedSampleBytes = bytesDone;
            }

            _lastReport = now;
            _lastBytes = bytesDone;
            _progress.Report(new ModelDownloadProgress(phase, _file, _index, _fileCount, bytesDone, _total, phase == ModelDownloadPhase.Downloading ? Math.Max(0, _speed) : 0));
        }
    }
}
