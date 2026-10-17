using System;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;

namespace EmbeddingSearchViewer.Core.Services;

/// <summary>
/// A synchronous / in-thread <see cref="IUiDispatcher"/> implementation for headless testing or non-UI environments.
/// </summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public static ImmediateDispatcher Instance { get; } = new();

    public bool HasThreadAccess => true;

    public void Enqueue(Action action) => action();

    public Task EnqueueAsync(Func<Task> function) => function();
}
