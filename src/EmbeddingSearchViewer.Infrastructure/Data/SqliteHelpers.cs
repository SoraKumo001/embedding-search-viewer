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

/// <summary>Small stateless SQLite helpers (statements, scalars, meta table, blob conversion) shared by the repository parts.</summary>
internal static class SqliteHelpers
{
    // --- small helpers of the start-up ---

    /// <summary>Runs one statement that returns no rows.</summary>
    internal static Task ExecuteAsync(SqliteConnection conn, string sql, CancellationToken cancellationToken)
        => ExecuteAsync(conn, sql, null, cancellationToken);

    /// <summary>Runs one statement that returns no rows (in a transaction when there is one).</summary>
    internal static async Task ExecuteAsync(SqliteConnection conn, string sql, SqliteTransaction? tx, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Reads the first column of the first row as text (null when there is no row).</summary>
    internal static async Task<string?> ScalarStringAsync(SqliteConnection conn, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand(sql, conn);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>Reads the first column of the first row as a number (0 when there is no row).</summary>
    internal static Task<long> ScalarLongAsync(SqliteConnection conn, string sql, CancellationToken cancellationToken)
        => ScalarLongAsync(conn, sql, null, cancellationToken);

    /// <summary>Reads the first column of the first row as a number (in a transaction when there is one).</summary>
    internal static async Task<long> ScalarLongAsync(SqliteConnection conn, string sql, SqliteTransaction? tx, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand(sql, conn, tx);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    /// <summary>True when the table has the column.</summary>
    internal static async Task<bool> HasColumnAsync(SqliteConnection conn, string table, string column, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand(
            $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name = '{column}';", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0L) > 0;
    }

    /// <summary>Reads the schema version of the database file.</summary>
    internal static async Task<long> GetUserVersionAsync(SqliteConnection conn, CancellationToken cancellationToken)
        => await ScalarLongAsync(conn, "PRAGMA user_version;", cancellationToken);

    /// <summary>Writes the schema version of the database file.</summary>
    internal static async Task SetUserVersionAsync(SqliteConnection conn, int version, CancellationToken cancellationToken)
        => await ExecuteAsync(conn, $"PRAGMA user_version = {version};", cancellationToken);


    /// <summary>Reads a value of the meta table, or null when the key is not there.</summary>
    internal static async Task<string?> GetMetaAsync(SqliteConnection conn, string key, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand("SELECT Value FROM SchemaMeta WHERE Key = @key;", conn);
        cmd.Parameters.AddWithValue("@key", key);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    /// <summary>Writes a value of the meta table.</summary>
    internal static async Task SetMetaAsync(SqliteConnection conn, string key, string value, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand(
            "INSERT INTO SchemaMeta (Key, Value) VALUES (@key, @value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;",
            conn);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Removes a value of the meta table.</summary>
    internal static async Task DeleteMetaAsync(SqliteConnection conn, string key, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand("DELETE FROM SchemaMeta WHERE Key = @key;", conn);
        cmd.Parameters.AddWithValue("@key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static byte[] ConvertFloatArrayToBytes(float[] floats)
    {
        if (floats == null || floats.Length == 0) return Array.Empty<byte>();
        return MemoryMarshal.AsBytes(floats.AsSpan()).ToArray();
    }

    internal static float[] ConvertBytesToFloatArray(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return Array.Empty<float>();
        var span = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        return span.ToArray();
    }
}
