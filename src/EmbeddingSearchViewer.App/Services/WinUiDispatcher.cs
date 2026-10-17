using System;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using Microsoft.UI.Dispatching;

namespace EmbeddingSearchViewer_App.Services;

/// <summary>
/// Implements <see cref="IUiDispatcher"/> using WinUI 3's <see cref="DispatcherQueue"/>.
/// </summary>
public sealed class WinUiDispatcher : IUiDispatcher
{
    private DispatcherQueue? _queue;

    public void Initialize(DispatcherQueue queue)
    {
        _queue = queue;
    }

    public bool HasThreadAccess => _queue?.HasThreadAccess ?? true;

    public void Enqueue(Action action)
    {
        var q = _queue ?? App.MainWindowDispatcherQueue;
        if (q == null || q.HasThreadAccess)
        {
            action();
            return;
        }

        q.TryEnqueue(DispatcherQueuePriority.Normal, () => action());
    }

    public Task EnqueueAsync(Func<Task> function)
    {
        var q = _queue ?? App.MainWindowDispatcherQueue;
        if (q == null || q.HasThreadAccess)
        {
            return function();
        }

        var tcs = new TaskCompletionSource();
        if (!q.TryEnqueue(async () =>
        {
            try
            {
                await function();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }))
        {
            tcs.SetCanceled();
        }

        return tcs.Task;
    }
}
