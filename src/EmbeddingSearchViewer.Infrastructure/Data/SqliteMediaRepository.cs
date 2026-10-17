using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using Microsoft.Data.Sqlite;

namespace EmbeddingSearchViewer.Infrastructure.Data;

public class SqliteMediaRepository : IMediaRepository
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

    private readonly string _connectionString;
    private readonly string _dbPath;
    private long _dataVersion;

    public long DataVersion => Interlocked.Read(ref _dataVersion);

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
            Cache = SqliteCacheMode.Shared
        }.ToString();
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

        await using (var cmd = new SqliteCommand(sql, conn))
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

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
            await using var conn = await OpenConnectionAsync(cancellationToken);

            // sqlite-vec wants the query as a little-endian float32 blob (the native layout on x64).
            byte[] queryBytes = MemoryMarshal.AsBytes(query.AsSpan()).ToArray();

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
            await using var conn = await OpenConnectionAsync(cancellationToken);

            byte[] queryBytes = MemoryMarshal.AsBytes(query.AsSpan()).ToArray();

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

    public async Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);

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

    public async Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenConnectionAsync(cancellationToken);
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
        await using var conn = await OpenConnectionAsync(cancellationToken);
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

        await using var conn = await OpenConnectionAsync(cancellationToken);
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
        await using var conn = await OpenConnectionAsync(cancellationToken);
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
        await using var conn = await OpenConnectionAsync(cancellationToken);

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
        await using var conn = await OpenConnectionAsync(cancellationToken);

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
