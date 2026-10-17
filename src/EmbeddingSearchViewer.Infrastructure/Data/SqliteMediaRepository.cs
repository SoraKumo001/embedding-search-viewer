using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using Microsoft.Data.Sqlite;

namespace EmbeddingSearchViewer.Infrastructure.Data;

public class SqliteMediaRepository : IMediaRepository
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private long _dataVersion;

    public long DataVersion => Interlocked.Read(ref _dataVersion);

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
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = """
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
                FOREIGN KEY (AssetId) REFERENCES MediaAssets(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_MediaChunks_AssetId ON MediaChunks(AssetId);

            CREATE VIRTUAL TABLE IF NOT EXISTS MediaChunksFts USING fts5(
                ChunkId UNINDEXED,
                FileName,
                SnippetText,
                tokenize = 'unicode61'
            );
        """;

        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

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
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

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

    public async Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        const string sql = "SELECT Id, FilePath, FileName, MediaType, FileSizeBytes, FileHash, LastModifiedUtc, ThumbnailPath, DurationOrPages, IndexedAtUtc FROM MediaAssets;";
        await using var cmd = new SqliteCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var list = new List<MediaAsset>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(ReadAsset(reader));
        }
        return list;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAssetHashesAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new SqliteCommand("SELECT FilePath, FileHash FROM MediaAssets;", conn);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }
        return result;
    }

    public async Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = conn.BeginTransaction();

        // 1. Delete what is stored for this asset, and for an earlier asset of the same file (a changed file is saved
        //    with a new id; its path is unique, so the old row has to go first)
        const string sameAsset = "SELECT Id FROM MediaAssets WHERE Id = @assetId OR FilePath = @filePath";
        await using (var delCmd = new SqliteCommand(
            $"DELETE FROM MediaChunksFts WHERE ChunkId IN (SELECT Id FROM MediaChunks WHERE AssetId IN ({sameAsset})); " +
            $"DELETE FROM MediaChunks WHERE AssetId IN ({sameAsset}); " +
            "DELETE FROM MediaAssets WHERE Id = @assetId OR FilePath = @filePath;", conn, tx))
        {
            delCmd.Parameters.AddWithValue("@assetId", asset.Id.ToString());
            delCmd.Parameters.AddWithValue("@filePath", asset.FilePath);
            await delCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2. Insert Asset
        const string insertAssetSql = """
            INSERT INTO MediaAssets (Id, FilePath, FileName, MediaType, FileSizeBytes, FileHash, LastModifiedUtc, ThumbnailPath, DurationOrPages, IndexedAtUtc)
            VALUES (@Id, @FilePath, @FileName, @MediaType, @FileSizeBytes, @FileHash, @LastModifiedUtc, @ThumbnailPath, @DurationOrPages, @IndexedAtUtc);
        """;
        await using (var cmd = new SqliteCommand(insertAssetSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("@Id", asset.Id.ToString());
            cmd.Parameters.AddWithValue("@FilePath", asset.FilePath);
            cmd.Parameters.AddWithValue("@FileName", asset.FileName);
            cmd.Parameters.AddWithValue("@MediaType", asset.MediaType.ToString());
            cmd.Parameters.AddWithValue("@FileSizeBytes", asset.FileSizeBytes);
            cmd.Parameters.AddWithValue("@FileHash", asset.FileHash);
            cmd.Parameters.AddWithValue("@LastModifiedUtc", asset.LastModifiedUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@ThumbnailPath", (object?)asset.ThumbnailPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DurationOrPages", (object?)asset.DurationOrPages ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@IndexedAtUtc", asset.IndexedAtUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 3. Insert Chunks and FTS entries
        const string insertChunkSql = """
            INSERT INTO MediaChunks (Id, AssetId, Modality, ChunkIndex, PageNumber, StartTimeMs, EndTimeMs, SnippetText, PreviewImagePath, Embedding)
            VALUES (@Id, @AssetId, @Modality, @ChunkIndex, @PageNumber, @StartTimeMs, @EndTimeMs, @SnippetText, @PreviewImagePath, @Embedding);
        """;
        const string insertFtsSql = """
            INSERT INTO MediaChunksFts (ChunkId, FileName, SnippetText)
            VALUES (@ChunkId, @FileName, @SnippetText);
        """;

        foreach (var chunk in chunks)
        {
            byte[] embeddingBytes = ConvertFloatArrayToBytes(chunk.Embedding);
            await using (var chunkCmd = new SqliteCommand(insertChunkSql, conn, tx))
            {
                chunkCmd.Parameters.AddWithValue("@Id", chunk.Id.ToString());
                chunkCmd.Parameters.AddWithValue("@AssetId", asset.Id.ToString());
                chunkCmd.Parameters.AddWithValue("@Modality", chunk.Modality.ToString());
                chunkCmd.Parameters.AddWithValue("@ChunkIndex", chunk.ChunkIndex);
                chunkCmd.Parameters.AddWithValue("@PageNumber", (object?)chunk.PageNumber ?? DBNull.Value);
                chunkCmd.Parameters.AddWithValue("@StartTimeMs", (object?)chunk.StartTimeMs ?? DBNull.Value);
                chunkCmd.Parameters.AddWithValue("@EndTimeMs", (object?)chunk.EndTimeMs ?? DBNull.Value);
                chunkCmd.Parameters.AddWithValue("@SnippetText", (object?)chunk.SnippetText ?? DBNull.Value);
                chunkCmd.Parameters.AddWithValue("@PreviewImagePath", (object?)chunk.PreviewImagePath ?? DBNull.Value);
                chunkCmd.Parameters.AddWithValue("@Embedding", embeddingBytes);
                await chunkCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(chunk.SnippetText))
            {
                await using var ftsCmd = new SqliteCommand(insertFtsSql, conn, tx);
                ftsCmd.Parameters.AddWithValue("@ChunkId", chunk.Id.ToString());
                ftsCmd.Parameters.AddWithValue("@FileName", asset.FileName);
                ftsCmd.Parameters.AddWithValue("@SnippetText", chunk.SnippetText);
                await ftsCmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await tx.CommitAsync(cancellationToken);
        Interlocked.Increment(ref _dataVersion);
    }

    public async Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = conn.BeginTransaction();

        const string sql = """
            DELETE FROM MediaChunksFts WHERE ChunkId IN (SELECT Id FROM MediaChunks WHERE AssetId = @assetId);
            DELETE FROM MediaChunks WHERE AssetId = @assetId;
            DELETE FROM MediaAssets WHERE Id = @assetId;
        """;
        await using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@assetId", assetId.ToString());
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        Interlocked.Increment(ref _dataVersion);
    }

    public async Task DeleteAssetsAsync(IEnumerable<Guid> assetIds, CancellationToken cancellationToken = default)
    {
        var ids = assetIds.ToList();
        if (ids.Count == 0) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = conn.BeginTransaction();

        // Delete in batches of up to 500 to stay well within SQLite parameter limits
        const int batchSize = 500;
        for (int i = 0; i < ids.Count; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = ids.Skip(i).Take(batchSize).ToList();
            var placeholders = string.Join(",", chunk.Select((_, idx) => $"@id{idx}"));

            string sql = $"""
                DELETE FROM MediaChunksFts WHERE ChunkId IN (SELECT Id FROM MediaChunks WHERE AssetId IN ({placeholders}));
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
        Interlocked.Increment(ref _dataVersion);
    }

    public async Task ClearAllAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = conn.BeginTransaction();

        const string sql = """
            DELETE FROM MediaChunksFts;
            DELETE FROM MediaChunks;
            DELETE FROM MediaAssets;
        """;
        await using var cmd = new SqliteCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        Interlocked.Increment(ref _dataVersion);
    }

    public async Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

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
    }

    public async Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Escape keyword for FTS query
        string cleanKeyword = keyword.Replace("\"", "\"\"").Trim();
        if (string.IsNullOrEmpty(cleanKeyword)) return Array.Empty<(Guid, float)>();

        const string sql = """
            SELECT ChunkId, bm25(MediaChunksFts) AS Rank
            FROM MediaChunksFts
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
        catch (SqliteException)
        {
            // If FTS match query syntax is invalid, ignore
        }

        return results;
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
