using System;
using System.Collections.Generic;
using System.Linq;
using EmbeddingSearchViewer.Core.Localization;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// Estimates how long an indexing run still needs from how fast it has been going. The speed is taken from the last
/// minute (so it follows a change of the files being processed) and from the whole run while that is too short.
/// Stretches with nothing to report (a stalled run) do not count towards the speed, so they do not make it look slow.
/// Nothing is estimated during the first seconds, when the speed would only be a guess.
/// </summary>
public sealed class IngestionEta
{
    /// <summary>Seconds of actual work a run must have been going before an estimate is given.</summary>
    public const double WarmUpSeconds = 10;

    /// <summary>How far back the speed and the share of files that need work are measured.</summary>
    public const double WindowSeconds = 60;

    /// <summary>Files that needed work in the window before its share of work is trusted over the whole run.</summary>
    private const double MinWindowHandled = 20;

    private const double SampleSpacingSeconds = 1;

    private readonly Queue<(DateTime Time, double Done, double Handled)> _samples = new();
    private DateTime _start;
    private DateTime _lastSample;
    private double _startDone;
    private bool _started;
    private bool _progressing;
    private DateTime _previousTime;
    private double _previousDone;
    private double _previousHandled;

    // One video can need hundreds of scenes while it counts as a single file, so the file speed alone makes the tail
    // of a run (where videos finish while the queue is already empty) look much shorter than it is. The scene speed is
    // measured separately, the same way.
    private readonly SceneSpeed _sceneSpeed = new();

    /// <summary>The time left in coarse, readable steps (the estimate is rough anyway), in the current language.</summary>
    public static string Format(TimeSpan left)
    {
        int minutes = (int)Math.Ceiling(left.TotalMinutes);
        string text = left.TotalSeconds < 60 ? Loc.T("Library.Eta.LessThanMinute")
            : minutes < 60 ? Loc.T("Library.Eta.Minutes", minutes)
            : Loc.T("Library.Eta.Hours", minutes / 60, minutes % 60);
        return Loc.T("Library.Eta", text);
    }

    public void Reset()
    {
        _samples.Clear();
        _started = false;
        _sceneSpeed.Reset();
    }

    /// <summary>
    /// The time left for an indexing run. Only the files that needed work set the file speed: files that are already
    /// indexed are checked far faster, so counting them would make the rest look quicker than it is. How much work the
    /// files still to come will need is taken from the last minute and, right after a run resumes behind a batch of
    /// already indexed files, from the whole run too; the more pessimistic of the two is used so the estimate is not
    /// too short. The scenes left in the videos being processed give a second estimate, and the larger one is returned:
    /// a video tail is far slower than the file count alone suggests.
    /// </summary>
    public TimeSpan? Update(DateTime now, IngestionProgress progress)
    {
        double handled = progress.ProcessedFiles + progress.InFlightFiles;
        double worked = Math.Max(0, handled - progress.QuickFiles);
        var files = Update(now, worked, handled, progress.TotalFiles);

        // Fed even with no video left, so the history is there when the next video starts; a flat count costs nothing.
        double? sceneRate = _sceneSpeed.Add(now, progress.VideoDoneScenes);
        if (progress.VideoRemainingScenes <= 0 || sceneRate is not > 0) return files;

        var videos = FromSeconds(progress.VideoRemainingScenes / sceneRate.Value);
        if (videos is not { } videoTime) return files;
        if (files is not { } fileTime) return videoTime;
        return videoTime > fileTime ? videoTime : fileTime;
    }

    /// <summary>
    /// Reports the progress at <paramref name="now"/> (units may be fractions: a video that is half done counts as half a file)
    /// and returns the time left, or null while there is no basis for an estimate.
    /// </summary>
    public TimeSpan? Update(DateTime now, double done, double total)
        => Update(now, done, done, total);

    /// <summary>
    /// <paramref name="done"/> is the work done, <paramref name="handled"/> the files finished (work plus the ones that
    /// needed none) and <paramref name="total"/> how many files the run covers.
    /// </summary>
    private TimeSpan? Update(DateTime now, double done, double handled, double total)
    {
        if (!_started)
        {
            _started = true;
            _progressing = false;
            _start = now;
            _startDone = done;
            _lastSample = now;
            (_previousTime, _previousDone, _previousHandled) = (now, done, handled);
            _samples.Enqueue((now, done, handled));
            return null;
        }

        // The measuring starts when work first gets done: what came before (a first stretch with nothing to report)
        // must not count as time the work took.
        var (previousTime, previousDone, previousHandled) = (_previousTime, _previousDone, _previousHandled);
        (_previousTime, _previousDone, _previousHandled) = (now, done, handled);
        if (!_progressing)
        {
            if (done <= _startDone) return null;

            // From the last report before the first progress, so the work that led to it is counted.
            _progressing = true;
            _start = previousTime;
            _startDone = previousDone;
            _lastSample = previousTime;
            _samples.Clear();
            _samples.Enqueue((previousTime, previousDone, previousHandled));
        }

        if ((now - _lastSample).TotalSeconds >= SampleSpacingSeconds)
        {
            _samples.Enqueue((now, done, handled));
            _lastSample = now;
        }
        while (_samples.Count > 1 && (now - _samples.Peek().Time).TotalSeconds > WindowSeconds) _samples.Dequeue();

        double elapsed = (now - _start).TotalSeconds;
        if (elapsed < WarmUpSeconds) return null;

        double rate = Rate(now, done);
        if (rate <= 0) return null;

        double remaining = Math.Max(0, total - handled);
        double seconds = remaining * Share(done, handled) / rate;
        return FromSeconds(seconds);
    }

    /// <summary>
    /// Files per second of work, measured over the samples in the window. Only the stretches in which work advanced
    /// count towards the time, so a run that stalled (nothing to report for a while) is not made to look slow.
    /// </summary>
    private double Rate(DateTime now, double done)
        => Speed(_samples.Select(s => (s.Time, s.Done)), _start, _startDone, now, done);

    /// <summary>
    /// Work per second: the active stretches of the window first, and while there are too few of them the average over
    /// the whole run.
    /// </summary>
    private static double Speed(IEnumerable<(DateTime Time, double Done)> samples,
        DateTime start, double startDone, DateTime now, double done)
    {
        double rate = ActiveRate(samples);
        if (rate > 0) return rate;

        double elapsed = (now - start).TotalSeconds;
        return elapsed > 0 ? (done - startDone) / elapsed : 0;
    }

    /// <summary>
    /// Work per second over the samples, counting only the stretches in which work advanced (0 when there is too
    /// little to tell). A stall therefore does not make the speed look slow.
    /// </summary>
    private static double ActiveRate(IEnumerable<(DateTime Time, double Done)> samples)
    {
        double activeSeconds = 0;
        double activeWork = 0;
        bool first = true;
        DateTime previousTime = default;
        double previousDone = 0;
        foreach (var sample in samples)
        {
            if (!first)
            {
                double step = sample.Done - previousDone;
                if (step > 0)
                {
                    activeSeconds += (sample.Time - previousTime).TotalSeconds;
                    activeWork += step;
                }
            }
            first = false;
            (previousTime, previousDone) = (sample.Time, sample.Done);
        }

        return activeSeconds >= WarmUpSeconds && activeWork > 0 ? activeWork / activeSeconds : 0;
    }

    /// <summary>
    /// The share of the files still to come that are expected to need work. Taken from the last minute (so it follows
    /// a run that resumes behind a batch of already indexed files) and from the whole run; the larger of the two is
    /// used, so a recent burst of work is not read as "the rest is quick too" and the estimate stays on the safe side.
    /// </summary>
    private double Share(double done, double handled)
    {
        var oldest = _samples.Peek();
        double windowHandled = handled - oldest.Handled;
        double? windowShare = windowHandled >= MinWindowHandled && done >= oldest.Done
            ? Clamp01((done - oldest.Done) / windowHandled)
            : null;
        double? globalShare = handled > 0 ? Clamp01(done / handled) : null;

        if (windowShare is { } window && globalShare is { } global) return Math.Max(window, global);
        return windowShare ?? globalShare ?? 1.0;
    }

    private static TimeSpan? FromSeconds(double seconds)
        => seconds > TimeSpan.MaxValue.TotalSeconds / 2 ? null : TimeSpan.FromSeconds(seconds);

    private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;

    /// <summary>
    /// Scenes per second of work, measured like the file speed: last minute first, whole run as the fallback, and only
    /// the stretches in which scenes advanced count. The first seconds are ignored, so a video that has just started
    /// does not produce a wild guess.
    /// </summary>
    private sealed class SceneSpeed
    {
        private readonly Queue<(DateTime Time, double Done)> _samples = new();
        private DateTime _start;
        private DateTime _lastSample;
        private double _startDone;
        private bool _started;
        private bool _progressing;
        private DateTime _previousTime;
        private double _previousDone;

        public void Reset()
        {
            _samples.Clear();
            _started = false;
        }

        /// <summary>Adds the current count and returns the scene speed, or null while it cannot be told.</summary>
        public double? Add(DateTime now, double done)
        {
            if (!_started)
            {
                _started = true;
                _progressing = false;
                _start = now;
                _startDone = done;
                _lastSample = now;
                (_previousTime, _previousDone) = (now, done);
                _samples.Enqueue((now, done));
                return null;
            }

            var (previousTime, previousDone) = (_previousTime, _previousDone);
            (_previousTime, _previousDone) = (now, done);
            if (!_progressing)
            {
                if (done <= _startDone) return null;

                // Start at the last report without progress, so the stretch that led here is counted.
                _progressing = true;
                _start = previousTime;
                _startDone = previousDone;
                _lastSample = previousTime;
                _samples.Clear();
                _samples.Enqueue((previousTime, previousDone));
            }

            if ((now - _lastSample).TotalSeconds >= SampleSpacingSeconds)
            {
                _samples.Enqueue((now, done));
                _lastSample = now;
            }
            while (_samples.Count > 1 && (now - _samples.Peek().Time).TotalSeconds > WindowSeconds) _samples.Dequeue();

            if ((now - _start).TotalSeconds < WarmUpSeconds) return null;

            double rate = Speed(_samples.Select(s => (s.Time, s.Done)), _start, _startDone, now, done);
            return rate > 0 ? rate : null;
        }
    }
}
