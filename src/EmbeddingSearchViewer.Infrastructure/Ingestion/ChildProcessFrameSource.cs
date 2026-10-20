using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Infrastructure.Core;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// Decodes frames with FFmpeg in a separate process (<c>EmbeddingSearchViewer.FfmpegWorker.exe</c>), one process per
/// video. FFmpeg's native objects then never run in the app process, so several videos decoded at the same time cannot
/// drive FFmpeg concurrently there (the 0xC0000374 heap corruption the process-wide gate used to prevent).
/// </summary>
/// <remarks>
/// <para>
/// <b>No in-process FFmpeg.</b> Everything crosses the process boundary as files and a line of text: the child writes
/// one JPEG per segment into the video's cache directory (the same <c>FramePath</c> name the in-process extractor
/// uses) and prints one line per finished segment. There is no shared memory and no binary IPC.
/// </para>
/// <para>
/// <b>One process per video.</b> The child plans the segments itself from the parameters it is given on the command
/// line; <see cref="IVideoFrameSourceBinding"/> is how those parameters reach it (<see cref="OpenAsync"/> only knows the
/// video). A file is never retried in the child: opening fails (exit code 2) or the process dies, and the extractor
/// moves on to Media Foundation.
/// </para>
/// <para>
/// <b>Timeouts.</b> A frame waits for its segment line, the child's exit, or <c>FrameTimeout</c>; the timeout is the
/// only place that kills the child, so there is exactly one wait-and-kill path in the parent. Disposal kills and waits
/// for the same process.
/// </para>
/// <para>
/// <b>Crash marker.</b> An abnormal exit (neither 0 nor the decoder-failure code) writes the existing
/// <c>ffmpeg.active</c> marker next to the frames, with a <c>crashed=</c> field. The marker is what made the next run of
/// a video go straight to Media Foundation when the app itself died in FFmpeg; the child crashing now has to leave the
/// same signal, and <see cref="VideoMediaIngestor"/> keeps such a marker instead of clearing it.
/// </para>
/// </remarks>
public sealed class ChildProcessFrameSource : IVideoFrameSource, IVideoFrameSourceBinding
{
    /// <summary>File name of the worker next to the app (in the <c>FfmpegWorker</c> folder) and in its own build output.</summary>
    public const string ExecutableFileName = "EmbeddingSearchViewer.FfmpegWorker.exe";

    /// <summary>The worker completed every segment it was asked for.</summary>
    public const int ExitComplete = 0;

    /// <summary>The worker could not open the file or could not read a frame (partly or completely).</summary>
    public const int ExitDecoderFailed = 2;

    // Opening the file (container parse) is bounded so a wedged decoder cannot hold the ingest before a single frame.
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

    // One frame: measured well under a second, so this only catches a decoder that never returns.
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(30);

    // After Kill the process has to go away; this bounds the wait in Dispose so a stuck child cannot hold the run.
    private static readonly TimeSpan ExitWaitLimit = TimeSpan.FromSeconds(10);

    private readonly string? _executablePath;

    // Set by BindForExtraction: the child gets all of these on its command line.
    private readonly string? _outputDirectory;
    private readonly int _segmentSeconds;
    private readonly int _overlapSeconds;
    private readonly int _maxSegments;
    private readonly IReadOnlySet<int>? _alreadyDone;

    /// <param name="executablePath">
    /// Full path to the worker executable, or null when it was not shipped. The app resolves it once; a missing worker
    /// simply means this decoder is not offered (the flag is off by default, see <c>RunDiagnosticsOptions</c>).
    /// </param>
    public ChildProcessFrameSource(string? executablePath)
        : this(executablePath, null, 0, 0, 0, null)
    {
    }

    private ChildProcessFrameSource(
        string? executablePath, string? outputDirectory, int segmentSeconds, int overlapSeconds,
        int maxSegments, IReadOnlySet<int>? alreadyDone)
    {
        _executablePath = executablePath;
        _outputDirectory = outputDirectory;
        _segmentSeconds = segmentSeconds;
        _overlapSeconds = overlapSeconds;
        _maxSegments = maxSegments;
        _alreadyDone = alreadyDone;
    }

    public string Name => "FFmpegWorker";

    /// <summary>Whether the worker executable is present; the app only offers this decoder when it is.</summary>
    public bool IsAvailable => !string.IsNullOrEmpty(_executablePath) && File.Exists(_executablePath);

    /// <summary>The worker executable this source starts, for the diagnostics log.</summary>
    public string? ExecutablePath => _executablePath;

    /// <summary>
    /// Binds one extraction: the child needs the cache directory (where the JPEGs go), the segment length, the
    /// overlap, the segments an earlier run already finished, and the segment cap, all before it opens the file.
    /// </summary>
    public IVideoFrameSource BindForExtraction(
        string outputDirectory, int segmentSeconds, int overlapSeconds, IReadOnlySet<int>? alreadyDone, int maxSegments)
        => new ChildProcessFrameSource(_executablePath, outputDirectory, segmentSeconds, overlapSeconds, maxSegments, alreadyDone);

    public async Task<IVideoFrameSession?> OpenAsync(string videoPath, CancellationToken cancellationToken)
    {
        if (!IsAvailable || _outputDirectory == null)
        {
            IngestTrace.Video("worker-unavailable", videoPath, Name, 0,
                detail: $"path={_executablePath ?? "-"} bound={_outputDirectory != null}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var session = new Session(
            _executablePath!, videoPath, _outputDirectory, _segmentSeconds, _overlapSeconds, _maxSegments, _alreadyDone);

        long startTicks = Stopwatch.GetTimestamp();
        int durationMs;
        try
        {
            durationMs = await session.StartAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IngestTrace.Video("worker-start-failed", videoPath, Name, ElapsedMs(startTicks), ex: ex);
            await session.DisposeAsync();
            return null;
        }
        catch (OperationCanceledException)
        {
            await session.DisposeAsync();
            throw;
        }

        if (durationMs <= 0)
        {
            // Exit code 2 (the file is not one FFmpeg can read) lands here as well: the extractor tries Media Foundation.
            IngestTrace.Video("worker-open-failed", videoPath, Name, ElapsedMs(startTicks),
                detail: $"exitCode={session.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
            await session.DisposeAsync();
            return null;
        }

        IngestTrace.Video("worker-open", videoPath, Name, ElapsedMs(startTicks),
            detail: $"pid={session.ProcessId} durationMs={durationMs} exe={_executablePath}");
        return session;
    }

    private static long ElapsedMs(long startTicks) => (long)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);

    /// <summary>One running worker process and the frames it has announced.</summary>
    private sealed class Session : IVideoFrameSession
    {
        private readonly Process _process;
        private readonly string _videoPath;
        private readonly string _outputDirectory;

        // Segment line management. A line can arrive before SaveFrameAsync asks for that segment (the child does not
        // wait for the parent), so finished indices are remembered as well as the waiters.
        private readonly object _gate = new();
        private readonly Dictionary<int, TaskCompletionSource> _waiters = new();
        private readonly HashSet<int> _announced = new();

        private readonly TaskCompletionSource<int> _duration = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private volatile bool _disposeRequested;
        private volatile bool _killedByParent;
        private int _processId = -1;
        private int _durationMs = -1;
        private int? _exitCode;
        private long _startTicks;

        public Session(
            string executablePath, string videoPath, string outputDirectory,
            int segmentSeconds, int overlapSeconds, int maxSegments, IReadOnlySet<int>? alreadyDone)
        {
            _videoPath = videoPath;
            _outputDirectory = outputDirectory;

            var info = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
            };

            // The worker takes exactly the parameters of this extraction; the skip list lets it leave the frames of an
            // earlier run alone instead of decoding them again.
            string skip = alreadyDone is { Count: > 0 }
                ? string.Join(',', alreadyDone.OrderBy(i => i))
                : string.Empty;
            info.ArgumentList.Add(videoPath);
            info.ArgumentList.Add(outputDirectory);
            info.ArgumentList.Add(segmentSeconds.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(overlapSeconds.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(skip);
            info.ArgumentList.Add(maxSegments.ToString(CultureInfo.InvariantCulture));

            _process = new Process { StartInfo = info };
        }

        public TimeSpan Duration { get; private set; }

        public int? ExitCode => _exitCode;

        public int ProcessId => _processId;

        /// <summary>Starts the child and returns the duration it reports, or -1 when it could not be used.</summary>
        public async Task<int> StartAsync(CancellationToken cancellationToken)
        {
            _startTicks = Stopwatch.GetTimestamp();
            if (!_process.Start())
            {
                return -1;
            }
            _processId = _process.Id;

            _ = Task.Run(ReadStdoutAsync);
            _ = Task.Run(ReadStderrAsync);
            _ = Task.Run(MonitorExitAsync);

            var finished = await Task.WhenAny(_duration.Task, Task.Delay(OpenTimeout, cancellationToken));
            if (finished != _duration.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Opening did not even report a duration: the child is not usable, and nothing of it is kept.
                _killedByParent = true;
                Kill();
                IngestTrace.Video("worker-open-timeout", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks),
                    detail: $"waitedMs={(long)OpenTimeout.TotalMilliseconds}");
                return -1;
            }

            _durationMs = _duration.Task.Result;
            if (_durationMs > 0)
            {
                // Rounded to whole milliseconds on both sides, so the plan of the child (which plans from its own
                // TimeSpan) and the plan of the extractor (which plans from this Duration) come out identical.
                Duration = TimeSpan.FromMilliseconds(_durationMs);
            }
            return _durationMs;
        }

        public async Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            if (_disposeRequested) return false;

            int index = SegmentIndexOf(jpegPath);
            if (index < 0)
            {
                IngestTrace.Video("worker-path-unexpected", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks),
                    detail: $"path={jpegPath}");
                return false;
            }

            if (TryTakeAnnounced(index))
            {
                return File.Exists(jpegPath);
            }

            var cancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => cancel.TrySetResult());

            Task segment;
            lock (_gate)
            {
                // The line can land between the check above and the waiter being registered.
                if (_announced.Remove(index))
                {
                    return File.Exists(jpegPath);
                }

                if (!_waiters.TryGetValue(index, out var waiter))
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[index] = waiter;
                }
                segment = waiter.Task;
            }

            var finished = await Task.WhenAny(segment, _exited.Task, Task.Delay(FrameTimeout), cancel.Task);

            if (finished == segment)
            {
                bool written = File.Exists(jpegPath);
                if (!written)
                {
                    IngestTrace.Video("worker-frame-missing", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks), index,
                        detail: $"path={jpegPath}; the child announced the segment but the file is gone");
                }
                return written;
            }

            if (finished == cancel.Task)
            {
                // The run was cancelled: disposing the session kills the child, so nothing is abandoned here.
                return false;
            }

            if (finished == _exited.Task)
            {
                IngestTrace.Video("worker-exited-frame", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks), index,
                    detail: $"positionMs={(long)position.TotalMilliseconds} exitCode={_exitCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                return false;
            }

            // One timeout for the whole wait, then the child is killed: a decoder that never returns must not hold the
            // ingest (and there is no native call left in this process that could be abandoned instead).
            _killedByParent = true;
            Kill();
            IngestTrace.Video("worker-frame-timeout", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks), index,
                detail: $"positionMs={(long)position.TotalMilliseconds} waitedMs={(long)FrameTimeout.TotalMilliseconds}; child killed");
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            _disposeRequested = true;

            // A child that already ended by itself keeps its own verdict (MonitorExitAsync writes the crash marker for
            // an abnormal exit). Only a process that is still running is being stopped by this dispose.
            bool running;
            try { running = !_process.HasExited; }
            catch (InvalidOperationException) { running = false; }

            if (running) _killedByParent = true;
            Kill();

            try
            {
                using var cts = new CancellationTokenSource(ExitWaitLimit);
                await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                IngestTrace.Video("worker-exit-timeout", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks),
                    detail: $"waitedMs={(long)ExitWaitLimit.TotalMilliseconds}; the child did not end after Kill");
            }
            catch (Exception ex)
            {
                IngestTrace.Video("worker-exit-wait-failed", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks), ex: ex);
            }
            finally
            {
                try { _process.Dispose(); } catch (Exception) { /* nothing left to release */ }
            }
        }

        /// <summary>Kills the child (and anything it started). Safe to call more than once and after it exited.</summary>
        private void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Not started, or already gone.
            }
            catch (Exception ex)
            {
                IngestTrace.Video("worker-kill-failed", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks), ex: ex);
            }
        }

        private async Task ReadStdoutAsync()
        {
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    HandleLine(line);
                }
            }
            catch (Exception ex)
            {
                IngestTrace.Video("worker-stdout-failed", _videoPath, "FFmpegWorker", 0, ex: ex);
            }
        }

        private async Task ReadStderrAsync()
        {
            try
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 0)
                    {
                        IngestTrace.Video("worker-log", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks), detail: line);
                    }
                }
            }
            catch (Exception)
            {
                // A closed pipe after a kill is expected.
            }
        }

        private async Task MonitorExitAsync()
        {
            int exitCode;
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
                exitCode = _process.ExitCode;
            }
            catch (Exception)
            {
                exitCode = int.MinValue; // unknown; treated as abnormal below
            }

            _exitCode = exitCode;
            _duration.TrySetResult(-1);
            _exited.TrySetResult(exitCode);

            if (exitCode == ExitComplete || exitCode == ExitDecoderFailed)
            {
                IngestTrace.Video("worker-exit", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks),
                    detail: $"exitCode={exitCode}");
                return;
            }

            IngestTrace.Video("worker-exit", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks),
                detail: $"exitCode={exitCode} killedByParent={_killedByParent}");

            // A killed child is the parent's own decision (stop, timeout, dispose), not a decoder that took the process
            // down: only an unexpected end of the child is worth the marker.
            if (!_killedByParent)
            {
                WriteCrashMarker(exitCode);
            }
        }

        /// <summary>
        /// Leaves the decoder marker that makes the next run of this video use Media Foundation. The marker is the one
        /// <see cref="VideoMediaIngestor"/> already writes before a decode; the additional <c>crashed=</c> field tells
        /// it not to clear this one when the (surviving) app finishes the video.
        /// </summary>
        private void WriteCrashMarker(int exitCode)
        {
            try
            {
                Directory.CreateDirectory(_outputDirectory);
                string markerPath = Path.Combine(_outputDirectory, VideoMediaIngestor.DecoderMarkerFileName);
                File.WriteAllText(markerPath,
                    $"pid={_processId} crashed={DateTimeOffset.Now:O} exitCode={exitCode} " +
                    $"video={_videoPath}{Environment.NewLine}");
                IngestTrace.Video("worker-crash-marker", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks),
                    detail: $"marker={markerPath} exitCode={exitCode}");
            }
            catch (Exception ex)
            {
                IngestTrace.Video("worker-crash-marker-failed", _videoPath, "FFmpegWorker", ElapsedMs(_startTicks), ex: ex);
            }
        }

        private void HandleLine(string line)
        {
            if (line.StartsWith("DURATION ", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan("DURATION ".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms))
                {
                    _duration.TrySetResult(ms);
                }
                return;
            }

            if (line.StartsWith("SEG ", StringComparison.Ordinal))
            {
                // SEG <index> <middleMs>
                string[] parts = line.Split(' ');
                if (parts.Length >= 3
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                    && index >= 0)
                {
                    Announce(index);
                }
            }
        }

        private void Announce(int index)
        {
            TaskCompletionSource? waiter = null;
            lock (_gate)
            {
                if (!_waiters.Remove(index, out waiter))
                {
                    _announced.Add(index); // asked for later; remembered until then
                }
            }

            waiter?.TrySetResult();
        }

        private bool TryTakeAnnounced(int index)
        {
            lock (_gate)
            {
                return _announced.Remove(index);
            }
        }

        /// <summary>The segment number in a frame path ("..._seg_0007.jpg"), or -1 when the name does not carry one.</summary>
        private static int SegmentIndexOf(string jpegPath)
        {
            string name = Path.GetFileNameWithoutExtension(jpegPath);
            int marker = name.LastIndexOf("_seg_", StringComparison.Ordinal);
            if (marker < 0) return -1;

            return int.TryParse(name.AsSpan(marker + "_seg_".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                ? index
                : -1;
        }
    }
}
