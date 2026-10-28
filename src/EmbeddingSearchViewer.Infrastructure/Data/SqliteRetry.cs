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

/// <summary>Transient-lock detection and the exponential backoff the repository's write and read paths retry with.</summary>
internal static class SqliteRetry
{
    /// <summary>Most attempts one write transaction makes before a transient lock error is surfaced to the caller.</summary>
    internal const int MaxWriteAttempts = 5;

    /// <summary>Most attempts a read makes before it gives up (reads never hold the writer gate, so fewer suffice).</summary>
    internal const int MaxReadAttempts = 3;

    /// <summary>Base backoff of the retry helpers: 50/100/200/400 ms doubling per attempt, plus up to this much jitter.</summary>
    internal const int RetryBaseDelayMs = 50;

    /// <summary>
    /// True for the transient lock-contention errors a short backoff can clear: SQLITE_BUSY (5, "database is locked")
    /// and SQLITE_LOCKED (6, "database table is locked"). busy_timeout waits out a plain BUSY but not LOCKED, so both
    /// are retried explicitly here.
    /// </summary>
    internal static bool IsTransientLock(SqliteException ex) => ex.SqliteErrorCode is 5 or 6;

    /// <summary>Exponential backoff (50/100/200/400 ms) plus up to one base delay of jitter, to de-sync retriers.</summary>
    internal static int RetryDelayMs(int attempt) =>
        (RetryBaseDelayMs << (attempt - 1)) + Random.Shared.Next(0, RetryBaseDelayMs);

    /// <summary>
    /// Runs one transaction attempt, retrying transient lock contention (SQLITE_BUSY/SQLITE_LOCKED) with exponential
    /// backoff. The caller's delegate must create, commit and dispose its own transaction so every attempt starts from
    /// a clean one. Cancellation and every non-transient failure propagate unchanged; the writer connection is reset by
    /// the caller on such a failure, not here, so a retry deliberately reuses it.
    /// </summary>
    internal static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> attempt, CancellationToken cancellationToken)
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
    internal static Task ExecuteWithRetryAsync(Func<Task> attempt, CancellationToken cancellationToken) =>
        ExecuteWithRetryAsync(async () => { await attempt(); return true; }, cancellationToken);
}
