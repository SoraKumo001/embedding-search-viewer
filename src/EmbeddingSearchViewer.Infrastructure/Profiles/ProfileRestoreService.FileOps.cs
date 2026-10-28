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

public sealed partial class ProfileRestoreService
{
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
}
