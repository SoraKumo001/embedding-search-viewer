using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>
/// The embedding service the rest of the app talks to. It starts as <see cref="UnavailableEmbeddingService"/> while
/// the engine is being created (the model files are read and the ONNX sessions are built, which takes a moment) and
/// is switched to the real engine once that is done, so the app starts right away. It is also switched to the real
/// engine by <see cref="ReloadAsync"/> after the model files have been downloaded, so searching and indexing work
/// without restarting the app.
/// </summary>
public sealed class EmbeddingServiceHost : IEmbeddingService, IDisposable
{
    private readonly Func<IEmbeddingService> _create;
    private IEmbeddingService _inner;

    /// <summary>The creation of the engine. It is awaited before the engine is used.</summary>
    private readonly Task _initTask;

    /// <summary>Raised (on a background thread) after the engine was replaced.</summary>
    public event Action? Changed;

    public EmbeddingServiceHost(Func<IEmbeddingService>? create = null)
    {
        _create = create ?? CreateDefault;
        // The engine is created in the background: until it is there the host reports that it is still initializing.
        _inner = new UnavailableEmbeddingService(Loc.T("Library.Engine.Initializing"));
        _initTask = Task.Run(async () =>
        {
            var next = SafeCreate();
            var previous = Interlocked.Exchange(ref _inner, next);
            (previous as IDisposable)?.Dispose();
            Changed?.Invoke();
        });
    }

    public bool IsAvailable => _inner is not UnavailableEmbeddingService;

    /// <summary>Why the engine is unavailable, or null while it is available.</summary>
    public string? UnavailableReason => (_inner as UnavailableEmbeddingService)?.Reason;

    /// <summary>True while the engine is still being created.</summary>
    public bool IsInitializing => !_initTask.IsCompleted;

    /// <summary>True when the engine could not be created because the model files are missing.</summary>
    public bool ModelFilesMissing => !IsAvailable && EmbeddingModelLocation.Find() == null;

    /// <summary>Diagnostics line of the underlying ONNX engine, or null when another engine (or none) is active.</summary>
    public string? DescribeEmbeddingStats() => (_inner as OnnxDirectMlEmbeddingService)?.DescribeStats();

    private static IEmbeddingService CreateDefault() => new OnnxDirectMlEmbeddingService(deviceId: 0);

    private IEmbeddingService SafeCreate()
    {
        try
        {
            return _create();
        }
        catch (Exception ex)
        {
            return new UnavailableEmbeddingService(UnavailableEmbeddingService.DescribeFailure(ex));
        }
    }

    /// <summary>Creates the engine again (after a download or a repair) and swaps it in.</summary>
    public Task ReloadAsync()
    {
        return Task.Run(async () =>
        {
            // The engine of the start-up is done before this one is created, so the two do not fight over the files.
            await _initTask.ConfigureAwait(false);

            var next = SafeCreate();
            var previous = Interlocked.Exchange(ref _inner, next);
            (previous as IDisposable)?.Dispose();
            Changed?.Invoke();
        });
    }

    /// <summary>
    /// Waits for the engine to be created and then asks it whether it is ready. The wait is the point: before the
    /// engine is there the answer is "not yet", not "never".
    /// </summary>
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        await _initTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.IsReadyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
    {
        await _initTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GenerateQueryEmbeddingAsync(query, modality, truncateDim, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
    {
        await _initTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await _inner.GenerateBatchEmbeddingsAsync(items, truncateDim, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
