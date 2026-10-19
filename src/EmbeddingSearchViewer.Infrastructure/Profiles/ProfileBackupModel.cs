using System;
using System.Collections.Generic;

namespace EmbeddingSearchViewer.Infrastructure.Profiles;

/// <summary>
/// One file recorded in a <see cref="ManifestV1"/>. <see cref="RelativePath"/> is relative to the root of the backup
/// zip (spelled with '/' separators), so the manifest alone is enough to verify or restore the archive.
/// </summary>
public sealed class ManifestFileV1
{
    /// <summary>Path inside the zip, with '/' separators (for example <c>media_index.db</c> or <c>profiles/ab12/media_index.db</c>).</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the file's bytes.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The file length in bytes.</summary>
    public long Bytes { get; set; }
}

/// <summary>
/// The self-describing header of a backup (PR1). It is written as <c>manifest.json</c> for a single-profile backup and,
/// for an all-profiles backup, once per profile as <c>profiles/&lt;id&gt;/manifest-&lt;id&gt;.json</c>; that naming is why a
/// single archive can carry several manifests without ambiguity.
/// <para>
/// The model is deliberately flat and additive: a reader of version 1 must ignore fields it does not know, and a later
/// version can add its own without changing the file names. <see cref="Version"/> is the only mandatory discriminator.
/// </para>
/// </summary>
public sealed class ManifestV1
{
    /// <summary>The schema version this model writes.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The embedding width the index was built with (matches <c>SqliteMediaRepository.EmbeddingDimensions</c>).</summary>
    public const int DefaultEmbeddingDimensions = 768;

    /// <summary>Manifest schema version; always <see cref="SchemaVersion"/> here.</summary>
    public int Version { get; set; } = SchemaVersion;

    /// <summary>The profile the backup was taken from.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>The profile's display name at the time of the backup (informational; the id is the key).</summary>
    public string ProfileName { get; set; } = string.Empty;

    /// <summary>When the backup was taken (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The app version that wrote the backup (from version.txt, without the commit suffix).</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>The model tag recorded in the profile's library.json, when it has one; a different model makes embeddings incomparable.</summary>
    public string? ModelTag { get; set; }

    /// <summary>The embedding width the index must be read back with.</summary>
    public int EmbeddingDimensions { get; set; } = DefaultEmbeddingDimensions;

    /// <summary>Assets in the backed-up database.</summary>
    public int AssetCount { get; set; }

    /// <summary>Chunk rows (and thus vectors) in the backed-up database.</summary>
    public int ChunkCount { get; set; }

    /// <summary>True when the sqlite-vec extension was available to the process that took the backup.</summary>
    public bool VecAvailable { get; set; }

    /// <summary>
    /// True when the backed-up database still carries both vec0 maintenance triggers. The backup step reads them back
    /// from <c>sqlite_master</c>; a database missing them restores its embeddings but needs a backfill, so the fact is
    /// recorded here rather than assumed from <see cref="VecAvailable"/>.
    /// </summary>
    public bool VecTriggers { get; set; } = true;

    /// <summary>True when the profile's cache folder is part of the backup.</summary>
    public bool CacheIncluded { get; set; }

    /// <summary>Every file in the archive with its size and SHA-256 (the manifest itself is not listed).</summary>
    public List<ManifestFileV1> Files { get; set; } = new();
}
