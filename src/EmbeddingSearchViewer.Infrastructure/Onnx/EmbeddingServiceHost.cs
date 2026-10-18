using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>
/// The embedding service the rest of the app talks to. It starts as <see cref="UnavailableEmbeddingService"/> when the
/// model files are missing and is switched to the real engine by <see cref="ReloadAsync"/> once they have been downloaded,
/// so searching and indexing work without restarting the app.
/// </summary>
public sealed class EmbeddingServiceHost : IEmbeddingService, IDisposable
{
    private readonly Func<IEmbeddingService> _create;
    private IEmbeddingService _inner;

    /// <summary>Raised (on a background thread) after the engine was replaced.</summary>
    public event Action? Changed;

    public EmbeddingServiceHost(Func<IEmbeddingService>? create = null)
    {
        _create = create ?? CreateDefault;
        _inner = SafeCreate();
    }

    public bool IsAvailable => _inner is not UnavailableEmbeddingService;

    /// <summary>Why the engine is unavailable, or null while it is available.</summary>
    public string? UnavailableReason => (_inner as UnavailableEmbeddingService)?.Reason;

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
        return Task.Run(() =>
        {
            var next = SafeCreate();
            var previous = Interlocked.Exchange(ref _inner, next);
            (previous as IDisposable)?.Dispose();
            Changed?.Invoke();
        });
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => _inner.IsReadyAsync(cancellationToken);

    public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
        => _inner.GenerateQueryEmbeddingAsync(query, modality, truncateDim, cancellationToken);

    public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
        => _inner.GenerateBatchEmbeddingsAsync(items, truncateDim, cancellationToken);

    public void Dispose() => (_inner as IDisposable)?.Dispose();
}
