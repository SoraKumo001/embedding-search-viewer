using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>
/// Calls a refresh action every <c>interval</c> while it is running, but only when the indexed data has changed since the
/// last call (the repository's <see cref="IMediaRepository.DataVersion"/>). Refreshes never overlap: the next tick waits
/// for the previous refresh to finish. Used to keep the library statistics current while an index is being built.
/// When <c>minInterval</c> is set, refreshes are thinned to at most one per that period even while the data keeps
/// changing, so a busy index does not push a refresh on every tick.
/// </summary>
public sealed class LibraryStatsRefresher : IDisposable
{
    private readonly IMediaRepository _repository;
    private readonly Func<Task> _refresh;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _minInterval;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public LibraryStatsRefresher(IMediaRepository repository, Func<Task> refresh, TimeSpan interval, TimeSpan? minInterval = null)
    {
        _repository = repository;
        _refresh = refresh;
        _interval = interval;
        _minInterval = minInterval ?? TimeSpan.Zero;
    }

    public bool IsRunning
    {
        get { lock (_lock) return _cts != null; }
    }

    /// <summary>Starts ticking; a no-op when already running. What is on screen now counts as up to date.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            long baseline = _repository.DataVersion;
            _ = Task.Run(() => RunAsync(baseline, _cts.Token));
        }
    }

    /// <summary>Stops ticking. A refresh that is already running is not interrupted.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    private async Task RunAsync(long lastVersion, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        var sinceRefresh = Stopwatch.StartNew();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                long version = _repository.DataVersion;
                // A negative version means the repository cannot tell what changed: every tick counts as a change.
                if (version >= 0 && version == lastVersion) continue;

                // Thinning: while the data keeps changing, do not refresh faster than the minimum interval. The change
                // is not forgotten (lastVersion is left as it is), so a later tick picks it up.
                if (_minInterval > TimeSpan.Zero && sinceRefresh.Elapsed < _minInterval) continue;

                lastVersion = version;
                sinceRefresh.Restart();

                try
                {
                    await _refresh();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The statistics are informational; a failed refresh is simply tried again on the next change.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    public void Dispose() => Stop();
}
