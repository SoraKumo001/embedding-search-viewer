using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Core.Interfaces;

public record EmbeddingItemInput(
    string Type,
    string? Text = null,
    string? Title = null,
    string? Path = null,
    string? VideoPath = null,
    string? ThumbnailPath = null,
    int? StartMs = null,
    int? EndMs = null
);

public interface IEmbeddingService
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
    Task<float[]> GenerateQueryEmbeddingAsync(
        string query,
        string modality = EmbeddingModalities.Text,
        int truncateDim = 768,
        CancellationToken cancellationToken = default
    );
    /// <summary>
    /// Embeds the items in order. An empty array for an item means the service chose not to embed it
    /// (for example an image that is a single plain colour); callers must not store such items.
    /// </summary>
    Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<EmbeddingItemInput> items,
        int truncateDim = 768,
        CancellationToken cancellationToken = default
    );
}
