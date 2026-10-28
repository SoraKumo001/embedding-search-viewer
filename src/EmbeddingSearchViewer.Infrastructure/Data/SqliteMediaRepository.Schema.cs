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
    // --- start-up: the fast part (the schema) and the slow part (the derived indexes) ---
    // The start-up is split in two so the app is usable while the expensive work runs:
    //   * the fast part opens the database, sets the pragmas and creates the tables, the indexes and the triggers.
    //     It only touches the schema, so it is over in a few milliseconds.
    //   * the slow part fills the derived indexes (the full-text rebuild, the vector backfill). It runs in the
    //     background; while it runs the search uses the fallbacks (a LIKE scan for the text, the plain vector index
    //     for the embeddings), and the data version is bumped when it is done, so the cached results are read again.
    // The schema version in PRAGMA user_version makes both parts idempotent: what is already done is not repeated.

    /// <summary>The schema version of the database file, kept in <c>PRAGMA user_version</c>.</summary>
    private const int SchemaVersionBase = 1;

    /// <summary>The full-text index is built and in sync (the rebuild is done).</summary>
    private const int SchemaVersionFtsReady = 2;

    /// <summary>The vector index is in its current shape (the coarse column is there).</summary>
    private const int SchemaVersionCurrent = 3;

    /// <summary>The rowid up to which the full-text index is rebuilt (the rows that predate the triggers).</summary>
    private const string MetaFtsRebuildLimit = "fts_rebuild_limit";

    /// <summary>The rowid up to which the full-text index has been rebuilt (the progress of the rebuild).</summary>
    private const string MetaFtsRebuildCursor = "fts_rebuild_cursor";

    /// <summary>Set once the file name of the older chunks has been filled in.</summary>
    private const string MetaFileNameBackfilled = "filename_backfilled";

    /// <summary>How many chunks one transaction of the full-text rebuild indexes.</summary>
    private const int FtsRebuildBatchSize = 2000;

    /// <summary>How many chunks one transaction of the file-name backfill updates.</summary>
    private const int FileNameBackfillBatchSize = 5000;

    private readonly object _initLock = new();
    private readonly CancellationTokenSource _slowPhaseCts = new();
    private Task? _slowPhaseTask;
    private long _schemaVersion;

    /// <summary>
    /// The full start-up: the fast part (the schema) and then the slow part (the derived indexes). The slow part runs
    /// in the background, so this waits for it; the app itself uses <see cref="InitializeFastAsync"/> and goes on
    /// while the slow part is still running.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeFastAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSlowPhaseAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The fast part of the start-up: the database is opened, the pragmas are set and the tables, the indexes and the
    /// triggers are created. Nothing else is touched, so it is over in a few milliseconds. The slow part (the
    /// full-text rebuild and the vector backfill) starts in the background and does not block the caller.
    /// </summary>
    public async Task InitializeFastAsync(CancellationToken cancellationToken = default)
    {
        await EnsureFastPhaseAsync(cancellationToken).ConfigureAwait(false);
        StartSlowPhase();
    }

    /// <summary>
    /// The schema: the tables, the indexes and the triggers. It is written so it can be run again at any time (every
    /// statement is "IF NOT EXISTS" or a drop-and-create), which is what makes the start-up idempotent.
    /// </summary>
    private async Task EnsureFastPhaseAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        // P3 maintenance policy: WAL + synchronous=NORMAL are the steady state (never OFF). The WAL is bounded by
        // journal_size_limit (set per connection in OpenConnectionAsync) and folded back by an explicit checkpoint
        // at idle/ingest-complete time (CheckpointWalAsync). VACUUM is deliberately never automated (see below).
        // Setting the journal mode again on a database that is already in WAL costs a lock round trip, so the mode is
        // read first and only changed when it is something else.
        string? journalMode = await ScalarStringAsync(conn, "PRAGMA journal_mode;", cancellationToken);
        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteAsync(conn, "PRAGMA journal_mode = WAL;", cancellationToken);
        }
        await ExecuteAsync(conn, "PRAGMA synchronous = NORMAL;", cancellationToken);

        // The tables and the indexes (the source of truth).
        await ExecuteAsync(conn, BaseSchemaSql, cancellationToken);

        // The small table that remembers what the start-up has already done (the rebuild cursor, the backfill flag).
        await ExecuteAsync(conn, MetaTableSql, cancellationToken);

        // The full-text index reads MediaChunks.FileName back from its content table, so the column has to exist
        // before the triggers are created. Adding a column is a metadata-only change in SQLite (no row is rewritten),
        // so it belongs to the fast part; the values of the older chunks are filled in by the slow part.
        if (!await HasColumnAsync(conn, "MediaChunks", "FileName", cancellationToken))
        {
            await ExecuteAsync(conn, "ALTER TABLE MediaChunks ADD COLUMN FileName TEXT;", cancellationToken);
            // The file names have to be filled in again (the column was just added).
            await DeleteMetaAsync(conn, MetaFileNameBackfilled, cancellationToken);
        }

        _schemaVersion = await GetUserVersionAsync(conn, cancellationToken);

        // The full-text index: the old standalone shape is dropped (it is derived, so nothing is lost) and the
        // content-sync table is created.
        bool ftsExisted = await FtsTableExistsAsync(conn, cancellationToken);
        if (ftsExisted && await IsLegacyFtsAsync(conn, cancellationToken))
        {
            await ExecuteAsync(conn, "DROP TABLE IF EXISTS MediaChunksFts;", cancellationToken);
            ftsExisted = false;
        }
        await ExecuteAsync(conn, FtsTableSql, cancellationToken);

        // The index is only complete when the rebuild has run. While it is not, the triggers must not exist: the FTS5
        // 'delete' command (which the update and delete triggers use) fails with "database disk image is malformed" for
        // a row that is not in the index yet. The slow part indexes the rows and creates the triggers afterwards.
        _ftsRebuildNeeded = _schemaVersion < SchemaVersionFtsReady || !ftsExisted;
        if (_ftsRebuildNeeded)
        {
            await ExecuteAsync(conn, FtsTriggerDropSql, cancellationToken);

            // The rows that exist now are the ones the rebuild has to index; everything written from here on is indexed
            // by the catch-up of the slow part. The limit is remembered, so a rebuild that was interrupted continues
            // where it stopped.
            string? limit = await GetMetaAsync(conn, MetaFtsRebuildLimit, cancellationToken);
            if (limit is null)
            {
                long maxRowId = await ScalarLongAsync(conn, "SELECT coalesce(max(rowid), 0) FROM MediaChunks;", cancellationToken);
                await SetMetaAsync(conn, MetaFtsRebuildLimit, maxRowId.ToString(), cancellationToken);
            }
        }
        else
        {
            // The index is complete, so the triggers can be (re)created: they keep it in step from now on.
            await ExecuteAsync(conn, FtsTriggerSql, cancellationToken);
        }

        // The vector index: the table and its triggers. The migration from the Phase-1 shape (the table without the
        // coarse column) is part of it, because the triggers need that column; it is a schema change and therefore
        // fast. The backfill of the older rows is the slow part.
        if (VecAvailable)
        {
            await EnsureVecSchemaAsync(conn, cancellationToken);
        }

        // The schema is in place; the version remembers it, so the next start does not repeat the work.
        if (_schemaVersion < SchemaVersionBase)
        {
            await SetUserVersionAsync(conn, SchemaVersionBase, cancellationToken);
            _schemaVersion = SchemaVersionBase;
        }

        // The full-text index is usable when its rebuild is done (the version says so). The vector index is usable as
        // soon as it exists: the backfill only adds the rows that predate it, and the data version is bumped when
        // that is done.
        _ftsIndexReady = !_ftsRebuildNeeded;
        _vecIndexReady = VecAvailable;
    }

    /// <summary>Starts the slow part of the start-up in the background (once).</summary>
    private void StartSlowPhase()
    {
        lock (_initLock)
        {
            _slowPhaseTask ??= RunSlowPhaseAsync(_slowPhaseCts.Token);
        }
    }

    /// <summary>The slow part of the start-up, started if it is not running yet.</summary>
    private Task EnsureSlowPhaseAsync()
    {
        StartSlowPhase();
        return _slowPhaseTask!;
    }

    /// <summary>
    /// The slow part of the start-up: the file names of the older chunks are filled in, the full-text index is
    /// rebuilt and the vector index gets the rows that predate it. It runs in the background, in batches, so the app
    /// stays usable while it works. When it is done the data version is bumped: the derived indexes changed, so the
    /// cached search results have to be read again.
    /// </summary>
    private async Task RunSlowPhaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 1. The file name of the chunks that predate the column.
            await BackfillFileNameAsync(cancellationToken).ConfigureAwait(false);

            // 2. The full-text index: the rows that predate the triggers are indexed here, in batches, and the triggers
            // are created afterwards (while the rebuild runs they must not exist, see EnsureFastPhaseAsync).
            if (_ftsRebuildNeeded)
            {
                await RebuildFtsIndexAsync(cancellationToken).ConfigureAwait(false);
            }

            // 3. The vector index: the rows that predate it are added in one pass.
            if (VecAvailable)
            {
                await EnsureVecBackfillAsync(cancellationToken).ConfigureAwait(false);
            }
            _vecIndexReady = VecAvailable;
            _ftsIndexReady = true;

            // 4. The version remembers that the work is done, so the next start does not repeat it.
            int version = VecAvailable ? SchemaVersionCurrent : SchemaVersionFtsReady;
            if (_schemaVersion < version)
            {
                await SetSchemaVersionAsync(version).ConfigureAwait(false);
                _schemaVersion = version;
            }

            // The derived indexes changed, so the cached results are stale.
            Interlocked.Increment(ref _dataVersion);
        }
        catch (OperationCanceledException)
        {
            // The repository was closed while the slow part was running.
        }
        catch (Exception ex)
        {
            // A failure here must never stop the app: the search falls back to the plain table scan and the next
            // start tries again.
            System.Diagnostics.Debug.WriteLine($"[SqliteMediaRepository] The slow start-up work failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Fills in the file name of the chunks that predate the column (the full-text index reads it back). It runs in
    /// batches, so the database is not locked for the whole table.
    /// </summary>
    private async Task BackfillFileNameAsync(CancellationToken cancellationToken)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
        if (await GetMetaAsync(conn, MetaFileNameBackfilled, cancellationToken) is not null) return;

        long cursor = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var ids = new List<long>();
            await using (var readConn = await OpenConnectionAsync(cancellationToken))
            {
                await using var select = new SqliteCommand(
                    "SELECT rowid FROM MediaChunks WHERE FileName IS NULL AND rowid > @cursor ORDER BY rowid LIMIT @batch;",
                    readConn);
                select.Parameters.AddWithValue("@cursor", cursor);
                select.Parameters.AddWithValue("@batch", FileNameBackfillBatchSize);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(reader.GetInt64(0));
                }
            }

            if (ids.Count == 0) break;

            await using (var writeConn = await OpenConnectionAsync(cancellationToken))
            {
                await using var tx = writeConn.BeginTransaction();
                var placeholders = string.Join(",", ids.Select((_, i) => $"@id{i}"));
                await using var update = new SqliteCommand(
                    "UPDATE MediaChunks SET FileName = (SELECT a.FileName FROM MediaAssets a WHERE a.Id = MediaChunks.AssetId) " +
                    $"WHERE rowid IN ({placeholders}) AND FileName IS NULL;", writeConn, tx);
                for (int i = 0; i < ids.Count; i++)
                {
                    update.Parameters.AddWithValue($"@id{i}", ids[i]);
                }
                await update.ExecuteNonQueryAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }

            cursor = ids[^1];
        }

        await SetMetaAsync(conn, MetaFileNameBackfilled, "1", cancellationToken);
    }

    /// <summary>The tables, the indexes and the meta table of the schema.</summary>
    private const string BaseSchemaSql = """
        CREATE TABLE IF NOT EXISTS MediaAssets (
            Id TEXT PRIMARY KEY,
            FilePath TEXT UNIQUE NOT NULL,
            FileName TEXT NOT NULL,
            MediaType TEXT NOT NULL,
            FileSizeBytes INTEGER NOT NULL,
            FileHash TEXT NOT NULL,
            LastModifiedUtc TEXT NOT NULL,
            ThumbnailPath TEXT,
            DurationOrPages INTEGER,
            IndexedAtUtc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS MediaChunks (
            Id TEXT PRIMARY KEY,
            AssetId TEXT NOT NULL,
            Modality TEXT NOT NULL,
            ChunkIndex INTEGER NOT NULL,
            PageNumber INTEGER,
            StartTimeMs INTEGER,
            EndTimeMs INTEGER,
            SnippetText TEXT,
            PreviewImagePath TEXT,
            Embedding BLOB,
            -- Denormalized copy of the owning asset's file name. It exists so the content-sync MediaChunksFts
            -- (see EnsureFtsSchemaAsync) can read the indexed column back from its content table (on delete/update).
            FileName TEXT,
            FOREIGN KEY (AssetId) REFERENCES MediaAssets(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_MediaChunks_AssetId ON MediaChunks(AssetId);

        CREATE INDEX IF NOT EXISTS IX_MediaAssets_MediaType ON MediaAssets(MediaType);
        """;

    /// <summary>The small table that remembers what the start-up has already done.</summary>
    private const string MetaTableSql = """
        CREATE TABLE IF NOT EXISTS SchemaMeta (Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        """;

    /// <summary>Writes the schema version of the database file (on its own connection).</summary>
    private async Task SetSchemaVersionAsync(int version)
    {
        await using var conn = await OpenConnectionAsync(CancellationToken.None);
        await SetUserVersionAsync(conn, version, CancellationToken.None);
    }
}
