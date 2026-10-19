using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Library;
using Microsoft.Data.Sqlite;

namespace EmbeddingSearchViewer.Infrastructure.Profiles;

/// <summary>A step of a backup, for a progress bar. The stages are advisory and may grow in a later version.</summary>
public sealed record ProfileBackupProgress(string Stage, int Completed = 0, int Total = 0);

/// <summary>
/// Writes a consistent snapshot of one profile (or all of them) into a single zip (PR1: backup only). The database is
/// checkpointed first, so only <c>media_index.db</c> has to be copied; the JSON stores and, optionally, the cache follow
/// through a staging folder on the same volume. Every file is hashed into a <see cref="ManifestV1"/> and the finished
/// archive is read back and re-hashed before the method returns, so a backup that is reported as done is verified.
/// <para>
/// It never restores and never touches the UI: a caller supplies the current busy state (see
/// <see cref="ProfileBackupGate"/>) and the destination path. Restore, the view models and the shell are out of scope.
/// </para>
/// </summary>
public sealed class ProfileBackupService
{
    private const string DatabaseFileName = "media_index.db";
    private const string LibraryFileName = "library.json";
    private const string HistoryFileName = "search_history.json";
    private const string ExclusionsFileName = "excluded_files.json";
    private const string EmptySkipsFileName = "empty_embedding_skips.json";
    private const string CacheFolderName = "cache";
    private const string ProfilesJsonFileName = "profiles.json";
    private const string SingleManifestFileName = "manifest.json";

    private static readonly string[] ProfileJsonFiles = { LibraryFileName, HistoryFileName, ExclusionsFileName, EmptySkipsFileName };

    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };

    private readonly ProfileManager _profiles;
    private readonly Func<BackupBusyState>? _busyState;

    /// <summary>
    /// <paramref name="busyState"/> is read at the start of every backup and refuses the run while the app is writing.
    /// PR1 has no UI wiring, so it may be omitted; PR2/PR3 must always supply it.
    /// </summary>
    public ProfileBackupService(ProfileManager profiles, Func<BackupBusyState>? busyState = null)
    {
        _profiles = profiles;
        _busyState = busyState;
    }

    /// <summary>
    /// Backs up one profile into <paramref name="destZipPath"/>. The archive is flat (<c>manifest.json</c> plus the
    /// profile's files at its root), so it describes exactly one profile.
    /// </summary>
    public async Task BackupSingleAsync(
        string profileId,
        string destZipPath,
        bool includeCache,
        IProgress<ProfileBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var profile = FindProfile(profileId);
        ThrowIfBusy();
        progress?.Report(new ProfileBackupProgress("start"));

        string stagingRoot = CreateStagingDirectory();
        try
        {
            var manifest = await StageProfileAsync(
                profile, _profiles.DirectoryOf(profile.Id), stagingRoot, basePrefix: string.Empty,
                includeCache, progress, cancellationToken);

            await WriteManifestAsync(stagingRoot, manifest, SingleManifestFileName, cancellationToken);

            await CreateZipAsync(stagingRoot, destZipPath, progress, cancellationToken);
            await VerifyZipAsync(destZipPath, new[] { (SingleManifestFileName, manifest) }, progress, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    /// <summary>
    /// Backs up every profile plus the profiles.json snapshot into <paramref name="destZipPath"/>. The archive keeps the
    /// normal layout (<c>profiles.json</c> and <c>profiles/&lt;id&gt;/...</c>), with one <c>manifest-&lt;id&gt;.json</c>
    /// per profile, which is the shape the manifest model is named for.
    /// </summary>
    public async Task BackupAllAsync(
        string destZipPath,
        bool includeCache,
        IProgress<ProfileBackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfBusy();
        progress?.Report(new ProfileBackupProgress("start"));

        string stagingRoot = CreateStagingDirectory();
        try
        {
            string profilesJson = Path.Combine(_profiles.RootDirectory, ProfilesJsonFileName);
            if (File.Exists(profilesJson))
            {
                File.Copy(profilesJson, Path.Combine(stagingRoot, ProfilesJsonFileName), overwrite: true);
            }

            var manifests = new List<(string EntryName, ManifestV1 Manifest)>();
            var all = _profiles.Profiles.ToList();
            for (int i = 0; i < all.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profile = all[i];
                string prefix = $"profiles/{profile.Id}/";
                string profileStage = Path.Combine(stagingRoot, "profiles", profile.Id);

                var manifest = await StageProfileAsync(
                    profile, _profiles.DirectoryOf(profile.Id), profileStage, prefix,
                    includeCache, progress, cancellationToken);

                string entryName = prefix + $"manifest-{profile.Id}.json";
                await WriteManifestAsync(stagingRoot, manifest, entryName, cancellationToken);
                manifests.Add((entryName, manifest));
            }

            await CreateZipAsync(stagingRoot, destZipPath, progress, cancellationToken);
            await VerifyZipAsync(destZipPath, manifests, progress, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    private ProfileInfo FindProfile(string profileId)
    {
        var profile = _profiles.Profiles.FirstOrDefault(p => p.Id == profileId);
        return profile ?? throw new InvalidOperationException(Loc.T("Profile.NotFound"));
    }

    private void ThrowIfBusy()
    {
        // PR1 does not wire the UI, so a caller may not have a state provider yet; PR2/PR3 always pass one.
        if (_busyState is null) return;
        ProfileBackupGate.ThrowIfBusy(_busyState());
    }

    /// <summary>
    /// Copies one profile into <paramref name="targetDir"/> and returns the manifest describing what was staged.
    /// <paramref name="basePrefix"/> is prepended to every recorded path so the same routine serves both the flat
    /// single-profile archive and the nested all-profiles one.
    /// </summary>
    private static async Task<ManifestV1> StageProfileAsync(
        ProfileInfo profile,
        string sourceDir,
        string targetDir,
        string basePrefix,
        bool includeCache,
        IProgress<ProfileBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetDir);

        var manifest = new ManifestV1
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            CreatedAtUtc = DateTime.UtcNow,
            AppVersion = GetAppVersion(),
            VecAvailable = SqliteMediaRepository.VecAvailable,
            CacheIncluded = includeCache
        };

        // The model tag lives in library.json; it decides whether the restored embeddings are comparable.
        string sourceLibrary = Path.Combine(sourceDir, LibraryFileName);
        if (File.Exists(sourceLibrary))
        {
            manifest.ModelTag = new LibrarySettingsStore(sourceLibrary).Load().IndexModelTag;
        }

        string dbSource = Path.Combine(sourceDir, DatabaseFileName);
        if (File.Exists(dbSource))
        {
            await CheckpointForBackupAsync(dbSource, cancellationToken);

            var meta = await ReadDatabaseMetadataAsync(dbSource, cancellationToken);
            manifest.AssetCount = meta.AssetCount;
            manifest.ChunkCount = meta.ChunkCount;
            manifest.VecTriggers = meta.VecTriggers;

            // Only the database file itself is staged: the -wal/-shm sidecars are folded away by the checkpoint above.
            File.Copy(dbSource, Path.Combine(targetDir, DatabaseFileName), overwrite: true);
        }

        foreach (var name in ProfileJsonFiles)
        {
            string source = Path.Combine(sourceDir, name);
            if (File.Exists(source)) File.Copy(source, Path.Combine(targetDir, name), overwrite: true);
        }

        if (includeCache)
        {
            string cacheSource = Path.Combine(sourceDir, CacheFolderName);
            if (Directory.Exists(cacheSource)) CopyDirectory(cacheSource, Path.Combine(targetDir, CacheFolderName));
        }

        // Hash the staged bytes, not the live files: this is what the archive must contain. The manifest itself is
        // written afterwards and is deliberately not part of the list.
        foreach (var file in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(targetDir, file).Replace('\\', '/');
            manifest.Files.Add(new ManifestFileV1
            {
                RelativePath = basePrefix + relative,
                Sha256 = await ComputeSha256Async(file, cancellationToken),
                Bytes = new FileInfo(file).Length
            });
        }

        progress?.Report(new ProfileBackupProgress("staged:" + profile.Id));
        return manifest;
    }

    /// <summary>
    /// Truncates the profile's WAL so that copying <c>media_index.db</c> alone is complete. A busy database is an error
    /// here: proceeding would produce an archive that silently misses whatever is still in the -wal file.
    /// </summary>
    private static async Task CheckpointForBackupAsync(string dbPath, CancellationToken cancellationToken)
    {
        using (var repository = new SqliteMediaRepository(dbPath))
        {
            if (!await repository.CheckpointForBackupAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    $"The database is busy and could not be checkpointed for backup: {dbPath}");
            }
        }

        // A pooled connection would keep the file open; the copy below must see the checkpointed bytes.
        SqliteConnection.ClearAllPools();
    }

    private static async Task<(int AssetCount, int ChunkCount, bool VecTriggers)> ReadDatabaseMetadataAsync(
        string dbPath, CancellationToken cancellationToken)
    {
        // Read-write (not read-only): a WAL database needs its -shm to be openable, and this connection only selects.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var triggers = new HashSet<string>(StringComparer.Ordinal);
        await using (var cmd = new SqliteCommand(
            "SELECT name FROM sqlite_master WHERE type = 'trigger' AND name IN ('trg_chunks_vec_ai', 'trg_chunks_vec_ad');",
            connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (!reader.IsDBNull(0)) triggers.Add(reader.GetString(0));
            }
        }

        int assets = await CountAsync(connection, "SELECT count(*) FROM MediaAssets;", cancellationToken);
        int chunks = await CountAsync(connection, "SELECT count(*) FROM MediaChunks;", cancellationToken);
        bool vecTriggers = triggers.Contains("trg_chunks_vec_ai") && triggers.Contains("trg_chunks_vec_ad");
        return (assets, chunks, vecTriggers);
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = new SqliteCommand(sql, connection);
        object? value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static async Task WriteManifestAsync(
        string stagingRoot, ManifestV1 manifest, string entryName, CancellationToken cancellationToken)
    {
        string path = Path.Combine(stagingRoot, entryName.Replace('/', Path.DirectorySeparatorChar));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, manifest, ManifestJsonOptions, cancellationToken);
    }

    private static async Task CreateZipAsync(
        string stagingRoot, string destZipPath, IProgress<ProfileBackupProgress>? progress, CancellationToken cancellationToken)
    {
        string? dir = Path.GetDirectoryName(destZipPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        progress?.Report(new ProfileBackupProgress("zip"));

        await using var file = new FileStream(destZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var path in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string entryName = Path.GetRelativePath(stagingRoot, path).Replace('\\', '/');

            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
            await source.CopyToAsync(entryStream, cancellationToken);
        }
    }

    /// <summary>
    /// Reads the finished archive back and re-hashes every recorded file against its manifest. A verified backup is the
    /// only one this service reports as done, so a truncated or corrupted zip fails here rather than at restore time.
    /// </summary>
    private static async Task VerifyZipAsync(
        string destZipPath,
        IReadOnlyList<(string EntryName, ManifestV1 Manifest)> manifests,
        IProgress<ProfileBackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new ProfileBackupProgress("verify"));

        await using var file = new FileStream(destZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);

        foreach (var (entryName, _) in manifests)
        {
            var manifestEntry = zip.GetEntry(entryName)
                ?? throw new InvalidDataException($"The backup is missing its manifest entry: {entryName}");

            ManifestV1? stored;
            await using (var stream = manifestEntry.Open())
            {
                stored = await JsonSerializer.DeserializeAsync<ManifestV1>(stream, cancellationToken: cancellationToken);
            }
            if (stored is null)
            {
                throw new InvalidDataException($"The manifest in the backup could not be read back: {entryName}");
            }

            foreach (var recorded in stored.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(recorded.RelativePath)
                    ?? throw new InvalidDataException($"The backup is missing a file it recorded: {recorded.RelativePath}");

                if (entry.Length != recorded.Bytes)
                {
                    throw new InvalidDataException(
                        $"Size mismatch in the backup for {recorded.RelativePath}: {entry.Length} != {recorded.Bytes}");
                }

                await using var entryStream = entry.Open();
                string hash = await ComputeSha256Async(entryStream, cancellationToken);
                if (!string.Equals(hash, recorded.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Hash mismatch in the backup for {recorded.RelativePath}");
                }
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return await ComputeSha256Async(stream, cancellationToken);
    }

    private static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken)
    {
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            string? dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(file, target, overwrite: true);
        }
    }

    private string CreateStagingDirectory()
    {
        // Same volume as the profiles (a sibling folder), so the copy within it stays cheap and never crosses devices.
        string profilesRoot = Path.Combine(_profiles.RootDirectory, "profiles");
        Directory.CreateDirectory(profilesRoot);
        string staging = Path.Combine(
            profilesRoot,
            $".staging-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(staging);
        return staging;
    }

    private static string GetAppVersion()
    {
        var info = typeof(ProfileBackupService).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        // "1.2.3+<commit>" -> "1.2.3" (same shape the settings screen shows).
        return string.IsNullOrEmpty(info) ? "unknown" : info.Split('+')[0];
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover staging folder is harmless; the next backup uses a fresh, timestamped one.
        }
    }
}
