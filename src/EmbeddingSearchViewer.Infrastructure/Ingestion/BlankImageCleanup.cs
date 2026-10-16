using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>Finds images in an existing index that are almost a single colour (blank pages) so they can be removed.</summary>
public static class BlankImageCleanup
{
    /// <summary>
    /// Checks the image assets whose files still exist. Missing files are not reported (they are handled by the
    /// "remove missing files" action). Runs several images at a time because decoding dominates the cost.
    /// </summary>
    public static async Task<List<MediaAsset>> FindBlankImagesAsync(
        IEnumerable<MediaAsset> assets,
        IProgress<int>? checkedCount = null,
        CancellationToken cancellationToken = default)
    {
        var candidates = assets.Where(a => a.MediaType == MediaType.Image && File.Exists(a.FilePath)).ToList();
        var blank = new List<MediaAsset>();
        int done = 0;

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = cancellationToken },
            (asset, _) =>
            {
                if (Gemma4ImagePreprocessor.IsNearlyBlank(asset.FilePath))
                {
                    lock (blank) blank.Add(asset);
                }
                checkedCount?.Report(Interlocked.Increment(ref done));
                return ValueTask.CompletedTask;
            });

        return blank.OrderBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
