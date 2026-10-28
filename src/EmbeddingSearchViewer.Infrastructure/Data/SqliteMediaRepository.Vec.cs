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
    /// <summary>
    /// Embedding width of the model (768 for the bundled Gemma/CLIP image-text encoder). The sqlite-vec table is a
    /// flat <c>float[768]</c> column, so only embeddings of exactly this width are indexed there.
    /// </summary>
    private const int EmbeddingDimensions = 768;
    private const int EmbeddingBytes = EmbeddingDimensions * sizeof(float);

    /// <summary>How many chunk vectors are inserted in one transaction while backfilling.</summary>
    private const int VecBackfillBatchSize = 5000;

    /// <summary>How many coarse (binary-quantization) candidates are rescored per requested neighbour by default.</summary>
    public const int DefaultVecOversample = 8;

    /// <summary>True when the vector index exists and can be queried.</summary>
    private volatile bool _vecIndexReady;

    /// <summary>
    /// The vec0 index: an exact <c>float[768]</c> column (Phase 1) plus a binary-quantized <c>bit[768]</c> column
    /// (Phase 2). The coarse column is a 96-byte code used for a fast KNN pre-filter that is then rescored exactly.
    /// </summary>
    private const string VecTableSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS VecMediaChunks USING vec0(
            chunk_id INTEGER PRIMARY KEY,
            embedding float[768] distance_metric=cosine,
            embedding_coarse bit[768]
        );
        """;

    /// <summary>
    /// The triggers that keep the vec0 index in step with <c>MediaChunks</c>. vec0 does not support upsert cleanly, so
    /// every write is a DELETE followed by an INSERT (never INSERT OR REPLACE). The definition is dropped and re-created
    /// on every start so a database that only has a Phase-1 index picks up the coarse-column trigger.
    /// </summary>
    private const string VecTriggerSql = """
        DROP TRIGGER IF EXISTS trg_chunks_vec_ai;
        DROP TRIGGER IF EXISTS trg_chunks_vec_ad;

        CREATE TRIGGER trg_chunks_vec_ai AFTER INSERT ON MediaChunks
        WHEN NEW.Embedding IS NOT NULL AND length(NEW.Embedding) = 3072
        BEGIN
            DELETE FROM VecMediaChunks WHERE chunk_id = NEW.rowid;
            INSERT INTO VecMediaChunks(chunk_id, embedding, embedding_coarse)
            VALUES (NEW.rowid, NEW.Embedding, vec_quantize_binary(NEW.Embedding));
        END;

        CREATE TRIGGER trg_chunks_vec_ad AFTER DELETE ON MediaChunks
        BEGIN
            DELETE FROM VecMediaChunks WHERE chunk_id = OLD.rowid;
        END;
        """;

    /// <summary>
    /// Creates the vec0 index and its triggers, migrating a Phase-1 index (without the coarse column) in place. The
    /// migration rebuilds the table and lets the backfill re-populate it, because vec0 has no in-place schema change:
    /// MediaChunks.Embedding stays the source of truth, so dropping and recreating the derived index loses nothing.
    /// </summary>
    private static async Task EnsureVecSchemaAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        bool tableExists;
        bool hasCoarseColumn;
        await using (var check = new SqliteCommand(
            "SELECT count(*) FROM pragma_table_info('VecMediaChunks') WHERE name = 'embedding_coarse';", conn))
        {
            hasCoarseColumn = Convert.ToInt64(await check.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
        }

        if (!hasCoarseColumn)
        {
            await using var existsCmd = new SqliteCommand(
                "SELECT count(*) FROM pragma_table_info('VecMediaChunks');", conn);
            tableExists = Convert.ToInt64(await existsCmd.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
            if (tableExists)
            {
                await using var drop = new SqliteCommand("DROP TABLE VecMediaChunks;", conn);
                await drop.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var create = new SqliteCommand(VecTableSql, conn))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var triggers = new SqliteCommand(VecTriggerSql, conn))
        {
            await triggers.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Inserts the vectors (exact and coarse) of chunks that are not in the index yet (rows indexed before the index
    /// existed, after a vec0 build mismatch, or with a missing coarse code). A no-op once up to date. Returns how many
    /// vectors were inserted.
    /// </summary>
    public async Task<int> EnsureVecBackfillAsync(CancellationToken cancellationToken = default)
    {
        if (!VecAvailable) return 0;

        var indexed = new HashSet<long>();
        await using (var idConn = await OpenConnectionAsync(cancellationToken))
        {
            // A row with a NULL coarse vector is treated as not indexed, so a partial Phase-2 index is repaired too.
            await using var idCmd = new SqliteCommand(
                "SELECT chunk_id FROM VecMediaChunks WHERE embedding_coarse IS NOT NULL;", idConn);
            await using var idReader = await idCmd.ExecuteReaderAsync(cancellationToken);
            while (await idReader.ReadAsync(cancellationToken))
            {
                if (!idReader.IsDBNull(0)) indexed.Add(idReader.GetInt64(0));
            }
        }

        int inserted = 0;
        var batch = new List<(long RowId, byte[] Embedding)>(VecBackfillBatchSize);

        // One connection streams MediaChunks while another writes vectors: WAL lets the two run side by side.
        await using (var readConn = await OpenConnectionAsync(cancellationToken))
        await using (var writeConn = await OpenConnectionAsync(cancellationToken))
        {
            await using var selectCmd = new SqliteCommand(
                "SELECT rowid, Embedding FROM MediaChunks WHERE Embedding IS NOT NULL AND length(Embedding) = @dim;",
                readConn);
            selectCmd.Parameters.AddWithValue("@dim", EmbeddingBytes);

            await using var reader = await selectCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                long rowId = reader.GetInt64(0);
                if (indexed.Contains(rowId)) continue;

                batch.Add((rowId, (byte[])reader[1]));
                if (batch.Count >= VecBackfillBatchSize)
                {
                    inserted += await InsertVecBatchAsync(writeConn, batch, cancellationToken);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                inserted += await InsertVecBatchAsync(writeConn, batch, cancellationToken);
                batch.Clear();
            }
        }

        if (inserted > 0) Interlocked.Increment(ref _dataVersion);
        return inserted;
    }

    private static async Task<int> InsertVecBatchAsync(
        SqliteConnection conn,
        List<(long RowId, byte[] Embedding)> batch,
        CancellationToken cancellationToken)
    {
        await using var tx = conn.BeginTransaction();
        // Delete-then-insert (never INSERT OR REPLACE: vec0 upsert is unreliable) also repairs a coarse-null row.
        await using var cmd = new SqliteCommand(
            "DELETE FROM VecMediaChunks WHERE chunk_id = @id; " +
            "INSERT INTO VecMediaChunks(chunk_id, embedding, embedding_coarse) " +
            "VALUES (@id, @emb, vec_quantize_binary(@emb));", conn, tx);
        var idParam = cmd.Parameters.Add("@id", SqliteType.Integer);
        var embParam = cmd.Parameters.Add("@emb", SqliteType.Blob);

        int n = 0;
        foreach (var (rowId, embedding) in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            idParam.Value = rowId;
            embParam.Value = embedding;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            n++;
        }

        await tx.CommitAsync(cancellationToken);
        return n;
    }

    /// <summary>
    /// Nearest neighbours by exact cosine distance, best first (the Phase-1 flat KNN). Returns an empty list (never
    /// throws) when the extension is missing, the query width does not match the index, or the index is unavailable:
    /// callers then brute-force.
    /// </summary>
    public async Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecAsync(
        float[] query,
        int k,
        CancellationToken cancellationToken = default)
    {
        if (!_vecIndexReady || !VecAvailable || query is null || query.Length != EmbeddingDimensions || k <= 0)
        {
            return Array.Empty<(Guid, float)>();
        }

        try
        {
            // sqlite-vec wants the query as a little-endian float32 blob (the native layout on x64).
            byte[] queryBytes = MemoryMarshal.AsBytes(query.AsSpan()).ToArray();

            for (int attempt = 1; ; attempt++)
            {
                await using var conn = await OpenConnectionAsync(cancellationToken);
                try
                {
                    var hits = new List<(long RowId, float Distance)>();
                    const string knnSql = """
                        SELECT chunk_id, distance
                        FROM VecMediaChunks
                        WHERE embedding MATCH @q AND k = @k
                        ORDER BY distance;
                        """;
                    await using (var cmd = new SqliteCommand(knnSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@q", queryBytes);
                        cmd.Parameters.AddWithValue("@k", k);
                        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            hits.Add((reader.GetInt64(0), (float)reader.GetDouble(1)));
                        }
                    }

                    return await MapHitsToChunkIdsAsync(conn, hits, cancellationToken);
                }
                catch (SqliteException ex) when (IsTransientLock(ex) && attempt < MaxReadAttempts)
                {
                    // A transient lock is retried on a fresh connection; the final failure falls through to the
                    // empty-result fallback below, which is unchanged.
                    await Task.Delay(RetryDelayMs(attempt), cancellationToken);
                }
            }
        }
        catch
        {
            // A vec failure must never break search: report "no index results" and let the caller scan.
            return Array.Empty<(Guid, float)>();
        }
    }

    /// <summary>
    /// Nearest neighbours by exact cosine distance, best first, using the binary-quantization coarse column as a fast
    /// pre-filter (Phase 2). The coarse pass takes <c>oversample * k</c> candidates by Hamming distance on the 768-bit
    /// codes, then reranks them with the exact <c>vec_distance_cosine</c> of the full <c>float[768]</c> column, so the
    /// returned order matches the Phase-1 flat KNN up to the quantization recall. Returns an empty list (never throws)
    /// when the extension or index is unavailable: callers then fall back to <see cref="SearchVecAsync"/> and, last, to
    /// a brute-force scan. The query must be a 768-dim vector; it is bound as a little-endian float32 blob.
    /// </summary>
    public async Task<IReadOnlyList<(Guid ChunkId, float Distance)>> SearchVecRescoredAsync(
        float[] query,
        int k,
        int oversample = DefaultVecOversample,
        CancellationToken cancellationToken = default)
    {
        if (!_vecIndexReady || !VecAvailable || query is null || query.Length != EmbeddingDimensions || k <= 0)
        {
            return Array.Empty<(Guid, float)>();
        }

        int candidateLimit = Math.Max(k, k * Math.Max(1, oversample));

        try
        {
            byte[] queryBytes = MemoryMarshal.AsBytes(query.AsSpan()).ToArray();

            for (int attempt = 1; ; attempt++)
            {
                await using var conn = await OpenConnectionAsync(cancellationToken);
                try
                {
                    var hits = new List<(long RowId, float Distance)>();
                    // The exact vectors are read from MediaChunks (the source of truth, a plain B-tree rowid lookup) rather
                    // than re-reading the vec0 table: joining back into the virtual table by chunk_id makes the planner scan
                    // the whole index once per candidate, which is several times slower than the coarse pass itself.
                    const string rescoreSql = """
                        WITH coarse AS (
                            SELECT chunk_id
                            FROM VecMediaChunks
                            WHERE embedding_coarse MATCH vec_quantize_binary(@q)
                            ORDER BY distance
                            LIMIT @limit
                        )
                        SELECT c.chunk_id, vec_distance_cosine(m.Embedding, @q) AS distance
                        FROM coarse c
                        JOIN MediaChunks m ON m.rowid = c.chunk_id
                        ORDER BY distance
                        LIMIT @k;
                        """;
                    await using (var cmd = new SqliteCommand(rescoreSql, conn))
                    {
                        cmd.Parameters.AddWithValue("@q", queryBytes);
                        cmd.Parameters.AddWithValue("@limit", candidateLimit);
                        cmd.Parameters.AddWithValue("@k", k);
                        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            hits.Add((reader.GetInt64(0), (float)reader.GetDouble(1)));
                        }
                    }

                    return await MapHitsToChunkIdsAsync(conn, hits, cancellationToken);
                }
                catch (SqliteException ex) when (IsTransientLock(ex) && attempt < MaxReadAttempts)
                {
                    // A transient lock is retried on a fresh connection; the final failure falls through to the
                    // empty-result fallback below, which is unchanged.
                    await Task.Delay(RetryDelayMs(attempt), cancellationToken);
                }
            }
        }
        catch
        {
            // A vec failure must never break search: report "no index results" and let the caller fall back.
            return Array.Empty<(Guid, float)>();
        }
    }

    /// <summary>Maps the rowids of KNN hits back to chunk Guids, preserving their order.</summary>
    private static async Task<IReadOnlyList<(Guid ChunkId, float Distance)>> MapHitsToChunkIdsAsync(
        SqliteConnection conn,
        List<(long RowId, float Distance)> hits,
        CancellationToken cancellationToken)
    {
        if (hits.Count == 0) return Array.Empty<(Guid, float)>();

        var ids = new Dictionary<long, Guid>(hits.Count);
        var placeholders = string.Join(",", hits.Select((_, i) => $"@r{i}"));
        await using (var mapCmd = new SqliteCommand(
            $"SELECT rowid, Id FROM MediaChunks WHERE rowid IN ({placeholders});", conn))
        {
            for (int i = 0; i < hits.Count; i++)
            {
                mapCmd.Parameters.AddWithValue($"@r{i}", hits[i].RowId);
            }
            await using var reader = await mapCmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                ids[reader.GetInt64(0)] = Guid.Parse(reader.GetString(1));
            }
        }

        var results = new List<(Guid, float)>(hits.Count);
        foreach (var (rowId, distance) in hits)
        {
            if (ids.TryGetValue(rowId, out var chunkId)) results.Add((chunkId, distance));
        }
        return results;
    }
}
