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

    /// <summary>The manifests and the profiles.json snapshot read out of an all-profiles archive.</summary>
    private sealed record AllArchiveContents(IReadOnlyList<ManifestV1> Manifests, long ProfilesJsonBytes);
}
