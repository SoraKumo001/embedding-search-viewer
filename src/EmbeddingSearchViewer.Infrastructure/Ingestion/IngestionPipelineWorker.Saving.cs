using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Infrastructure.Core;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

public partial class IngestionPipelineWorker
{
    private async Task SaveAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            await _repository.SaveAssetWithChunksAsync(asset, chunks, cancellationToken);
            if (_knownHashes is { IsCompletedSuccessfully: true } loaded) loaded.Result[asset.FilePath] = asset.FileHash;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>
    /// Stores a batch of assets in as few transactions as possible. The repository isolates a single failing asset, so
    /// the rest of the batch still lands; this returns which ones failed (empty when all were stored).
    /// </summary>
    private async Task<IReadOnlyList<AssetSaveFailure>> SaveBatchAsync(IReadOnlyList<AssetChunkBatch> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return Array.Empty<AssetSaveFailure>();

        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var failures = await _repository.SaveAssetsWithChunksAsync(items, cancellationToken);

            // Only the assets that actually landed belong in the hash cache; a failed one is retried on the next run.
            if (_knownHashes is { IsCompletedSuccessfully: true } loaded)
            {
                var failedIds = failures.Count > 0 ? new HashSet<Guid>(failures.Select(f => f.AssetId)) : null;
                foreach (var item in items)
                {
                    if (failedIds != null && failedIds.Contains(item.Asset.Id)) continue;
                    loaded.Result[item.Asset.FilePath] = item.Asset.FileHash;
                }
            }
            return failures;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    // The hash of every file that was indexed before this run (plus those saved during it), read with one query. Checking
    // each file with its own query takes minutes for a library of hundreds of thousands of files.
    private Task<ConcurrentDictionary<string, string>>? _knownHashes;

    private Task<ConcurrentDictionary<string, string>> KnownHashesAsync()
    {
        lock (_stateLock)
        {
            if (_knownHashes is { IsFaulted: true } or { IsCanceled: true }) _knownHashes = null; // try again
            return _knownHashes ??= Task.Run(async () =>
                new ConcurrentDictionary<string, string>(await _repository.GetAssetHashesAsync(), StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>True when the file is in the index with the same size and modification time.</summary>
    private async Task<bool> IsUnchangedAsync(string filePath, string fileHash, CancellationToken cancellationToken)
    {
        var known = await KnownHashesAsync().WaitAsync(cancellationToken);
        return known.TryGetValue(filePath, out var indexed) && indexed == fileHash;
    }
}
