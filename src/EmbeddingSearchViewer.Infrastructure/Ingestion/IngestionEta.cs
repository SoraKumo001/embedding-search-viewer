using System;
using System.Collections.Generic;
using EmbeddingSearchViewer.Core.Localization;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// Estimates how long an indexing run still needs from how fast it has been going. The speed is taken from the last
/// minute (so it follows a change of the files being processed) and from the whole run while that is too short.
/// Nothing is estimated during the first seconds, when the speed would only be a guess.
/// </summary>
public sealed class IngestionEta
{
    /// <summary>Seconds a run must have been going before an estimate is given.</summary>
    public const double WarmUpSeconds = 10;

    /// <summary>How far back the speed is measured.</summary>
    public const double WindowSeconds = 60;

    private const double SampleSpacingSeconds = 1;

    private readonly Queue<(DateTime Time, double Done)> _samples = new();
    private DateTime _start;
    private DateTime _lastSample;
    private double _startDone;
    private bool _started;
    private bool _progressing;
    private DateTime _previousTime;
    private double _previousDone;

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
    }

    /// <summary>
    /// The time left for an indexing run. Only the files that needed work set the speed: files that are already indexed
    /// are checked far faster, so counting them would make the rest look quicker than it is. The files still to come are
    /// assumed to need work in the same share as the ones seen so far.
    /// </summary>
    public TimeSpan? Update(DateTime now, IngestionProgress progress)
    {
        double handled = progress.ProcessedFiles + progress.InFlightFiles;
        double worked = Math.Max(0, handled - progress.QuickFiles);
        double share = handled > 0 ? worked / handled : 0;
        double remaining = Math.Max(0, progress.TotalFiles - handled);
        return Update(now, worked, worked + remaining * share);
    }

    /// <summary>
    /// Reports the progress at <paramref name="now"/> (units may be fractions: a video that is half done counts as half a file)
    /// and returns the time left, or null while there is no basis for an estimate.
    /// </summary>
    public TimeSpan? Update(DateTime now, double done, double total)
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

        // The measuring starts when work first gets done: what came before (a first stretch with nothing to report)
        // must not count as time the work took.
        var (previousTime, previousDone) = (_previousTime, _previousDone);
        (_previousTime, _previousDone) = (now, done);
        if (!_progressing)
        {
            if (done <= _startDone) return null;

            // From the last report before the first progress, so the work that led to it is counted.
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

        double elapsed = (now - _start).TotalSeconds;
        if (elapsed < WarmUpSeconds) return null;

        var oldest = _samples.Peek();
        double windowSeconds = (now - oldest.Time).TotalSeconds;
        double rate = windowSeconds >= WarmUpSeconds && done > oldest.Done
            ? (done - oldest.Done) / windowSeconds
            : (done - _startDone) / elapsed;
        if (rate <= 0) return null;

        double remaining = Math.Max(0, total - done);
        double seconds = remaining / rate;
        return seconds > TimeSpan.MaxValue.TotalSeconds / 2 ? null : TimeSpan.FromSeconds(seconds);
    }
}
