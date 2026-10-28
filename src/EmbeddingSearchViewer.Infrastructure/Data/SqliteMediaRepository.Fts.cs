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
    /// <summary>True while the full-text index may still be incomplete (the rebuild is running).</summary>
    private volatile bool _ftsIndexReady;

    /// <summary>True when the full-text index has to be rebuilt (the triggers do not exist while it is).</summary>
    private volatile bool _ftsRebuildNeeded;

    /// <summary>
    /// True when a chunk delete ran while the full-text triggers were dropped: the index then keeps entries whose chunk
    /// rows are gone and has to be rebuilt from the content table once the rebuild catches up.
    /// </summary>
    private volatile bool _ftsDeleteWhileRebuilding;

    /// <summary>
    /// Indexes the rows that predate the full-text triggers in the full-text index, in batches: the content-sync index
    /// holds no text of its own, so the rows that were written before the triggers existed have to be added once. The
    /// progress is remembered, so an interrupted rebuild continues where it stopped instead of indexing a row twice.
    /// <para>
    /// The triggers do not exist while this runs (the index is not complete, and the FTS5 'delete' command needs the
    /// row to be in the index). They are created at the end, together with the catch-up of the rows that were written
    /// in the meantime: the rowid limit and the triggers are written in one transaction, so no write can slip in
    /// between them.
    /// </para>
    /// </summary>
    private async Task RebuildFtsIndexAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        string? limitText = await GetMetaAsync(conn, MetaFtsRebuildLimit, cancellationToken);
        long limit = 0;
        if (limitText is not null && long.TryParse(limitText, out long parsed)) limit = parsed;

        long cursor = 0;
        if (await GetMetaAsync(conn, MetaFtsRebuildCursor, cancellationToken) is string cursorText &&
            long.TryParse(cursorText, out long stored) && stored > 0)
        {
            cursor = stored;
        }

        // The read connection streams the chunks, the write connection fills the index: WAL lets the two run side by
        // side, and every batch is a transaction of its own, so a write of the app is never blocked for long.
        await using var writeConn = await OpenConnectionAsync(cancellationToken);
        while (cursor < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await ReadFtsBatchAsync(cursor, limit, cancellationToken);
            if (batch.Count == 0) break;

            await WriteFtsBatchAsync(writeConn, batch, cancellationToken);

            cursor = batch[^1].RowId;
            await SetMetaAsync(conn, MetaFtsRebuildCursor, cursor.ToString(), cancellationToken);
        }

        // The triggers are created now that the index holds the rows of the rebuild. The rowid limit is read in the
        // same transaction, so a row that is written right now is indexed by the trigger and not by the catch-up.
        long catchUpLimit;
        await using (var tx = writeConn.BeginTransaction(deferred: false))
        {
            catchUpLimit = await ScalarLongAsync(writeConn, "SELECT coalesce(max(rowid), 0) FROM MediaChunks;", tx, cancellationToken);
            await ExecuteAsync(writeConn, FtsTriggerSql, tx, cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        // The triggers are back: deletes and updates keep the index in step from now on, so the window in which a
        // delete had to be cleaned up by hand is over.
        _ftsRebuildNeeded = false;

        // The catch-up: the rows that were written while the rebuild ran are not in the index yet (the triggers did
        // not exist). They are added in batches, so a write of the app is not blocked for long.
        cursor = limit;
        while (cursor < catchUpLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batch = await ReadFtsBatchAsync(cursor, catchUpLimit, cancellationToken);
            if (batch.Count == 0) break;

            await WriteFtsBatchAsync(writeConn, batch, cancellationToken);
            cursor = batch[^1].RowId;
        }

        // A delete that ran while the triggers were dropped removed the chunk rows but could not remove their index
        // entries (there is no trigger to do it). FTS5 cannot drop such an entry afterwards either: it needs the
        // indexed values for that, and they went with the rows. One rebuild from the content table is the cleanup; the
        // normal case (no delete during the rebuild) skips it.
        if (_ftsDeleteWhileRebuilding)
        {
            await ExecuteAsync(writeConn, "INSERT INTO MediaChunksFts(MediaChunksFts) VALUES('rebuild');", cancellationToken);
            _ftsDeleteWhileRebuilding = false;
        }
    }

    /// <summary>Reads the next batch of chunks that have to be put into the full-text index.</summary>
    private async Task<List<(long RowId, string? FileName, string? SnippetText)>> ReadFtsBatchAsync(
        long cursor, long limit, CancellationToken cancellationToken)
    {
        var batch = new List<(long RowId, string? FileName, string? SnippetText)>();
        await using var readConn = await OpenConnectionAsync(cancellationToken);
        await using var select = new SqliteCommand(
            "SELECT rowid, FileName, SnippetText FROM MediaChunks WHERE rowid > @cursor AND rowid <= @limit ORDER BY rowid LIMIT @batch;",
            readConn);
        select.Parameters.AddWithValue("@cursor", cursor);
        select.Parameters.AddWithValue("@limit", limit);
        select.Parameters.AddWithValue("@batch", FtsRebuildBatchSize);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            batch.Add((
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return batch;
    }

    /// <summary>Puts one batch of chunks into the full-text index (one transaction).</summary>
    private static async Task WriteFtsBatchAsync(
        SqliteConnection writeConn,
        List<(long RowId, string? FileName, string? SnippetText)> batch,
        CancellationToken cancellationToken)
    {
        await using var tx = writeConn.BeginTransaction();
        await using var insert = new SqliteCommand(
            "INSERT INTO MediaChunksFts(rowid, FileName, SnippetText) VALUES (@rowid, @fileName, @snippetText);",
            writeConn, tx);
        var rowIdParam = insert.Parameters.Add("@rowid", SqliteType.Integer);
        var fileNameParam = insert.Parameters.Add("@fileName", SqliteType.Text);
        var snippetParam = insert.Parameters.Add("@snippetText", SqliteType.Text);
        foreach (var (rowId, fileName, snippetText) in batch)
        {
            rowIdParam.Value = rowId;
            fileNameParam.Value = (object?)fileName ?? DBNull.Value;
            snippetParam.Value = (object?)snippetText ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The content-sync full-text index. It stores no text of its own: <c>content='MediaChunks'</c> makes FTS5 read
    /// every indexed column straight from <c>MediaChunks</c> by <c>rowid</c>, so the index can never drift from the
    /// chunk rows. The indexed columns are <c>MediaChunks.FileName</c> and <c>MediaChunks.SnippetText</c>; there is no
    /// stored ChunkId (the rowid is the join key), which is what lets every write be a plain rowid lookup instead of the
    /// former <c>DELETE ... WHERE ChunkId IN (...)</c> full scan over an UNINDEXED column.
    /// </summary>
    private const string FtsTableSql = """
        CREATE VIRTUAL TABLE IF NOT EXISTS MediaChunksFts USING fts5(
            FileName,
            SnippetText,
            content='MediaChunks',
            content_rowid='rowid',
            tokenize = 'unicode61'
        );
        """;

    /// <summary>
    /// The triggers that keep the content-sync index in step with <c>MediaChunks</c>. FTS5 external content cannot be
    /// updated by a plain INSERT/DELETE: a delete must use the special <c>'delete'</c> command, and it must be handed the
    /// exact values that were indexed (otherwise the index becomes corrupt). Hence the AFTER DELETE/UPDATE triggers pass
    /// the <c>OLD</c> FileName/SnippetText, and the OUTER INSERT trigger indexes every row so those values always exist.
    /// The definitions are dropped and re-created on every start (like the vec triggers) so they self-heal. Note there is
    /// deliberately no direct <c>DELETE FROM MediaChunksFts</c> anywhere: on an external-content table that would wipe the
    /// whole index instead of the intended rows.
    /// </summary>
    private const string FtsTriggerSql = """
        DROP TRIGGER IF EXISTS trg_chunks_fts_ai;
        DROP TRIGGER IF EXISTS trg_chunks_fts_ad;
        DROP TRIGGER IF EXISTS trg_chunks_fts_au;

        CREATE TRIGGER trg_chunks_fts_ai AFTER INSERT ON MediaChunks
        BEGIN
            INSERT INTO MediaChunksFts(rowid, FileName, SnippetText)
            VALUES (NEW.rowid, NEW.FileName, NEW.SnippetText);
        END;

        CREATE TRIGGER trg_chunks_fts_ad AFTER DELETE ON MediaChunks
        BEGIN
            INSERT INTO MediaChunksFts(MediaChunksFts, rowid, FileName, SnippetText)
            VALUES ('delete', OLD.rowid, OLD.FileName, OLD.SnippetText);
        END;

        CREATE TRIGGER trg_chunks_fts_au AFTER UPDATE ON MediaChunks
        BEGIN
            INSERT INTO MediaChunksFts(MediaChunksFts, rowid, FileName, SnippetText)
            VALUES ('delete', OLD.rowid, OLD.FileName, OLD.SnippetText);
            INSERT INTO MediaChunksFts(rowid, FileName, SnippetText)
            VALUES (NEW.rowid, NEW.FileName, NEW.SnippetText);
        END;
        """;

    /// <summary>
    /// Removes the full-text triggers without creating them again. They may only be missing while the index is being
    /// rebuilt: the FTS5 'delete' command that the update and delete triggers use fails with "database disk image is
    /// malformed" for a row that is not in the index yet.
    /// </summary>
    private const string FtsTriggerDropSql = """
        DROP TRIGGER IF EXISTS trg_chunks_fts_ai;
        DROP TRIGGER IF EXISTS trg_chunks_fts_ad;
        DROP TRIGGER IF EXISTS trg_chunks_fts_au;
        """;

    /// <summary>
    /// True when the database still carries the old standalone full-text index (<c>ChunkId UNINDEXED, FileName,
    /// SnippetText</c>). The old shape is recognised from <c>sqlite_master</c> (its stored SQL never mentions
    /// <c>content='MediaChunks'</c>); it is derived data, so it is simply dropped and indexed again from
    /// <c>MediaChunks</c>, which is the source of truth. No re-import is needed.
    /// </summary>
    private static async Task<bool> IsLegacyFtsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        await using var meta = new SqliteCommand(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'MediaChunksFts';", conn);
        if (await meta.ExecuteScalarAsync(cancellationToken) is not string createSql || string.IsNullOrEmpty(createSql))
        {
            return false;
        }

        return !createSql.Contains("content='MediaChunks'", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the full-text index exists.</summary>
    private static async Task<bool> FtsTableExistsAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        await using var probe = new SqliteCommand(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'MediaChunksFts';", conn);
        return Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
    }

    public async Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default)
    {
        // Escape keyword for FTS query
        string cleanKeyword = keyword.Replace("\"", "\"\"").Trim();
        if (string.IsNullOrEmpty(cleanKeyword)) return Array.Empty<(Guid, float)>();

        // While the index is being rebuilt it does not hold every row yet, so the search falls back to a plain scan of
        // the chunk table. It is slower but complete, and it is only used until the rebuild is done.
        if (!_ftsIndexReady) return await SearchFtsByScanAsync(cleanKeyword, limit, cancellationToken);

        for (int attempt = 1; ; attempt++)
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);

            // The content-sync index has no stored ChunkId: its rowid is the MediaChunks rowid, so the Guid is joined
            // back from MediaChunks. bm25/MATCH read the inverted index only, so this stays a full-text lookup plus a
            // rowid join.
            const string sql = """
                SELECT m.Id, bm25(MediaChunksFts) AS Rank
                FROM MediaChunksFts
                JOIN MediaChunks m ON m.rowid = MediaChunksFts.rowid
                WHERE MediaChunksFts MATCH @query
                ORDER BY Rank
                LIMIT @limit;
            """;

            await using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@query", $"\"{cleanKeyword}\"");
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<(Guid, float)>();
            try
            {
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = Guid.Parse(reader.GetString(0));
                    // bm25 returns lower is better (usually negative), normalize to positive score
                    float rank = (float)reader.GetDouble(1);
                    float positiveScore = 1.0f / (1.0f + Math.Abs(rank));
                    results.Add((id, positiveScore));
                }
            }
            catch (SqliteException ex) when (IsTransientLock(ex) && attempt < MaxReadAttempts)
            {
                // A transient lock is retried on a fresh connection (this method never throws on a lock).
                await Task.Delay(RetryDelayMs(attempt), cancellationToken);
                continue;
            }
            catch (SqliteException)
            {
                // If FTS match query syntax is invalid, ignore
            }

            return results;
        }
    }

    /// <summary>
    /// The fallback of the full-text search while the index is being rebuilt: a plain scan of the chunk table with a
    /// LIKE comparison. It is slower than the index (it reads every chunk row) but it is complete, so the search finds
    /// the same documents before and after the rebuild. The scan stops as soon as <paramref name="limit"/> rows are
    /// found, so a common keyword is quick.
    /// </summary>
    private async Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsByScanAsync(
        string keyword, int limit, CancellationToken cancellationToken)
    {
        string pattern = "%" + keyword.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        const string sql = """
            SELECT Id FROM MediaChunks
            WHERE SnippetText LIKE @pattern ESCAPE '\' OR FileName LIKE @pattern ESCAPE '\'
            LIMIT @limit;
            """;

        await using var conn = await OpenConnectionAsync(cancellationToken);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@pattern", pattern);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<(Guid, float)>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // The scan has no ranking of its own; every hit gets the same score, so the fusion orders them by the
                // vector score (and by the path, for equal scores).
                results.Add((Guid.Parse(reader.GetString(0)), 1.0f));
            }
        }
        catch (SqliteException)
        {
            // A database without the tables (not initialized yet) has nothing to search: the same as the index path.
        }

        return results;
    }
}
