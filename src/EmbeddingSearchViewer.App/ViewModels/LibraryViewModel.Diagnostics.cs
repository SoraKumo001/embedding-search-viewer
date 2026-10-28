using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Core;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer_App.ViewModels;

public partial class LibraryViewModel
{
    // ---- diagnostics switches for video ingestion (this session only) ----
    //
    // The four values live in RunDiagnosticsOptions, one process-wide store that the worker reads (the two counts when
    // a run starts, the two switches per video and per frame). Every change here is written straight to it, so the
    // next run uses it, and none of them goes into SaveSettings: library.json keeps only the real settings, and the
    // values start again from the store (environment variables) at the next start.

    /// <summary>How many videos are indexed at once (1-3). Read by the worker when a run starts.</summary>
    [ObservableProperty]
    private int _videoParallelism = RunDiagnosticsOptions.MaxVideoParallelism;

    partial void OnVideoParallelismChanged(int value)
    {
        _runDiagnostics.VideoParallelism = value;
        // The store clamps: the screen shows the value the worker will actually use.
        if (_runDiagnostics.VideoParallelism != value) VideoParallelism = _runDiagnostics.VideoParallelism;
    }

    /// <summary>How many FFmpeg decodes may run at once (1-2). Read by the worker when a run starts.</summary>
    [ObservableProperty]
    private int _ffmpegPermit = RunDiagnosticsOptions.MinFfmpegPermit;

    partial void OnFfmpegPermitChanged(int value)
    {
        _runDiagnostics.FfmpegPermit = value;
        if (_runDiagnostics.FfmpegPermit != value) FfmpegPermit = _runDiagnostics.FfmpegPermit;
    }

    /// <summary>When on, a leftover decoder marker does not keep a video on the Windows media stack. Read per video.</summary>
    [ObservableProperty]
    private bool _ignoreDecoderMarkers;

    partial void OnIgnoreDecoderMarkersChanged(bool value) => _runDiagnostics.IgnoreDecoderMarkers = value;

    /// <summary>When on, every frame is written to the ingestion trace. Read per frame.</summary>
    [ObservableProperty]
    private bool _frameTracing;

    partial void OnFrameTracingChanged(bool value) => _runDiagnostics.FrameTracing = value;

    /// <summary>
    /// When on, the loopback diagnostics API listens (see <c>DiagApiServer</c>); off by default, as before. The app
    /// starts and stops the listener when this changes, so it applies at once. Not read by the worker.
    /// </summary>
    [ObservableProperty]
    private bool _enableDiagApi;

    partial void OnEnableDiagApiChanged(bool value) => _runDiagnostics.EnableDiagApi = value;

    // ---- the numbers of the run, shown while it runs ----

    /// <summary>
    /// Per-decoder milliseconds per frame and the video and stage totals of the running job; empty when nothing runs.
    /// It is polled (about every 750 ms) rather than rebuilt on every progress update: the numbers move slowly, while
    /// progress events arrive far more often than the screen can show them.
    /// </summary>
    [ObservableProperty]
    private string _diagnosticsText = string.Empty;

    private static readonly TimeSpan DiagnosticsInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>The decoder totals at the moment the run started, so only this run's frames are counted.</summary>
    private Dictionary<string, (double Seconds, long Frames)> _decoderBaseline = new(StringComparer.Ordinal);

    private readonly System.Threading.Timer _diagnosticsTimer;

    /// <summary>1 while a poll is being built, so a slow tick cannot stack up behind the next one.</summary>
    private int _diagnosticsBusy;

    private void StartDiagnosticsPolling()
    {
        _decoderBaseline = new Dictionary<string, (double Seconds, long Frames)>(VideoDecoderStats.Shared.GetRaw());
        PollDiagnostics(); // the first line right away
        _diagnosticsTimer.Change(DiagnosticsInterval, DiagnosticsInterval);
    }

    private void StopDiagnosticsPolling()
    {
        _diagnosticsTimer.Change(Timeout.Infinite, Timeout.Infinite);
        DiagnosticsText = string.Empty;
    }

    private void PollDiagnostics()
    {
        if (Interlocked.Exchange(ref _diagnosticsBusy, 1) == 1) return;
        try
        {
            string text = BuildDiagnosticsText();
            _dispatcher.Enqueue(() =>
            {
                // The run may have stopped while this line was being built.
                if (IsIndexing) DiagnosticsText = text;
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error reading run diagnostics: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _diagnosticsBusy, 0);
        }
    }

    /// <summary>
    /// "FFmpeg 41.2 ms/枚 (120 枚) · MediaFoundation 88.0 ms/枚 (30 枚)" over the phases of the run on the next line.
    /// The decoder totals come from the same counters the video extractor writes (the store the worker's extractor
    /// uses), and are the raw seconds and frames: the milliseconds per frame are worked out here.
    /// </summary>
    private string BuildDiagnosticsText()
    {
        var parts = new List<string>();
        foreach (var (decoder, totals) in VideoDecoderStats.Shared.GetRaw().OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var before = _decoderBaseline.TryGetValue(decoder, out var seen) ? seen : (Seconds: 0.0, Frames: 0L);
            double seconds = totals.Seconds - before.Seconds;
            long frames = totals.Frames - before.Frames;
            if (frames <= 0 || seconds < 0) continue; // this decoder has not run yet, or only before this run
            parts.Add(Loc.T("Library.Diagnostics.Decoder", decoder, seconds * 1000.0 / frames, frames));
        }

        string decoders = parts.Count > 0
            ? string.Join(Loc.T("Library.Diagnostics.Separator"), parts)
            : Loc.T("Library.Diagnostics.NoDecoder");

        var video = _worker.VideoStats;
        if (video.Videos == 0) return decoders;

        string line = Loc.T("Library.Diagnostics.Videos", video.Videos, video.Segments, video.ExtractSeconds, video.EmbedSeconds);
        if (_worker.StageStats.HasSamples) line += Loc.T("Library.Diagnostics.SaveShare", _worker.StageStats.SaveSharePercent);
        return decoders + "\n" + line;
    }

    /// <summary>One line per video being processed ("動画 a.mp4: 420 / 1,050 場面 (40%)"); empty otherwise.</summary>
    [ObservableProperty]
    private string _videoProgressText = string.Empty;
}
