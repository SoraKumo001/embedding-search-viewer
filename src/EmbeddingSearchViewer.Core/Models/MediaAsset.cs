using System;

namespace EmbeddingSearchViewer.Core.Models;

public class MediaAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public MediaType MediaType { get; set; } = MediaType.Other;
    public long FileSizeBytes { get; set; }
    public string FileHash { get; set; } = string.Empty;
    public DateTime LastModifiedUtc { get; set; }
    public string? ThumbnailPath { get; set; }
    public int? DurationOrPages { get; set; }
    public DateTime IndexedAtUtc { get; set; } = DateTime.UtcNow;

    public static string ComputeFileHash(long length, long lastWriteTimeUtcTicks) =>
        $"{length}_{lastWriteTimeUtcTicks}";

    public static string ComputeFileHash(System.IO.FileInfo fileInfo) =>
        ComputeFileHash(fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
}
