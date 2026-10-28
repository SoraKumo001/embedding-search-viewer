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

public partial class SqliteMediaRepository : IMediaRepository, IDisposable
{
    /// <summary>
    /// Upper bound on the write-ahead log: after a checkpoint the WAL is truncated back to at most this size instead of
    /// growing without limit between checkpoints. 64 MB keeps a bulk ingest from leaving a multi-gigabyte -wal behind.
    /// </summary>
    private const long JournalSizeLimitBytes = 64L * 1024 * 1024;

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

    /// <summary>True once a connection has successfully loaded the vec0 extension.</summary>
    public static bool VecAvailable => SqliteNative.VecAvailable;

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

        // MediaChunksFts is content-sync: the AFTER DELETE trigger normally clears its entries as the chunk rows go, so
        // it must not be deleted from directly (a direct DELETE on an external-content table would wipe the whole
        // index). ClearAllAsync wipes it explicitly, before the chunk rows go, only while the triggers are dropped for
        // a rebuild.
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

        // The slow part of the start-up stops with the repository: the database file must not be held any longer, so
        // the folder of the profile can be deleted right after a profile switch. The batches check the cancellation,
        // so it is observed quickly. It is not waited for here: Dispose runs on the profile-switch (UI) thread.
        try { _slowPhaseCts.Cancel(); } catch (ObjectDisposedException) { }
        Task? slowPhase = _slowPhaseTask;

        // Dispose must not block the caller. The usual case has no writer in flight, so the writer connection (it is
        // not pooled) is closed right away; when a write is running, the wait for it and the close are handed to the
        // background instead of holding the UI thread for as long as the write takes.
        if (_writeGate.Wait(0))
        {
            try
            {
                ResetWriteConnection();
            }
            finally
            {
                _writeGate.Release();
                _writeGate.Dispose();
            }
            return;
        }

        _ = Task.Run(() =>
        {
            if (slowPhase is not null)
            {
                try { slowPhase.Wait(); }
                catch (AggregateException) { /* the slow part failed; it is reported in its own log */ }
                catch (OperationCanceledException) { }
            }

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
        });
    }

    /// <summary>
    /// The single place connections are created: initialises the SQLitePCLRaw provider exactly once and tries to load
    /// the vec0 extension (when it is missing, callers silently fall back to the brute-force scan).
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        SqliteNative.EnsureNativeInitialized();
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

            SqliteNative.TryLoadVec(conn);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}
