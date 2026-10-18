using System;
using System.Collections.Generic;

namespace EmbeddingSearchViewer.Core.Models;

/// <summary>
/// One asset together with the chunks to store for it: the unit a batch save works on. The chunks are a materialized
/// list so a batch can split its transactions by the number of chunk rows without enumerating lazy sequences twice.
/// </summary>
public readonly record struct AssetChunkBatch(MediaAsset Asset, IReadOnlyList<MediaChunk> Chunks);

/// <summary>
/// An asset a batch save could not store. The batch keeps going, so one bad asset never takes its neighbours with it;
/// the caller reports the failures and moves on.
/// </summary>
public readonly record struct AssetSaveFailure(Guid AssetId, string FilePath, string Error);
