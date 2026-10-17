using System;
using System.Threading.Tasks;

namespace EmbeddingSearchViewer.Core.Interfaces;

/// <summary>
/// Dispatches work to the UI thread in an abstract way, enabling headless unit testing.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Gets a value indicating whether the current thread is the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Enqueues an action to run on the UI thread.</summary>
    void Enqueue(Action action);

    /// <summary>Enqueues an asynchronous action to run on the UI thread and awaits its completion.</summary>
    Task EnqueueAsync(Func<Task> function);
}
