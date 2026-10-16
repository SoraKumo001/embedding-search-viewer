using System;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;

namespace EmbeddingSearchViewer.Infrastructure.Library;

/// <summary>
/// Calls a refresh action every <c>interval</c> while it is running, but only when the indexed data has changed since the
/// last call (the repository's <see cref="IMediaRepository.DataVersion"/>). Refreshes never overlap: the next tick waits
/// for the previous refresh to finish. Used to keep the library statistics current while an index is being built.
/// </summary>
public sealed class LibraryStatsRefresher : IDisposable
{
    private readonly IMediaRepository _repository;
    private readonly Func<Task> _refresh;
    private readonly TimeSpan _interval;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public LibraryStatsRefresher(IMediaRepository repository, Func<Task> refresh, TimeSpan interval)
    {
        _repository = repository;
        _refresh = refresh;
        _interval = interval;
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
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                long version = _repository.DataVersion;
                // A negative version means the repository cannot tell what changed: refresh on every tick.
                if (version >= 0 && version == lastVersion) continue;
                lastVersion = version;

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
