using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using Microsoft.Data.Sqlite;
using static EmbeddingSearchViewer.Infrastructure.Data.SqliteHelpers;
using static EmbeddingSearchViewer.Infrastructure.Data.SqliteRetry;

namespace EmbeddingSearchViewer.Infrastructure.Data;

public partial class SqliteMediaRepository
{
    /// <summary>Upper bound on assets in one bulk-save transaction, so a transaction never grows without limit.</summary>
    private const int MaxAssetsPerTx = 150;

    /// <summary>Upper bound on chunk rows in one bulk-save transaction (an asset with many chunks splits on its own).</summary>
    private const int MaxChunksPerTx = 800;

    /// <summary>The savepoint a bulk save wraps each asset in, so a bad asset rolls back only itself.</summary>
    private const string SavepointName = "sp_asset";

    /// <summary>
    /// Diagnostics for tests: how many transactions the most recent <see cref="SaveAssetsWithChunksAsync"/> call used.
    /// It exists so a test can assert the run is sliced into bounded transactions without inspecting SQLite internals.
    /// </summary>
    internal int LastBulkSaveTransactionCount { get; private set; }

    public async Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        const string sql = "SELECT Id, FilePath, FileName, MediaType, FileSizeBytes, FileHash, LastModifiedUtc, ThumbnailPath, DurationOrPages, IndexedAtUtc FROM MediaAssets WHERE FilePath = @path;";
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@path", filePath);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadAsset(reader);
        }
        return null;
    }

    public async Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        const string sql = "SELECT Id, FilePath, FileName, MediaType, FileSizeBytes, FileHash, LastModifiedUtc, ThumbnailPath, DurationOrPages, IndexedAtUtc FROM MediaAssets WHERE Id = @id;";
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", assetId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            return ReadAsset(reader);
        }
        return null;
    }

    public Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteReadWithRetryAsync<IReadOnlyList<MediaAsset>>(async conn =>
        {
            const string sql = "SELECT Id, FilePath, FileName, MediaType, FileSizeBytes, FileHash, LastModifiedUtc, ThumbnailPath, DurationOrPages, IndexedAtUtc FROM MediaAssets;";
            await using var cmd = new SqliteCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var list = new List<MediaAsset>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(ReadAsset(reader));
            }
            return list;
        }, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAssetHashesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        await using var cmd = new SqliteCommand("SELECT FilePath, FileHash FROM MediaAssets;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    public async Task<AssetStats> GetAssetStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        await using var cmd = new SqliteCommand("SELECT MediaType, COUNT(*) FROM MediaAssets GROUP BY MediaType;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        int total = 0, images = 0, pdfs = 0, videos = 0, documents = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            int count = reader.GetInt32(1);
            total += count;
            switch (reader.GetString(0))
            {
                case nameof(MediaType.Image): images = count; break;
                case nameof(MediaType.Pdf): pdfs = count; break;
                case nameof(MediaType.Video): videos = count; break;
                case nameof(MediaType.Document): documents = count; break;
            }
        }
        return new AssetStats(total, images, pdfs, videos, documents);
    }

    private const string InsertAssetSql = """
        INSERT INTO MediaAssets (Id, FilePath, FileName, MediaType, FileSizeBytes, FileHash, LastModifiedUtc, ThumbnailPath, DurationOrPages, IndexedAtUtc)
        VALUES (@Id, @FilePath, @FileName, @MediaType, @FileSizeBytes, @FileHash, @LastModifiedUtc, @ThumbnailPath, @DurationOrPages, @IndexedAtUtc);
        """;

    private const string InsertChunkSql = """
        INSERT INTO MediaChunks (Id, AssetId, Modality, ChunkIndex, PageNumber, StartTimeMs, EndTimeMs, SnippetText, PreviewImagePath, Embedding, FileName)
        VALUES (@Id, @AssetId, @Modality, @ChunkIndex, @PageNumber, @StartTimeMs, @EndTimeMs, @SnippetText, @PreviewImagePath, @Embedding, @FileName);
        """;

    public async Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default)
    {
        var list = chunks as IReadOnlyList<MediaChunk> ?? chunks.ToList();

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);
            try
            {
                await ExecuteWithRetryAsync(async () =>
                {
                    var tx = conn.BeginTransaction();
                    try
                    {
                        await InsertAssetCoreAsync(tx, asset, list, cancellationToken);
                        await tx.CommitAsync(cancellationToken);
                    }
                    finally
                    {
                        await tx.DisposeAsync();
                    }
                }, cancellationToken);
            }
            catch
            {
                // A failed transaction can leave the connection in a state the next statement must not inherit.
                ResetWriteConnection();
                throw;
            }
            Interlocked.Increment(ref _dataVersion);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Stores many assets with as few transactions as possible: the run is sliced into bounded transactions (at most
    /// <see cref="MaxAssetsPerTx"/> assets and <see cref="MaxChunksPerTx"/> chunk rows each), and each asset is wrapped
    /// in a SAVEPOINT so a single bad asset rolls back only itself. Failures are returned, never thrown, so one broken
    /// asset cannot take its neighbours (or the run) down with it.
    /// </summary>
    public async Task<IReadOnlyList<AssetSaveFailure>> SaveAssetsWithChunksAsync(
        IReadOnlyList<AssetChunkBatch> items, CancellationToken cancellationToken = default)
    {
        var failures = new List<AssetSaveFailure>();
        if (items is null || items.Count == 0)
        {
            LastBulkSaveTransactionCount = 0;
            return failures;
        }

        int index = 0;
        int transactions = 0;
        while (index < items.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Slice the run into one bounded transaction. An asset with more chunks than the limit still gets its own
            // transaction (the inner guard is skipped for the first asset of a slice).
            int start = index;
            int chunksInTx = 0;
            while (index < items.Count && index - start < MaxAssetsPerTx)
            {
                int chunkCount = items[index].Chunks?.Count ?? 0;
                if (index > start && chunksInTx + chunkCount > MaxChunksPerTx) break;
                chunksInTx += chunkCount;
                index++;
            }

            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                var conn = await EnsureWriteConnectionAsync(cancellationToken);
                bool changed = false;
                IReadOnlyList<AssetSaveFailure> txFailures;
                try
                {
                    txFailures = await ExecuteWithRetryAsync(async () =>
                    {
                        // Failures are collected per attempt so a transient-lock retry never double-reports them (only
                        // the attempt that commits contributes). Per-asset isolation is unchanged: a bad asset is
                        // rolled back to its SAVEPOINT and reported, while a whole-transaction lock error is retried
                        // at this level instead of being turned into a per-asset failure.
                        var attemptFailures = new List<AssetSaveFailure>();
                        var tx = conn.BeginTransaction();
                        try
                        {
                            bool attemptChanged = false;
                            for (int i = start; i < index; i++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var item = items[i];
                                var chunks = item.Chunks ?? Array.Empty<MediaChunk>();

                                await RunAsync(_savepointCmd!, tx, cancellationToken);
                                try
                                {
                                    await InsertAssetCoreAsync(tx, item.Asset, chunks, cancellationToken);
                                    await RunAsync(_releaseCmd!, tx, cancellationToken);
                                    attemptChanged = true;
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    // Undo just this asset and carry on: a bad asset must not discard the whole bundle.
                                    await RunAsync(_rollbackCmd!, tx, cancellationToken);
                                    attemptFailures.Add(new AssetSaveFailure(item.Asset.Id, item.Asset.FilePath, ex.Message));
                                }
                            }

                            await tx.CommitAsync(cancellationToken);
                            changed = attemptChanged;
                            return attemptFailures;
                        }
                        finally
                        {
                            await tx.DisposeAsync();
                        }
                    }, cancellationToken);
                }
                catch
                {
                    ResetWriteConnection();
                    throw;
                }

                failures.AddRange(txFailures);
                if (changed) Interlocked.Increment(ref _dataVersion);
            }
            finally
            {
                _writeGate.Release();
            }

            transactions++;
        }

        LastBulkSaveTransactionCount = transactions;
        return failures;
    }

    /// <summary>
    /// Writes one asset and its chunks using the writer connection's cached commands. Callers hold <see cref="_writeGate"/>
    /// and pass the active transaction. The delete is a plain DELETE (never INSERT OR REPLACE): MediaChunksFts is
    /// content-sync and kept in step by the AFTER DELETE trigger on MediaChunks, so its entries go with the chunk rows.
    /// Every statement is a keyed lookup (Id PK / FilePath UNIQUE / IX_MediaChunks_AssetId).
    /// </summary>
    private async Task InsertAssetCoreAsync(SqliteTransaction tx, MediaAsset asset, IReadOnlyList<MediaChunk> chunks, CancellationToken cancellationToken)
    {
        var del = _saveDeleteCmd!;
        del.Parameters["@assetId"].Value = asset.Id.ToString();
        del.Parameters["@filePath"].Value = asset.FilePath;
        await RunAsync(del, tx, cancellationToken);

        var insertAsset = _saveInsertAssetCmd!;
        insertAsset.Parameters["@Id"].Value = asset.Id.ToString();
        insertAsset.Parameters["@FilePath"].Value = asset.FilePath;
        insertAsset.Parameters["@FileName"].Value = asset.FileName;
        insertAsset.Parameters["@MediaType"].Value = asset.MediaType.ToString();
        insertAsset.Parameters["@FileSizeBytes"].Value = asset.FileSizeBytes;
        insertAsset.Parameters["@FileHash"].Value = asset.FileHash;
        insertAsset.Parameters["@LastModifiedUtc"].Value = asset.LastModifiedUtc.ToString("O");
        insertAsset.Parameters["@ThumbnailPath"].Value = (object?)asset.ThumbnailPath ?? DBNull.Value;
        insertAsset.Parameters["@DurationOrPages"].Value = (object?)asset.DurationOrPages ?? DBNull.Value;
        insertAsset.Parameters["@IndexedAtUtc"].Value = asset.IndexedAtUtc.ToString("O");
        await RunAsync(insertAsset, tx, cancellationToken);

        // FileName is denormalized onto each chunk so the content-sync FTS can read it back; the FTS entry itself is
        // written by the AFTER INSERT trigger, not here.
        var insertChunk = _saveInsertChunkCmd!;
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            insertChunk.Parameters["@Id"].Value = chunk.Id.ToString();
            insertChunk.Parameters["@AssetId"].Value = asset.Id.ToString();
            insertChunk.Parameters["@Modality"].Value = chunk.Modality.ToString();
            insertChunk.Parameters["@ChunkIndex"].Value = chunk.ChunkIndex;
            insertChunk.Parameters["@PageNumber"].Value = (object?)chunk.PageNumber ?? DBNull.Value;
            insertChunk.Parameters["@StartTimeMs"].Value = (object?)chunk.StartTimeMs ?? DBNull.Value;
            insertChunk.Parameters["@EndTimeMs"].Value = (object?)chunk.EndTimeMs ?? DBNull.Value;
            insertChunk.Parameters["@SnippetText"].Value = (object?)chunk.SnippetText ?? DBNull.Value;
            insertChunk.Parameters["@PreviewImagePath"].Value = (object?)chunk.PreviewImagePath ?? DBNull.Value;
            insertChunk.Parameters["@Embedding"].Value = ConvertFloatArrayToBytes(chunk.Embedding);
            insertChunk.Parameters["@FileName"].Value = asset.FileName;
            await RunAsync(insertChunk, tx, cancellationToken);
        }
    }

    public async Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);
            try
            {
                // While the triggers are dropped the chunk rows go without their index entries. The slow phase rebuilds
                // the index once after the catch-up to drop those entries (see RebuildFtsIndexAsync).
                if (_ftsRebuildNeeded) _ftsDeleteWhileRebuilding = true;

                var tx = conn.BeginTransaction();
                try
                {
                    _deleteAssetCmd!.Parameters["@assetId"].Value = assetId.ToString();
                    await RunAsync(_deleteAssetCmd, tx, cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }
                finally
                {
                    await tx.DisposeAsync();
                }
            }
            catch
            {
                ResetWriteConnection();
                throw;
            }
            Interlocked.Increment(ref _dataVersion);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task DeleteAssetsAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default)
    {
        var ids = assetIds.ToList();
        if (ids.Count == 0) return;

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);
            try
            {
                // While the triggers are dropped the chunk rows go without their index entries. The slow phase rebuilds
                // the index once after the catch-up to drop those entries (see RebuildFtsIndexAsync).
                if (_ftsRebuildNeeded) _ftsDeleteWhileRebuilding = true;

                var tx = conn.BeginTransaction();
                try
                {
                    // Delete in batches of up to 500 to stay well within SQLite parameter limits. The placeholder count
                    // varies, so this command is not cached.
                    const int batchSize = 500;
                    for (int i = 0; i < ids.Count; i += batchSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var chunk = ids.Skip(i).Take(batchSize).ToList();
                        var placeholders = string.Join(",", chunk.Select((_, idx) => $"@id{idx}"));

                        // The AFTER DELETE trigger on MediaChunks removes the matching FTS entries automatically.
                        string sql = $"""
                            DELETE FROM MediaChunks WHERE AssetId IN ({placeholders});
                            DELETE FROM MediaAssets WHERE Id IN ({placeholders});
                        """;

                        await using var cmd = new SqliteCommand(sql, conn, tx);
                        for (int idx = 0; idx < chunk.Count; idx++)
                        {
                            cmd.Parameters.AddWithValue($"@id{idx}", chunk[idx].ToString());
                        }
                        await cmd.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await tx.CommitAsync(cancellationToken);
                }
                finally
                {
                    await tx.DisposeAsync();
                }
            }
            catch
            {
                ResetWriteConnection();
                throw;
            }
            Interlocked.Increment(ref _dataVersion);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);
            try
            {
                var tx = conn.BeginTransaction();
                try
                {
                    // The delete triggers are dropped while the full-text index is rebuilt, so the chunk delete below
                    // could not clear the index. The content rows still exist here, which is what lets FTS5 drop every
                    // entry (a delete after the chunk rows are gone cannot find the values it has to remove).
                    if (_ftsRebuildNeeded)
                    {
                        await using var clearFts = new SqliteCommand("DELETE FROM MediaChunksFts;", conn, tx);
                        await clearFts.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await RunAsync(_clearCmd!, tx, cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                }
                finally
                {
                    await tx.DisposeAsync();
                }
            }
            catch
            {
                ResetWriteConnection();
                throw;
            }
            Interlocked.Increment(ref _dataVersion);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default)
    {
        return ExecuteReadWithRetryAsync<IReadOnlyList<MediaChunk>>(async conn =>
        {
            string sql = """
                SELECT c.Id, c.AssetId, c.Modality, c.ChunkIndex, c.PageNumber, c.StartTimeMs, c.EndTimeMs, c.SnippetText, c.PreviewImagePath, c.Embedding
                FROM MediaChunks c
                INNER JOIN MediaAssets a ON c.AssetId = a.Id
            """;
            if (mediaTypeFilter.HasValue)
            {
                sql += " WHERE a.MediaType = @mediaType;";
            }

            await using var cmd = new SqliteCommand(sql, conn);
            if (mediaTypeFilter.HasValue)
            {
                cmd.Parameters.AddWithValue("@mediaType", mediaTypeFilter.Value.ToString());
            }

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaChunk>();
            while (await reader.ReadAsync(cancellationToken))
            {
                byte[]? blob = reader.IsDBNull(9) ? null : (byte[])reader[9];
                var embedding = blob != null ? ConvertBytesToFloatArray(blob) : Array.Empty<float>();

                list.Add(new MediaChunk
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    AssetId = Guid.Parse(reader.GetString(1)),
                    Modality = Enum.Parse<ModalityType>(reader.GetString(2)),
                    ChunkIndex = reader.GetInt32(3),
                    PageNumber = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    StartTimeMs = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    EndTimeMs = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    SnippetText = reader.IsDBNull(7) ? null : reader.GetString(7),
                    PreviewImagePath = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Embedding = embedding
                });
            }
            return list;
        }, cancellationToken);
    }

    /// <summary>
    /// Every chunk's metadata without the embedding blob: the same rows, the same order, but the 768-float column is
    /// never read or decoded. That keeps a cold search's load to the metadata only (tens of MB at hundreds of thousands
    /// of chunks) instead of the whole embedding table (~1.8 GB at 600k x 768). Vectors are fetched per hit instead,
    /// see <see cref="GetEmbeddingsByChunkIdsAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<MediaChunk>> GetAllChunksMetadataAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default)
    {
        return ExecuteReadWithRetryAsync<IReadOnlyList<MediaChunk>>(async conn =>
        {
            string sql = """
                SELECT c.Id, c.AssetId, c.Modality, c.ChunkIndex, c.PageNumber, c.StartTimeMs, c.EndTimeMs, c.SnippetText, c.PreviewImagePath
                FROM MediaChunks c
                INNER JOIN MediaAssets a ON c.AssetId = a.Id
            """;
            if (mediaTypeFilter.HasValue)
            {
                sql += " WHERE a.MediaType = @mediaType;";
            }

            await using var cmd = new SqliteCommand(sql, conn);
            if (mediaTypeFilter.HasValue)
            {
                cmd.Parameters.AddWithValue("@mediaType", mediaTypeFilter.Value.ToString());
            }

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaChunk>();
            while (await reader.ReadAsync(cancellationToken))
            {
                list.Add(new MediaChunk
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    AssetId = Guid.Parse(reader.GetString(1)),
                    Modality = Enum.Parse<ModalityType>(reader.GetString(2)),
                    ChunkIndex = reader.GetInt32(3),
                    PageNumber = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    StartTimeMs = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    EndTimeMs = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    SnippetText = reader.IsDBNull(7) ? null : reader.GetString(7),
                    PreviewImagePath = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Embedding = Array.Empty<float>()
                });
            }
            return list;
        }, cancellationToken);
    }

    /// <summary>
    /// The embeddings of the given chunks, by id. <c>Id</c> is the primary key (a TEXT unique index), so each id is a
    /// rowid lookup; ids are read in batches to stay within SQLite's parameter limit. Only the requested column is
    /// read, so the cost is proportional to <paramref name="chunkIds"/> (the vector-index hits), never the whole table.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, float[]>> GetEmbeddingsByChunkIdsAsync(IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, float[]>();
        if (chunkIds is null || chunkIds.Count == 0) return result;

        const int batchSize = 500;
        for (int start = 0; start < chunkIds.Count; start += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int offset = start;
            int count = Math.Min(batchSize, chunkIds.Count - offset);

            // The delegate reads into `result` (a retry re-reads the same rows, so it only overwrites).
            await ExecuteReadWithRetryAsync<object?>(async conn =>
            {
                var placeholders = string.Join(",", Enumerable.Range(0, count).Select(idx => $"@id{idx}"));
                var sql = $"SELECT Id, Embedding FROM MediaChunks WHERE Id IN ({placeholders});";
                await using var cmd = new SqliteCommand(sql, conn);
                for (int idx = 0; idx < count; idx++)
                {
                    cmd.Parameters.AddWithValue($"@id{idx}", chunkIds[offset + idx].ToString());
                }

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = Guid.Parse(reader.GetString(0));
                    byte[]? blob = reader.IsDBNull(1) ? null : (byte[])reader[1];
                    result[id] = blob != null ? ConvertBytesToFloatArray(blob) : Array.Empty<float>();
                }
                return null;
            }, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// Every chunk (with its embedding) of one asset. <c>MediaChunks.AssetId</c> is indexed
    /// (<c>IX_MediaChunks_AssetId</c>), so this reads only that asset's rows (a few thousand at most) instead of the
    /// whole table, which is what makes a search restricted to a single file fast. Rows come in <c>ChunkIndex</c>
    /// order so a video's scenes are returned in their natural order.
    /// </summary>
    public Task<IReadOnlyList<MediaChunk>> GetChunksByAssetIdAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        return ExecuteReadWithRetryAsync<IReadOnlyList<MediaChunk>>(async conn =>
        {
            const string sql = """
                SELECT Id, AssetId, Modality, ChunkIndex, PageNumber, StartTimeMs, EndTimeMs, SnippetText, PreviewImagePath, Embedding
                FROM MediaChunks
                WHERE AssetId = @assetId
                ORDER BY ChunkIndex;
                """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@assetId", assetId.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var list = new List<MediaChunk>();
            while (await reader.ReadAsync(cancellationToken))
            {
                byte[]? blob = reader.IsDBNull(9) ? null : (byte[])reader[9];
                var embedding = blob != null ? ConvertBytesToFloatArray(blob) : Array.Empty<float>();

                list.Add(new MediaChunk
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    AssetId = Guid.Parse(reader.GetString(1)),
                    Modality = Enum.Parse<ModalityType>(reader.GetString(2)),
                    ChunkIndex = reader.GetInt32(3),
                    PageNumber = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    StartTimeMs = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    EndTimeMs = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    SnippetText = reader.IsDBNull(7) ? null : reader.GetString(7),
                    PreviewImagePath = reader.IsDBNull(8) ? null : reader.GetString(8),
                    Embedding = embedding
                });
            }
            return list;
        }, cancellationToken);
    }

    private static MediaAsset ReadAsset(SqliteDataReader reader)
    {
        return new MediaAsset
        {
            Id = Guid.Parse(reader.GetString(0)),
            FilePath = reader.GetString(1),
            FileName = reader.GetString(2),
            MediaType = Enum.Parse<MediaType>(reader.GetString(3)),
            FileSizeBytes = reader.GetInt64(4),
            FileHash = reader.GetString(5),
            LastModifiedUtc = DateTime.Parse(reader.GetString(6)),
            ThumbnailPath = reader.IsDBNull(7) ? null : reader.GetString(7),
            DurationOrPages = reader.IsDBNull(8) ? null : reader.GetInt32(8),
            IndexedAtUtc = DateTime.Parse(reader.GetString(9))
        };
    }
}
