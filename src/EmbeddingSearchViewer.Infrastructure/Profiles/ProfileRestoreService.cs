using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using Microsoft.Data.Sqlite;

namespace EmbeddingSearchViewer.Infrastructure.Profiles;

/// <summary>What a single-profile backup contains, for a confirmation screen shown before it is restored.</summary>
public sealed record RestorePreview(
    string ProfileId,
    string ProfileName,
    DateTime CreatedAtUtc,
    string AppVersion,
    string? ModelTag,
    int EmbeddingDimensions,
    int AssetCount,
    int ChunkCount,
    bool VecAvailable,
    bool VecTriggers,
    bool CacheIncluded,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> Warnings);

/// <summary>
/// What an all-profiles backup contains, for a confirmation screen shown before it is restored. <see cref="Profiles"/>
/// carries one <see cref="RestorePreview"/> per profile, in the order the manifests were found in the archive.
/// </summary>
public sealed record AllRestorePreview(
    int ProfileCount,
    int FileCount,
    long TotalBytes,
    DateTime? CreatedAtUtc,
    IReadOnlyList<RestorePreview> Profiles,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Lets a restore quiesce (and later resume) the parts of the app that hold the target profile's files open (PR2 seam).
/// Dispose the returned scope to resume. PR2 ships a default that only clears the SQLite pools; the composition root
/// supplies the real one in PR3, where it can also act on the app's own singletons.
/// </summary>
public interface IProfileRestoreQuiescer
{
    /// <summary>Called before the profile folder is replaced; the caller disposes the scope once the swap is done.</summary>
    IDisposable Quiesce(string profileId);
}

/// <summary>
/// Restores backups written by <see cref="ProfileBackupService"/> (PR1). The single-profile form (PR2) is verified
/// against its manifest, the target folder is swapped out under a suspension scope, and the restored database is
/// integrity checked and brought up to schema before the swap is committed; any failure puts the previous folder back.
/// The all-profiles form (PR3) instead moves the whole <c>profiles/</c> tree and <c>profiles.json</c> aside, unpacks the
/// archive into a staging folder, verifies every file by hash and swaps the lot in behind an <c>restore.in-progress</c>
/// marker, so a crash can be recovered on the next start.
/// <para>
/// The single form never writes profiles.json and never restores to a different id: the target is the profile the
/// manifest itself names, and it must still exist in the list. The UI and the composition root are out of scope.
/// </para>
/// </summary>
public sealed class ProfileRestoreService
{
    private const string DatabaseFileName = "media_index.db";
    private const string SingleManifestFileName = "manifest.json";
    private const string ProfilesJsonFileName = "profiles.json";
    private const string ProfilesFolderName = "profiles";

    /// <summary>
    /// Written inside <c>profiles/</c> for the duration of an all-profiles restore. A crash at any point leaves it on
    /// disk (see <see cref="ProfileManager.Load"/>) so the next start can tell the data may be half swapped.
    /// </summary>
    internal const string RestoreInProgressMarkerFileName = "restore.in-progress";

    /// <summary>Attempts at a folder move before it is reported as stuck (a file may still be held open).</summary>
    private const int MoveAttempts = 10;

    private const int MoveRetryDelayMs = 300;

    private static readonly IProfileRestoreQuiescer DefaultQuiescer = new PoolClearingQuiescer();

    private readonly ProfileManager _profiles;
    private readonly IProfileRestoreQuiescer? _quiescer;

    public ProfileRestoreService(ProfileManager profiles, IProfileRestoreQuiescer? quiescer = null)
    {
        _profiles = profiles;
        _quiescer = quiescer;
    }

    /// <summary>
    /// Reads and validates the manifest of a single-profile backup without touching the profile folder. The all-profiles
    /// form is rejected with <see cref="NotSupportedException"/>; a manifest of another version or embedding width is
    /// rejected with <see cref="InvalidDataException"/>. A model-tag mismatch is returned as a warning, not an error.
    /// </summary>
    public Task<RestorePreview> PreviewAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = ReadSingleManifest(zipPath);

        var warnings = new List<string>();
        string currentModel = EmbeddingModelLocation.ModelTag(EmbeddingModelLocation.DefaultPrecision);
        if (!string.IsNullOrWhiteSpace(manifest.ModelTag) &&
            !string.Equals(manifest.ModelTag, currentModel, StringComparison.Ordinal))
        {
            warnings.Add(string.Format(
                "The backup was built with model \"{0}\"; the app now uses \"{1}\". The stored embeddings may not be comparable.",
                manifest.ModelTag, currentModel));
        }

        var preview = new RestorePreview(
            manifest.ProfileId,
            manifest.ProfileName,
            manifest.CreatedAtUtc,
            manifest.AppVersion,
            manifest.ModelTag,
            manifest.EmbeddingDimensions,
            manifest.AssetCount,
            manifest.ChunkCount,
            manifest.VecAvailable,
            manifest.VecTriggers,
            manifest.CacheIncluded,
            manifest.Files.Count,
            manifest.Files.Sum(f => f.Bytes),
            warnings);
        return Task.FromResult(preview);
    }

    /// <summary>
    /// Restores <paramref name="zipPath"/> over the profile its manifest names. The steps are: gate on the supplied busy
    /// state, verify the manifest and that the target profile id still exists, check there is room for the unpacked
    /// payload, suspend, move the current folder aside, unpack and hash-check the archive into a staging folder, swap it
    /// in, then integrity-check and re-index the database. Any failure before that point restores the moved-aside folder.
    /// </summary>
    public async Task RestoreSingleAsync(
        string zipPath,
        Func<BackupBusyState>? getBusyState,
        CancellationToken cancellationToken = default)
    {
        if (getBusyState is not null) ProfileBackupGate.ThrowIfBusy(getBusyState());

        var manifest = ReadSingleManifest(zipPath);
        var target = FindProfileOrThrow(manifest.ProfileId);

        string targetDir = _profiles.DirectoryOf(target.Id);
        long payloadBytes = manifest.Files.Sum(f => f.Bytes);
        EnsureFreeSpace(Path.Combine(_profiles.RootDirectory, ProfilesFolderName), payloadBytes * 2);

        string stagingRoot = CreateRestoreStagingDirectory();
        string stagedProfile = Path.Combine(stagingRoot, "profile");
        string backupDir = targetDir + $".bak-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";

        try
        {
            Directory.CreateDirectory(stagedProfile);
            await ExtractVerifiedAsync(zipPath, manifest, stagedProfile, cancellationToken);

            using var scope = (_quiescer ?? DefaultQuiescer).Quiesce(target.Id);
            SqliteConnection.ClearAllPools(); // belt and braces; the quiescer drops the app's connections first

            bool previousMoved = false;
            try
            {
                if (Directory.Exists(targetDir))
                {
                    await MoveDirectoryWithRetryAsync(targetDir, backupDir, cancellationToken);
                    previousMoved = true;
                }

                await MoveDirectoryWithRetryAsync(stagedProfile, targetDir, cancellationToken);
                await RunPostRestoreMaintenanceAsync(targetDir, cancellationToken);
            }
            catch
            {
                // Undo inside the suspension: put the previous folder back before the app resumes.
                SqliteConnection.ClearAllPools();
                TryDeleteDirectory(targetDir);
                if (previousMoved && Directory.Exists(backupDir))
                {
                    try
                    {
                        await MoveDirectoryWithRetryAsync(backupDir, targetDir, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The previous data is still under backupDir; leaving it there beats deleting it.
                    }
                }
                throw;
            }

            // The restore is committed; the moved-aside copy of the old data is no longer needed.
            TryDeleteDirectory(backupDir);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    /// <summary>
    /// Reads and validates every manifest of an all-profiles backup without touching the profile folders. A single-profile
    /// archive is rejected with <see cref="NotSupportedException"/>; a manifest of another version or embedding width, an
    /// archive without any manifest or without its <c>profiles.json</c> snapshot, and duplicate manifests are rejected
    /// with <see cref="InvalidDataException"/>. A model-tag mismatch is returned as a warning, not an error.
    /// </summary>
    public Task<AllRestorePreview> PreviewAllAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contents = ReadAllArchive(zipPath);

        var warnings = new List<string>();
        string currentModel = EmbeddingModelLocation.ModelTag(EmbeddingModelLocation.DefaultPrecision);
        var profiles = new List<RestorePreview>(contents.Manifests.Count);
        long totalBytes = contents.ProfilesJsonBytes;

        foreach (var manifest in contents.Manifests)
        {
            if (!string.IsNullOrWhiteSpace(manifest.ModelTag) &&
                !string.Equals(manifest.ModelTag, currentModel, StringComparison.Ordinal))
            {
                warnings.Add(string.Format(
                    "\"{0}\" was built with model \"{1}\"; the app now uses \"{2}\". The stored embeddings may not be comparable.",
                    manifest.ProfileName, manifest.ModelTag, currentModel));
            }

            long profileBytes = manifest.Files.Sum(f => f.Bytes);
            totalBytes += profileBytes;
            profiles.Add(new RestorePreview(
                manifest.ProfileId,
                manifest.ProfileName,
                manifest.CreatedAtUtc,
                manifest.AppVersion,
                manifest.ModelTag,
                manifest.EmbeddingDimensions,
                manifest.AssetCount,
                manifest.ChunkCount,
                manifest.VecAvailable,
                manifest.VecTriggers,
                manifest.CacheIncluded,
                manifest.Files.Count,
                profileBytes,
                Array.Empty<string>()));
        }

        DateTime? createdAt = contents.Manifests.Count == 0 ? null : contents.Manifests.Max(m => m.CreatedAtUtc);

        var preview = new AllRestorePreview(
            contents.Manifests.Count,
            contents.Manifests.Sum(m => m.Files.Count),
            totalBytes,
            createdAt,
            profiles,
            warnings);
        return Task.FromResult(preview);
    }

    /// <summary>
    /// Restores an all-profiles backup over the whole profile tree. The steps are: gate on the supplied busy state,
    /// validate every manifest, check there is room for the unpacked payload, write the <c>restore.in-progress</c>
    /// marker, suspend, move <c>profiles/</c> and <c>profiles.json</c> aside on the same volume, unpack and hash-check the
    /// archive into a staging folder, swap the profiles in (and the profiles.json snapshot), then integrity-check and
    /// re-index each restored database. Any failure before that point deletes the partial tree, puts the moved-aside one
    /// back and leaves the marker for the startup recovery; on success the marker and the moved-aside copy are removed.
    /// </summary>
    public async Task RestoreAllAsync(
        string zipPath,
        Func<BackupBusyState>? getBusyState,
        CancellationToken cancellationToken = default)
    {
        if (getBusyState is not null) ProfileBackupGate.ThrowIfBusy(getBusyState());

        var contents = ReadAllArchive(zipPath);
        long payloadBytes = contents.ProfilesJsonBytes + contents.Manifests.Sum(m => m.Files.Sum(f => f.Bytes));
        EnsureFreeSpace(_profiles.RootDirectory, payloadBytes * 2);

        string profilesRoot = Path.Combine(_profiles.RootDirectory, ProfilesFolderName);
        string profilesJsonPath = Path.Combine(_profiles.RootDirectory, ProfilesJsonFileName);
        string markerPath = Path.Combine(profilesRoot, RestoreInProgressMarkerFileName);
        string stamp = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}";
        string bakDir = Path.Combine(_profiles.RootDirectory, $"{ProfilesFolderName}.bak-{stamp}");
        string bakJson = profilesJsonPath + $".bak-{stamp}";

        bool profilesMoved = false;
        bool profilesJsonMoved = false;
        bool liveRecreated = false;
        bool profilesJsonPlaced = false;

        // The marker is written before anything is touched and re-created in the fresh folder below, so a crash at any
        // point leaves a marker on disk: either in profiles/ or, if it was mid-move, inside the profiles.bak-* folder.
        Directory.CreateDirectory(profilesRoot);
        WriteRestoreMarker(markerPath);

        var scopes = new List<IDisposable>();
        try
        {
            foreach (var manifest in contents.Manifests)
            {
                scopes.Add((_quiescer ?? DefaultQuiescer).Quiesce(manifest.ProfileId));
            }
            SqliteConnection.ClearAllPools(); // belt and braces; the quiescer drops the app's connections first

            await MoveDirectoryWithRetryAsync(profilesRoot, bakDir, cancellationToken);
            profilesMoved = true;

            if (File.Exists(profilesJsonPath))
            {
                await MoveFileWithRetryAsync(profilesJsonPath, bakJson, cancellationToken);
                profilesJsonMoved = true;
            }

            Directory.CreateDirectory(profilesRoot);
            liveRecreated = true;
            WriteRestoreMarker(markerPath);

            string stagingRoot = CreateAllRestoreStagingDirectory();
            try
            {
                foreach (var manifest in contents.Manifests)
                {
                    await ExtractVerifiedAsync(zipPath, manifest, stagingRoot, cancellationToken);
                }

                string stagedProfiles = Path.Combine(stagingRoot, ProfilesFolderName);
                foreach (var manifest in contents.Manifests)
                {
                    await MoveDirectoryWithRetryAsync(
                        Path.Combine(stagedProfiles, manifest.ProfileId),
                        Path.Combine(profilesRoot, manifest.ProfileId),
                        cancellationToken);
                }

                // The profiles.json snapshot is not covered by a profile manifest; place it last so a failure above keeps
                // the current list and the undo path can restore it.
                await ExtractFileAsync(
                    zipPath, ProfilesJsonFileName, Path.Combine(stagingRoot, ProfilesJsonFileName), cancellationToken);
                await MoveFileWithRetryAsync(
                    Path.Combine(stagingRoot, ProfilesJsonFileName), profilesJsonPath, cancellationToken);
                profilesJsonPlaced = true;

                foreach (var manifest in contents.Manifests)
                {
                    await RunPostRestoreMaintenanceAsync(
                        Path.Combine(profilesRoot, manifest.ProfileId), cancellationToken);
                }
            }
            finally
            {
                TryDeleteDirectory(stagingRoot);
            }

            // The restore is committed; the marker and the moved-aside copy of the old data are no longer needed.
            TryDeleteFile(markerPath);
            TryDeleteFile(bakJson);
            TryDeleteDirectory(bakDir);
            // TODO(PR3 follow-up): profiles.json on disk has just been replaced, so the caller must reload ProfileManager
            // (or restart) before the restored profile list is visible; this service does not reach into the manager.
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (profilesJsonPlaced) TryDeleteFile(profilesJsonPath);
            if (liveRecreated) TryDeleteDirectory(profilesRoot); // also drops the marker written into the fresh folder
            if (profilesJsonMoved && File.Exists(bakJson))
            {
                try { await MoveFileWithRetryAsync(bakJson, profilesJsonPath, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The previous list is still under bakJson; leaving it there beats deleting it.
                }
            }
            if (profilesMoved && Directory.Exists(bakDir))
            {
                try { await MoveDirectoryWithRetryAsync(bakDir, profilesRoot, CancellationToken.None); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The previous data is still under bakDir; the marker inside it drives the startup recovery.
                }
            }
            // The marker stays behind so the next start can see the restore did not finish.
            throw;
        }
        finally
        {
            for (int i = scopes.Count - 1; i >= 0; i--) scopes[i].Dispose();
        }
    }

    private ProfileInfo FindProfileOrThrow(string profileId)
    {
        var profile = _profiles.Profiles.FirstOrDefault(p => p.Id == profileId);
        return profile ?? throw new InvalidOperationException(Loc.T("Profile.NotFound"));
    }

    /// <summary>Reads the single-profile manifest, rejecting the all-profiles form and unsupported versions/widths.</summary>
    private static ManifestV1 ReadSingleManifest(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("The backup file was not found.", zipPath);

        using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);

        var entry = zip.GetEntry(SingleManifestFileName);
        if (entry is null)
        {
            bool allForm = zip.GetEntry(ProfilesJsonFileName) is not null
                || zip.Entries.Any(e => e.FullName.StartsWith(ProfilesFolderName + "/", StringComparison.Ordinal));
            if (allForm)
            {
                throw new NotSupportedException("This is an all-profiles backup; only a single-profile backup can be restored.");
            }
            throw new InvalidDataException($"The backup does not contain {SingleManifestFileName}.");
        }

        ManifestV1? manifest;
        using (var stream = entry.Open())
        {
            manifest = JsonSerializer.Deserialize<ManifestV1>(stream);
        }

        if (manifest is null) throw new InvalidDataException("The backup manifest could not be read.");
        if (manifest.Version != ManifestV1.SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported backup manifest version: {manifest.Version}.");
        }
        if (manifest.EmbeddingDimensions != ManifestV1.DefaultEmbeddingDimensions)
        {
            throw new InvalidDataException(
                $"The backup holds {manifest.EmbeddingDimensions}-dimension embeddings; this app uses {ManifestV1.DefaultEmbeddingDimensions}.");
        }
        if (string.IsNullOrWhiteSpace(manifest.ProfileId))
        {
            throw new InvalidDataException("The backup manifest does not name a profile.");
        }
        return manifest;
    }

    /// <summary>
    /// Reads (and validates) an all-profiles archive: its <c>profiles.json</c> snapshot and every
    /// <c>profiles/&lt;id&gt;/manifest-&lt;id&gt;.json</c>. The single-profile form is rejected with
    /// <see cref="NotSupportedException"/>. Each manifest is checked for version/width and that it names its own folder,
    /// no profile is recorded twice, and at least one profile and the profiles.json snapshot are present.
    /// </summary>
    private static AllArchiveContents ReadAllArchive(string zipPath)
    {
        if (!File.Exists(zipPath)) throw new FileNotFoundException("The backup file was not found.", zipPath);

        using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);

        if (zip.GetEntry(SingleManifestFileName) is not null)
        {
            throw new NotSupportedException("This is a single-profile backup; use the single-profile restore instead.");
        }

        var profilesJsonEntry = zip.GetEntry(ProfilesJsonFileName);
        var manifests = new List<ManifestV1>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in zip.Entries)
        {
            string path = entry.FullName;
            if (!path.StartsWith(ProfilesFolderName + "/", StringComparison.Ordinal)) continue;
            if (!entry.Name.StartsWith("manifest-", StringComparison.Ordinal) ||
                !entry.Name.EndsWith(".json", StringComparison.Ordinal)) continue;

            var manifest = ReadAndValidateManifest(entry);

            string expectedPath = $"{ProfilesFolderName}/{manifest.ProfileId}/manifest-{manifest.ProfileId}.json";
            if (!string.Equals(path, expectedPath, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The all-profiles backup has a manifest at an unexpected path: {path} (expected {expectedPath}).");
            }

            string prefix = $"{ProfilesFolderName}/{manifest.ProfileId}/";
            foreach (var recorded in manifest.Files)
            {
                if (!recorded.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"The manifest for profile {manifest.ProfileId} records a file outside its folder: {recorded.RelativePath}");
                }
            }

            if (!seen.Add(manifest.ProfileId))
            {
                throw new InvalidDataException($"The backup contains more than one manifest for profile {manifest.ProfileId}.");
            }
            manifests.Add(manifest);
        }

        if (manifests.Count == 0)
        {
            throw new InvalidDataException("The backup does not contain any profile manifest.");
        }
        if (profilesJsonEntry is null)
        {
            throw new InvalidDataException($"The all-profiles backup does not contain {ProfilesJsonFileName}.");
        }

        return new AllArchiveContents(manifests, profilesJsonEntry.Length);
    }

    /// <summary>Reads one manifest entry and rejects an unsupported version/width or a manifest that names no profile.</summary>
    private static ManifestV1 ReadAndValidateManifest(ZipArchiveEntry entry)
    {
        ManifestV1? manifest;
        using (var stream = entry.Open())
        {
            manifest = JsonSerializer.Deserialize<ManifestV1>(stream);
        }

        if (manifest is null)
        {
            throw new InvalidDataException($"The backup manifest could not be read: {entry.FullName}.");
        }
        if (manifest.Version != ManifestV1.SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported backup manifest version: {manifest.Version} ({entry.FullName}).");
        }
        if (manifest.EmbeddingDimensions != ManifestV1.DefaultEmbeddingDimensions)
        {
            throw new InvalidDataException(
                $"The backup holds {manifest.EmbeddingDimensions}-dimension embeddings; this app uses {ManifestV1.DefaultEmbeddingDimensions} ({entry.FullName}).");
        }
        if (string.IsNullOrWhiteSpace(manifest.ProfileId))
        {
            throw new InvalidDataException($"The backup manifest does not name a profile: {entry.FullName}.");
        }
        return manifest;
    }

    /// <summary>Copies one archive entry to <paramref name="destination"/> (used for the un-hashed profiles.json snapshot).</summary>
    private static async Task ExtractFileAsync(
        string zipPath, string entryName, string destination, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);

        var entry = zip.GetEntry(entryName)
            ?? throw new InvalidDataException($"The backup is missing an expected entry: {entryName}");

        string? dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var entryStream = entry.Open();
        await using var destinationStream = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await entryStream.CopyToAsync(destinationStream, cancellationToken);
    }

    /// <summary>
    /// Unpacks exactly the files the manifest lists into <paramref name="stagingDir"/>, hashing each one as it is written
    /// and comparing the digest (and size) against the manifest. Entries outside the staging root are refused (zip slip).
    /// The manifest entry itself is not in <see cref="ManifestV1.Files"/> and so is never unpacked.
    /// </summary>
    private static async Task ExtractVerifiedAsync(
        string zipPath, ManifestV1 manifest, string stagingDir, CancellationToken cancellationToken)
    {
        string stagingRoot = Path.GetFullPath(stagingDir) + Path.DirectorySeparatorChar;

        await using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);

        foreach (var recorded in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.GetEntry(recorded.RelativePath)
                ?? throw new InvalidDataException($"The backup is missing a file it recorded: {recorded.RelativePath}");

            if (entry.Length != recorded.Bytes)
            {
                throw new InvalidDataException(
                    $"Size mismatch in the backup for {recorded.RelativePath}: {entry.Length} != {recorded.Bytes}");
            }

            string relative = recorded.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(stagingDir, relative));
            if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The backup contains an entry outside its root: {recorded.RelativePath}");
            }

            string? dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var entryStream = entry.Open())
            await using (var destinationStream = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                byte[] buffer = new byte[1 << 20];
                int read;
                while ((read = await entryStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await destinationStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    hasher.AppendData(buffer, 0, read);
                }
            }

            string hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(hash, recorded.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Hash mismatch in the backup for {recorded.RelativePath}");
            }
        }
    }

    /// <summary>
    /// Brings the restored directory's database up to date and proves it is sound: an integrity check, then the normal
    /// startup schema/FTS/vec work via <see cref="SqliteMediaRepository.InitializeAsync"/>, then the post-ingest
    /// maintenance (triggers, vec backfill where applicable, ANALYZE and a WAL checkpoint). A database is optional: a
    /// profile without one is restored as just its JSON stores.
    /// </summary>
    private static async Task RunPostRestoreMaintenanceAsync(string profileDir, CancellationToken cancellationToken)
    {
        string dbPath = Path.Combine(profileDir, DatabaseFileName);
        if (!File.Exists(dbPath)) return;

        using var repository = new SqliteMediaRepository(dbPath);
        try
        {
            await repository.VerifyRestoredDatabaseAsync(cancellationToken);
            await repository.InitializeAsync(cancellationToken);
            await repository.EndBulkIngestAsync(cancellationToken);
        }
        finally
        {
            // Release the pooled read connections too, so the folder can be moved/deleted even if a step above failed.
            SqliteConnection.ClearAllPools();
        }
    }

    private static void EnsureFreeSpace(string directory, long requiredBytes)
    {
        if (requiredBytes <= 0) return;

        string full = Path.GetFullPath(directory);
        string root = Path.GetPathRoot(full)
            ?? throw new IOException($"Could not resolve the volume of {directory}.");
        var drive = new DriveInfo(root);
        if (drive.AvailableFreeSpace < requiredBytes)
        {
            throw new IOException(
                $"Not enough free space to restore: {drive.AvailableFreeSpace} bytes available, {requiredBytes} required in {root}.");
        }
    }

    private string CreateRestoreStagingDirectory()
    {
        // A sibling of the profile folders, so the final move is a same-volume rename rather than a copy.
        string profilesRoot = Path.Combine(_profiles.RootDirectory, ProfilesFolderName);
        Directory.CreateDirectory(profilesRoot);
        string staging = Path.Combine(
            profilesRoot,
            $".restore-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(staging);
        return staging;
    }

    private string CreateAllRestoreStagingDirectory()
    {
        // Inside the (freshly created) profiles folder, so every final move is a same-volume rename.
        string profilesRoot = Path.Combine(_profiles.RootDirectory, ProfilesFolderName);
        Directory.CreateDirectory(profilesRoot);
        string staging = Path.Combine(
            profilesRoot,
            $".staging-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(staging);
        return staging;
    }

    private static void WriteRestoreMarker(string markerPath)
    {
        string? dir = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(markerPath, $"restore in progress, started {DateTime.UtcNow:O}{Environment.NewLine}");
    }

    private static async Task MoveDirectoryWithRetryAsync(string from, string to, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MoveAttempts)
                {
                    throw new IOException($"Could not move '{from}' to '{to}' (a file may still be open).", ex);
                }
                await Task.Delay(MoveRetryDelayMs, cancellationToken);
            }
        }
    }

    private static async Task MoveFileWithRetryAsync(string from, string to, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MoveAttempts)
                {
                    throw new IOException($"Could not move '{from}' to '{to}' (a file may still be open).", ex);
                }
                await Task.Delay(MoveRetryDelayMs, cancellationToken);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover staging or .bak folder is harmless; the next run uses a fresh, timestamped one.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover marker or .bak file is harmless; the next run uses fresh, timestamped names.
        }
    }

    /// <summary>The manifests and the profiles.json snapshot read out of an all-profiles archive.</summary>
    private sealed record AllArchiveContents(IReadOnlyList<ManifestV1> Manifests, long ProfilesJsonBytes);

    /// <summary>
    /// PR2's minimum suspension: drop every pooled SQLite connection so the profile folder can be moved and replaced.
    /// </summary>
    private sealed class PoolClearingQuiescer : IProfileRestoreQuiescer
    {
        public IDisposable Quiesce(string profileId)
        {
            // TODO(PR3): the composition root supplies a real quiescer that also disposes the ProfileScopedRepository
            // (closing the current profile's writer connection, mirroring ProfileScopedRepository.OnProfilesChanged) and
            // stops the FolderWatcher from queueing new work (FolderWatcher.Refresh(discardPending: true)). Those two
            // singletons are owned by the app, not by Infrastructure, so they are reached from DI in PR3.
            SqliteConnection.ClearAllPools();
            return NoopScope.Instance;
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new();

        public void Dispose()
        {
            // No state to restore for the pool-clearing default.
        }
    }
}
