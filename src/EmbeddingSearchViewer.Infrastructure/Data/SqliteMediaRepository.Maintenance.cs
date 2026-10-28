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
}
