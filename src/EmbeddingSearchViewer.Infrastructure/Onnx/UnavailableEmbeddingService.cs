using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>
/// Stands in for the embedding engine when it could not be created (e.g. the model files are missing),
/// so the app still starts and every operation reports the reason instead of crashing.
/// </summary>
public sealed class UnavailableEmbeddingService : IEmbeddingService
{
    public string Reason { get; }

    public UnavailableEmbeddingService(string reason)
    {
        Reason = reason;
    }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Reason);

    public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Reason);

    /// <summary>Why the given engine is unavailable, or null when it works.</summary>
    public static string? ReasonOf(IEmbeddingService service) => service switch
    {
        EmbeddingServiceHost host => host.UnavailableReason,
        UnavailableEmbeddingService unavailable => unavailable.Reason,
        _ => null
    };

    public static string DescribeFailure(Exception ex) => ex switch
    {
        System.IO.FileNotFoundException =>
            $"埋め込みモデルが見つかりません。メニューの「モデル」からダウンロードするか、models フォルダにモデルを配置してください。({ex.Message})",
        _ => $"埋め込みエンジンを初期化できませんでした: {ex.Message}"
    };
}
