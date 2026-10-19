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

namespace EmbeddingSearchViewer.Infrastructure.Data;

public class SqliteMediaRepository : IMediaRepository, IDisposable
{
    /// <summary>
    /// Embedding width of the model (768 for the bundled Gemma/CLIP image-text encoder). The sqlite-vec table is a
    /// flat <c>float[768]</c> column, so only embeddings of exactly this width are indexed there.
    /// </summary>
    private const int EmbeddingDimensions = 768;
    private const int EmbeddingBytes = EmbeddingDimensions * sizeof(float);

    /// <summary>How many chunk vectors are inserted in one transaction while backfilling.</summary>
    private const int VecBackfillBatchSize = 5000;

    /// <summary>Upper bound on assets in one bulk-save transaction, so a transaction never grows without limit.</summary>
    private const int MaxAssetsPerTx = 150;

    /// <summary>Upper bound on chunk rows in one bulk-save transaction (an asset with many chunks splits on its own).</summary>
    private const int MaxChunksPerTx = 800;

    /// <summary>The savepoint a bulk save wraps each asset in, so a bad asset rolls back only itself.</summary>
    private const string SavepointName = "sp_asset";

    /// <summary>
    /// Upper bound on the write-ahead log: after a checkpoint the WAL is truncated back to at most this size instead of
    /// growing without limit between checkpoints. 64 MB keeps a bulk ingest from leaving a multi-gigabyte -wal behind.
    /// </summary>
    private const long JournalSizeLimitBytes = 64L * 1024 * 1024;

    /// <summary>How many coarse (binary-quantization) candidates are rescored per requested neighbour by default.</summary>
    public const int DefaultVecOversample = 8;

    /// <summary>Most attempts one write transaction makes before a transient lock error is surfaced to the caller.</summary>
    private const int MaxWriteAttempts = 5;

    /// <summary>Most attempts a read makes before it gives up (reads never hold the writer gate, so fewer suffice).</summary>
    private const int MaxReadAttempts = 3;

    /// <summary>Base backoff of the retry helpers: 50/100/200/400 ms doubling per attempt, plus up to this much jitter.</summary>
    private const int RetryBaseDelayMs = 50;

    /// <summary>
    /// Per-connection busy wait. SQLITE_BUSY is an awaited condition for this long; SQLITE_LOCKED is not (which is why
    /// the cache mode is private, see the constructor). 15s rides out a concurrent WAL writer/checkpoint and a bulk
    /// ingest slice without failing the statement outright.
    /// </summary>
    private const int BusyTimeoutMs = 15000;

    private readonly string _connectionString;
    private readonly string _dbPath;
    private long _dataVersion;

    public long DataVersion => Interlocked.Read(ref _dataVersion);

    // --- single reusable writer connection ---
    // Every write goes through one connection, held open for the life of the repository, instead of opening a new
    // connection (and re-loading the vec0 extension) per statement. _writeGate serialises writers, mirroring SQLite's
    // own single-writer rule; the worker's _saveLock keeps saves ordered above this, so this is mostly about deletes
    // that arrive from the folder watcher while an ingestion is running.
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SqliteConnection? _writeConn;
    private SqliteCommand? _saveDeleteCmd;
    private SqliteCommand? _saveInsertAssetCmd;
    private SqliteCommand? _saveInsertChunkCmd;
    private SqliteCommand? _deleteAssetCmd;
    private SqliteCommand? _clearCmd;
    private SqliteCommand? _savepointCmd;
    private SqliteCommand? _releaseCmd;
    private SqliteCommand? _rollbackCmd;
    private bool _disposed;

    /// <summary>
    /// Diagnostics for tests: how many transactions the most recent <see cref="SaveAssetsWithChunksAsync"/> call used.
    /// It exists so a test can assert the run is sliced into bounded transactions without inspecting SQLite internals.
    /// </summary>
    internal int LastBulkSaveTransactionCount { get; private set; }

    // --- sqlite-vec native extension state (shared by every repository in the process) ---
    private static readonly object NativeLock = new();
    private static readonly object VecLoadLock = new();
    private static bool _batteriesInitialized;
    private static int _vecState;                 // 0 = not probed, 1 = available, 2 = unavailable
    private static volatile string? _vecExtensionPath; // null until a load succeeds

    /// <summary>True once a connection has successfully loaded the vec0 extension.</summary>
    public static bool VecAvailable => Volatile.Read(ref _vecState) == 1;

    public SqliteMediaRepository(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbeddingSearchViewer",
            "media_index.db"
        );
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache (the provider default) is deliberate and must not become Shared: shared-cache mode makes
            // SQLite take table-level locks and return SQLITE_LOCKED (6) on contention ("database table is locked"),
            // which busy_timeout does NOT wait on. With the default private cache, WAL still gives us the one-writer
            // plus concurrent-readers model we want, and contention surfaces as a plain SQLITE_BUSY (5) that
            // busy_timeout does wait out.
            Cache = SqliteCacheMode.Private
        }.ToString();
    }

    /// <summary>
    /// The one writer connection, opened on first use and kept open: the vec0 extension is loaded once instead of on
    /// every statement. Callers must already hold <see cref="_writeGate"/>.
    /// </summary>
    private async Task<SqliteConnection> EnsureWriteConnectionAsync(CancellationToken cancellationToken)
    {
        if (_writeConn is { State: ConnectionState.Open } open) return open;

        ResetWriteConnection(); // a connection that somehow closed is dropped, commands and all
        var conn = await OpenConnectionAsync(cancellationToken);
        _writeConn = conn;
        CreateWriteCommands(conn);
        return conn;
    }

    /// <summary>Drops the writer connection and its cached commands so the next write opens a fresh one.</summary>
    private void ResetWriteConnection()
    {
        _saveDeleteCmd?.Dispose();
        _saveInsertAssetCmd?.Dispose();
        _saveInsertChunkCmd?.Dispose();
        _deleteAssetCmd?.Dispose();
        _clearCmd?.Dispose();
        _savepointCmd?.Dispose();
        _releaseCmd?.Dispose();
        _rollbackCmd?.Dispose();
        _saveDeleteCmd = null;
        _saveInsertAssetCmd = null;
        _saveInsertChunkCmd = null;
        _deleteAssetCmd = null;
        _clearCmd = null;
        _savepointCmd = null;
        _releaseCmd = null;
        _rollbackCmd = null;
        _writeConn?.Dispose();
        _writeConn = null;
    }

    /// <summary>
    /// Prepares the commands the write paths reuse. They are bound to <paramref name="conn"/> without a transaction and
    /// get the active one assigned per use (a transaction is per-statement in Microsoft.Data.Sqlite).
    /// </summary>
    private void CreateWriteCommands(SqliteConnection conn)
    {
        _saveDeleteCmd = new SqliteCommand(
            "DELETE FROM MediaChunks WHERE AssetId IN (SELECT Id FROM MediaAssets WHERE Id = @assetId OR FilePath = @filePath); " +
            "DELETE FROM MediaAssets WHERE Id = @assetId OR FilePath = @filePath;", conn);
        _saveDeleteCmd.Parameters.Add("@assetId", SqliteType.Text);
        _saveDeleteCmd.Parameters.Add("@filePath", SqliteType.Text);

        _saveInsertAssetCmd = new SqliteCommand(InsertAssetSql, conn);
        _saveInsertAssetCmd.Parameters.Add("@Id", SqliteType.Text);
        _saveInsertAssetCmd.Parameters.Add("@FilePath", SqliteType.Text);
        _saveInsertAssetCmd.Parameters.Add("@FileName", SqliteType.Text);
        _saveInsertAssetCmd.Parameters.Add("@MediaType", SqliteType.Text);
        _saveInsertAssetCmd.Parameters.Add("@FileSizeBytes", SqliteType.Integer);
        _saveInsertAssetCmd.Parameters.Add("@FileHash", SqliteType.Text);
        _saveInsertAssetCmd.Parameters.Add("@LastModifiedUtc", SqliteType.Text);
        _saveInsertAssetCmd.Parameters.Add("@ThumbnailPath", SqliteType.Text);
        _saveInsertAssetCmd.Parameters.Add("@DurationOrPages", SqliteType.Integer);
        _saveInsertAssetCmd.Parameters.Add("@IndexedAtUtc", SqliteType.Text);

        _saveInsertChunkCmd = new SqliteCommand(InsertChunkSql, conn);
        _saveInsertChunkCmd.Parameters.Add("@Id", SqliteType.Text);
        _saveInsertChunkCmd.Parameters.Add("@AssetId", SqliteType.Text);
        _saveInsertChunkCmd.Parameters.Add("@Modality", SqliteType.Text);
        _saveInsertChunkCmd.Parameters.Add("@ChunkIndex", SqliteType.Integer);
        _saveInsertChunkCmd.Parameters.Add("@PageNumber", SqliteType.Integer);
        _saveInsertChunkCmd.Parameters.Add("@StartTimeMs", SqliteType.Integer);
        _saveInsertChunkCmd.Parameters.Add("@EndTimeMs", SqliteType.Integer);
        _saveInsertChunkCmd.Parameters.Add("@SnippetText", SqliteType.Text);
        _saveInsertChunkCmd.Parameters.Add("@PreviewImagePath", SqliteType.Text);
        _saveInsertChunkCmd.Parameters.Add("@Embedding", SqliteType.Blob);
        _saveInsertChunkCmd.Parameters.Add("@FileName", SqliteType.Text);

        // The AFTER DELETE trigger on MediaChunks removes the matching FTS entries automatically.
        _deleteAssetCmd = new SqliteCommand(
            "DELETE FROM MediaChunks WHERE AssetId = @assetId; DELETE FROM MediaAssets WHERE Id = @assetId;", conn);
        _deleteAssetCmd.Parameters.Add("@assetId", SqliteType.Text);

        // MediaChunksFts is content-sync: the AFTER DELETE trigger clears its entries as the chunk rows go, so it must
        // not be deleted from directly (a direct DELETE on an external-content table would wipe the whole index).
        _clearCmd = new SqliteCommand("DELETE FROM MediaChunks; DELETE FROM MediaAssets;", conn);

        _savepointCmd = new SqliteCommand($"SAVEPOINT {SavepointName};", conn);
        _releaseCmd = new SqliteCommand($"RELEASE {SavepointName};", conn);
        _rollbackCmd = new SqliteCommand($"ROLLBACK TO {SavepointName}; RELEASE {SavepointName};", conn);
    }

    /// <summary>Runs one prepared command inside a transaction (<paramref name="tx"/> is per-statement in this provider).</summary>
    private static async Task<int> RunAsync(SqliteCommand cmd, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        cmd.Transaction = tx;
        return await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// True for the transient lock-contention errors a short backoff can clear: SQLITE_BUSY (5, "database is locked")
    /// and SQLITE_LOCKED (6, "database table is locked"). busy_timeout waits out a plain BUSY but not LOCKED, so both
    /// are retried explicitly here.
    /// </summary>
    private static bool IsTransientLock(SqliteException ex) => ex.SqliteErrorCode is 5 or 6;

    /// <summary>Exponential backoff (50/100/200/400 ms) plus up to one base delay of jitter, to de-sync retriers.</summary>
    private static int RetryDelayMs(int attempt) =>
        (RetryBaseDelayMs << (attempt - 1)) + Random.Shared.Next(0, RetryBaseDelayMs);

    /// <summary>
    /// Runs one transaction attempt, retrying transient lock contention (SQLITE_BUSY/SQLITE_LOCKED) with exponential
    /// backoff. The caller's delegate must create, commit and dispose its own transaction so every attempt starts from
    /// a clean one. Cancellation and every non-transient failure propagate unchanged; the writer connection is reset by
    /// the caller on such a failure, not here, so a retry deliberately reuses it.
    /// </summary>
    private static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> attempt, CancellationToken cancellationToken)
    {
        for (int i = 1; ; i++)
        {
            try
            {
                return await attempt();
            }
            catch (SqliteException ex) when (IsTransientLock(ex) && i < MaxWriteAttempts)
            {
                await Task.Delay(RetryDelayMs(i), cancellationToken);
            }
        }
    }

    /// <summary>Void overload of <see cref="ExecuteWithRetryAsync{T}"/>.</summary>
    private static Task ExecuteWithRetryAsync(Func<Task> attempt, CancellationToken cancellationToken) =>
        ExecuteWithRetryAsync(async () => { await attempt(); return true; }, cancellationToken);

    /// <summary>
    /// Opens its own connection and runs a read, retrying transient lock contention on a fresh connection (a new one
    /// each attempt, so no locked state is inherited). The delegate must not depend on state from a previous attempt,
    /// as a retry re-runs it from scratch. Signatures and fallbacks of the read callers are unchanged.
    /// </summary>
    private async Task<T> ExecuteReadWithRetryAsync<T>(Func<SqliteConnection, Task<T>> read, CancellationToken cancellationToken)
    {
        for (int i = 1; ; i++)
        {
            await using var conn = await OpenConnectionAsync(cancellationToken);
            try
            {
                return await read(conn);
            }
            catch (SqliteException ex) when (IsTransientLock(ex) && i < MaxReadAttempts)
            {
                await Task.Delay(RetryDelayMs(i), cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeGate.Wait();
        try
        {
            ResetWriteConnection();
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }

    /// <summary>
    /// The single place connections are created: initialises the SQLitePCLRaw provider exactly once and tries to load
    /// the vec0 extension (when it is missing, callers silently fall back to the brute-force scan).
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureNativeInitialized();
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync(cancellationToken);

            // busy_timeout is a per-connection setting and defaults to 0, so a writer that finds the database locked
            // fails immediately instead of waiting. 15s is enough to ride out a concurrent WAL writer/checkpoint and a
            // bulk-ingest write slice. It only covers SQLITE_BUSY; SQLITE_LOCKED (a shared-cache lock) is not awaited
            // by SQLite at all, so the cache mode is Private and the write paths retry LOCKED explicitly (see
            // ExecuteWithRetryAsync).
            await using (var busyTimeout = new SqliteCommand($"PRAGMA busy_timeout = {BusyTimeoutMs};", conn))
            {
                await busyTimeout.ExecuteNonQueryAsync(cancellationToken);
            }

            // journal_size_limit bounds the -wal file after a checkpoint. Like busy_timeout it is per-connection (it
            // applies to the connection that runs the checkpoint), so it is set here on every connection rather than
            // once in InitializeAsync. It never disables WAL or relaxes synchronous: WAL + synchronous=NORMAL stay put.
            await using (var journalLimit = new SqliteCommand($"PRAGMA journal_size_limit = {JournalSizeLimitBytes};", conn))
            {
                await journalLimit.ExecuteNonQueryAsync(cancellationToken);
            }

            TryLoadVec(conn);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    private static void EnsureNativeInitialized()
    {
        if (Volatile.Read(ref _batteriesInitialized)) return;
        lock (NativeLock)
        {
            if (_batteriesInitialized) return;
            try
            {
                SQLitePCL.Batteries_V2.Init();
            }
            catch
            {
                // Microsoft.Data.Sqlite sets the provider up on first use; a second init is not an error we care about.
            }
            Volatile.Write(ref _batteriesInitialized, true);
        }
    }

    private static void TryLoadVec(SqliteConnection conn)
    {
        if (Volatile.Read(ref _vecState) == 2) return;

        // Once a candidate path is known, load it on each new connection without taking the global lock.
        var known = _vecExtensionPath;
        if (known != null)
        {
            try
            {
                conn.EnableExtensions(true);
                conn.LoadExtension(known);
            }
            catch
            {
                // The query sites guard on failure and fall back to the brute-force scan.
            }
            return;
        }

        lock (VecLoadLock)
        {
            if (Volatile.Read(ref _vecState) == 2) return;

            known = _vecExtensionPath;
            if (known != null)
            {
                try
                {
                    conn.EnableExtensions(true);
                    conn.LoadExtension(known);
                }
                catch { }
                return;
            }

            try
            {
                conn.EnableExtensions(true);
            }
            catch
            {
                return;
            }

            foreach (var candidate in VecExtensionCandidates())
            {
                try
                {
                    conn.LoadExtension(candidate);
                    _vecExtensionPath = candidate;
                    Volatile.Write(ref _vecState, 1);
                    return;
                }
                catch
                {
                    // Try the next candidate path.
                }
            }

            Volatile.Write(ref _vecState, 2);
        }
    }

    /// <summary>The "vec0" bare name first (the loader search path), then candidate directories the host provides.</summary>
    private static IEnumerable<string> VecExtensionCandidates()
    {
        yield return "vec0";
        yield return Path.Combine(AppContext.BaseDirectory, "vec0.dll");

        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string nativeDirs)
        {
            foreach (var dir in nativeDirs.Split(Path.PathSeparator))
            {
                var trimmed = dir?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    yield return Path.Combine(trimmed, "vec0.dll");
                }
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

        var sql = """
            -- P3 maintenance policy: WAL + synchronous=NORMAL are the steady state (never OFF). The WAL is bounded by
            -- journal_size_limit (set per connection in OpenConnectionAsync) and folded back by an explicit checkpoint
            -- at idle/ingest-complete time (CheckpointWalAsync). VACUUM is deliberately never automated (see below).
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

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

        await using (var cmd = new SqliteCommand(sql, conn))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // The full-text index is a derived, content-sync copy of MediaChunks (see EnsureFtsSchemaAsync); it is created
        // and, on an older database, migrated to the trigger-maintained form before the vec work runs.
        await EnsureFtsSchemaAsync(conn, cancellationToken);

        // The vector index is a derived copy of MediaChunks.Embedding (the source of truth); it is only created when the
        // vec0 extension loaded. Chunk rows are keyed by MediaChunks.rowid, so a chunk_id can be joined back to its Guid.
        if (VecAvailable)
        {
            await EnsureVecSchemaAsync(conn, cancellationToken);

            await EnsureVecBackfillAsync(cancellationToken);
        }
    }

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
    /// Ensures the content-sync FTS index and its triggers exist, migrating a database that still carries the old
    /// standalone index (<c>ChunkId UNINDEXED, FileName, SnippetText</c>). The old shape is recognised from
    /// <c>sqlite_master</c> (it has no <c>content='MediaChunks'</c> option); the derived table is dropped and rebuilt
    /// from <c>MediaChunks</c>, which is the source of truth, so no re-import is needed.
    /// <para>
    /// The one-off rebuild is the expensive part: <c>INSERT INTO MediaChunksFts(MediaChunksFts) VALUES('rebuild')</c>
    /// re-tokenizes every snippet, which can take several minutes for a ~150k-chunk library. It runs inside
    /// <see cref="InitializeAsync"/> (which does not take a transaction here, so WAL keeps other readers usable).
    /// No automatic backup is taken; the database is a rebuildable cache of the source files, and an operator wanting a
    /// snapshot should close the app and copy <c>media_index.db</c> (with its <c>-wal</c>/<c>-shm</c> sidecars) before the
    /// first P1b start.
    /// </para>
    /// </summary>
    private static async Task EnsureFtsSchemaAsync(SqliteConnection conn, CancellationToken cancellationToken)
    {
        // MediaChunks.FileName is the denormalized column the external-content FTS reads back. Databases created before
        // this change lack it, so add it and back-fill it from the owning asset before the index is (re)built.
        bool hasFileNameColumn;
        await using (var col = new SqliteCommand(
            "SELECT count(*) FROM pragma_table_info('MediaChunks') WHERE name = 'FileName';", conn))
        {
            hasFileNameColumn = Convert.ToInt64(await col.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
        }

        if (!hasFileNameColumn)
        {
            await using (var addColumn = new SqliteCommand("ALTER TABLE MediaChunks ADD COLUMN FileName TEXT;", conn))
            {
                await addColumn.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var backfill = new SqliteCommand(
                "UPDATE MediaChunks SET FileName = (SELECT a.FileName FROM MediaAssets a WHERE a.Id = MediaChunks.AssetId) " +
                "WHERE FileName IS NULL;", conn))
            {
                await backfill.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        // Version check: a pre-P1b database has the old standalone FTS table, whose stored SQL never mentions
        // content='MediaChunks'. Anything already carrying the content-sync definition is left as is.
        bool ftsExistedBefore = false;
        bool legacyFts = false;
        await using (var meta = new SqliteCommand(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'MediaChunksFts';", conn))
        {
            if (await meta.ExecuteScalarAsync(cancellationToken) is string createSql && !string.IsNullOrEmpty(createSql))
            {
                ftsExistedBefore = true;
                legacyFts = !createSql.Contains("content='MediaChunks'", StringComparison.OrdinalIgnoreCase);
            }
        }

        if (legacyFts)
        {
            // The index is derived, so dropping it (and its rows) loses nothing; it is rebuilt from MediaChunks below.
            await using var drop = new SqliteCommand("DROP TABLE IF EXISTS MediaChunksFts;", conn);
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var create = new SqliteCommand(FtsTableSql, conn))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var triggers = new SqliteCommand(FtsTriggerSql, conn))
        {
            await triggers.ExecuteNonQueryAsync(cancellationToken);
        }

        // Rebuild when the index was the legacy form above, or when it did not exist at all on a database that already
        // has chunks (a build predating FTS). The EXISTS probe is O(1), so a brand-new empty database stays a no-op.
        bool needsRebuild = legacyFts;
        if (!ftsExistedBefore)
        {
            await using var probe = new SqliteCommand("SELECT EXISTS (SELECT 1 FROM MediaChunks LIMIT 1);", conn);
            needsRebuild = Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
        }

        if (needsRebuild)
        {
            await using var rebuild = new SqliteCommand(
                "INSERT INTO MediaChunksFts(MediaChunksFts) VALUES('rebuild');", conn);
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
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
        if (!VecAvailable || query is null || query.Length != EmbeddingDimensions || k <= 0)
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
        if (!VecAvailable || query is null || query.Length != EmbeddingDimensions || k <= 0)
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

    /// <summary>
    /// Suspends the vec0 insert trigger for the duration of a bulk ingest. The trigger would otherwise maintain the
    /// derived index one chunk at a time (a DELETE plus an INSERT plus a binary quantize per row); it is far cheaper to
    /// back-fill in one pass at the end (<see cref="EndBulkIngestAsync"/>). The delete trigger and the full-text
    /// triggers stay in place, so removals and search stay consistent throughout.
    /// </summary>
    public async Task BeginBulkIngestAsync(CancellationToken cancellationToken = default)
    {
        if (!VecAvailable) return;

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);
            await using var cmd = new SqliteCommand("DROP TRIGGER IF EXISTS trg_chunks_vec_ai;", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Ends a bulk ingest: restores the vec0 triggers and back-fills every vector the suspended trigger skipped, in one
    /// pass. The vec0 work is skipped when the extension is unavailable, but the P3 upkeep still runs: one ANALYZE to
    /// refresh planner statistics and a TRUNCATE WAL checkpoint now the run is quiescent.
    /// </summary>
    public async Task EndBulkIngestAsync(CancellationToken cancellationToken = default)
    {
        if (VecAvailable)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                var conn = await EnsureWriteConnectionAsync(cancellationToken);
                // VecTriggerSql drops and re-creates both vec triggers, so the suspended insert trigger comes back here.
                await using var triggers = new SqliteCommand(VecTriggerSql, conn);
                await triggers.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _writeGate.Release();
            }

            await EnsureVecBackfillAsync(cancellationToken);
        }

        // Exactly one ANALYZE per bulk ingest, after every write has landed: it refreshes sqlite_stat1 so the planner
        // cost model reflects the new row counts. It is never run per asset (that would dominate the ingest).
        await AnalyzeAsync(cancellationToken);

        // The ingest has finished and the connection is quiescent, so fold the WAL back into the database and truncate
        // it to journal_size_limit. A PASSIVE checkpoint is the only form allowed while an ingest is still running.
        await CheckpointWalAsync(truncate: true, cancellationToken);
    }

    /// <summary>
    /// Refreshes the query planner statistics with a single <c>ANALYZE</c>. Called once after a bulk ingest
    /// (<see cref="EndBulkIngestAsync"/>), never per asset.
    /// </summary>
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);
            await using var cmd = new SqliteCommand("ANALYZE;", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // --- P3 maintenance policy ---
    // The database is a rebuildable cache of the source files, so the file needs cheap, bounded upkeep, not rewrites:
    //   * WAL + synchronous=NORMAL are the steady state (never OFF); journal_size_limit (64 MB) caps the -wal file.
    //   * CheckpointWalAsync folds the WAL back and truncates it. TRUNCATE at idle/ingest-complete; PASSIVE during an
    //     ingest (it never blocks writers and never truncates).
    //   * ANALYZE runs once per bulk ingest (see AnalyzeAsync), not per asset.
    //   * VACUUM is NEVER automated anywhere. To decide whether a manual VACUUM is worthwhile, inspect the freelist:
    //         PRAGMA freelist_count;   -- pages currently free (reclaimable)
    //         PRAGMA page_count;       -- total pages in the file
    //     A freelist_count/page_count ratio well above ~10% after a large Clear/Delete is the signal to VACUUM by hand
    //     with the app closed; VACUUM rewrites the whole file and needs up to ~2x its size in free disk space.

    /// <summary>
    /// Runs a WAL checkpoint on the writer connection. With <paramref name="truncate"/> (the default) it is
    /// <c>wal_checkpoint(TRUNCATE)</c>: the WAL is folded back into the database and the -wal file is truncated to
    /// <see cref="JournalSizeLimitBytes"/>. Call it when an ingest has finished or the app is otherwise idle. While a
    /// bulk ingest is still running only a PASSIVE checkpoint is safe (it never blocks writers and never truncates), so
    /// pass <paramref name="truncate"/> = <c>false</c> there.
    /// <para>Never runs VACUUM: see the maintenance policy note above.</para>
    /// </summary>
    public async Task CheckpointWalAsync(bool truncate = true, CancellationToken cancellationToken = default)
    {
        string mode = truncate ? "TRUNCATE" : "PASSIVE";
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);
            await using var cmd = new SqliteCommand($"PRAGMA wal_checkpoint({mode});", conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Folds the WAL back into the database and truncates the -wal file, for a backup that copies only
    /// <c>media_index.db</c> (no sidecars). Like <see cref="CheckpointWalAsync"/>, it runs on the writer connection under
    /// <see cref="_writeGate"/>, so it cannot interleave with a write.
    /// <para>
    /// Unlike <see cref="CheckpointWalAsync"/> this <b>reports</b> the outcome instead of ignoring it: the first column
    /// of <c>PRAGMA wal_checkpoint(TRUNCATE)</c> is a busy flag, 1 when another connection held the database and the
    /// truncate could not complete. A backup must know that, because copying a database whose WAL is still populated
    /// would silently drop the frames in it, so the caller refuses to back up when this returns <c>false</c>.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when the checkpoint completed and the WAL was truncated; <c>false</c> when it was busy.</returns>
    public async Task<bool> CheckpointForBackupAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);
            await using var cmd = new SqliteCommand("PRAGMA wal_checkpoint(TRUNCATE);", conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return false;

            // (busy, log, checkpointed): the first column is 0 only when every frame was checkpointed and the WAL truncated.
            long busy = reader.IsDBNull(0) ? 1L : Convert.ToInt64(reader.GetValue(0));
            return busy == 0;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Reads back a freshly restored database and refuses it on any inconsistency: <c>PRAGMA integrity_check</c> over the
    /// whole file, then the FTS5 <c>integrity-check</c> command over the full-text index (which verifies it against the
    /// <c>MediaChunks</c> content table). It runs on the writer connection so the vec0 extension is loaded and the virtual
    /// tables can be inspected: an unloaded vtab module makes <c>integrity_check</c> report spurious errors. It throws
    /// instead of returning a status, because a restore that does not verify must roll back.
    /// </summary>
    public async Task VerifyRestoredDatabaseAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var conn = await EnsureWriteConnectionAsync(cancellationToken);

            var problems = new List<string>();
            await using (var integrity = new SqliteCommand("PRAGMA integrity_check;", conn))
            await using (var reader = await integrity.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    string line = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                    if (line.Length > 0 && !string.Equals(line, "ok", StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add(line);
                    }
                }
            }

            // The full-text index is derived and content-sync, so its own command can compare it against MediaChunks.
            bool hasFts;
            await using (var probe = new SqliteCommand(
                "SELECT count(*) FROM sqlite_master WHERE name = 'MediaChunksFts';", conn))
            {
                hasFts = Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
            }
            if (hasFts)
            {
                try
                {
                    await using var fts = new SqliteCommand(
                        "INSERT INTO MediaChunksFts(MediaChunksFts) VALUES('integrity-check');", conn);
                    await fts.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqliteException ex)
                {
                    problems.Add("MediaChunksFts integrity-check failed: " + ex.Message);
                }
            }

            if (problems.Count > 0)
            {
                throw new InvalidDataException(
                    "The restored database failed its integrity checks: " + string.Join("; ", problems));
            }
        }
        finally
        {
            _writeGate.Release();
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

    public async Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default)
    {
        // Escape keyword for FTS query
        string cleanKeyword = keyword.Replace("\"", "\"\"").Trim();
        if (string.IsNullOrEmpty(cleanKeyword)) return Array.Empty<(Guid, float)>();

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

    private static byte[] ConvertFloatArrayToBytes(float[] floats)
    {
        if (floats == null || floats.Length == 0) return Array.Empty<byte>();
        return MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
    }

    private static float[] ConvertBytesToFloatArray(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return Array.Empty<float>();
        var span = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        return span.ToArray();
    }
}
