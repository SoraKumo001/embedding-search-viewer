using System;
using System.Collections.Generic;

namespace EmbeddingSearchViewer.Core.Models;

public class SearchQuery
{
    public string QueryText { get; set; } = string.Empty;
    public string? QueryImagePath { get; set; }
    public string? QueryAudioPath { get; set; }
    public MediaType? MediaTypeFilter { get; set; }
    public int TruncateDim { get; set; } = 768;
    public int TopK { get; set; } = 20;
    public float MinSimilarity { get; set; } = 0.25f;
    public bool EnableFts { get; set; } = true;

    // Optional metadata filters (null = no restriction)
    public DateTime? ModifiedFromUtc { get; set; }   // inclusive
    public DateTime? ModifiedToUtc { get; set; }     // exclusive
    public long? MinSizeBytes { get; set; }          // inclusive
    public long? MaxSizeBytes { get; set; }          // inclusive
    public string? FolderContains { get; set; }      // case-insensitive substring of the file's directory path

    public bool HasMetadataFilters =>
        ModifiedFromUtc.HasValue || ModifiedToUtc.HasValue || MinSizeBytes.HasValue || MaxSizeBytes.HasValue ||
        !string.IsNullOrWhiteSpace(FolderContains);
}

public class SearchResultItem
{
    public MediaChunk Chunk { get; set; } = null!;
    public MediaAsset Asset { get; set; } = null!;
    public float SimilarityScore { get; set; }
    public float FtsScore { get; set; }
    public float CombinedScore { get; set; }

    /// <summary>Paths of other files whose embedding is identical to this one (they are hidden from the result list).</summary>
    public List<string> DuplicatePaths { get; } = new();
    public int DuplicateCount => DuplicatePaths.Count;
    public string DuplicateText => DuplicateCount > 0 ? $"同一画像 他{DuplicateCount}件" : string.Empty;
    public string SimilarityPercentageText => SimilarityScore.ToString("P0");

    public string DisplayTitle => Asset.FileName;
    public string Subtitle => Chunk.Modality switch
    {
        ModalityType.PageImage or ModalityType.Text when Chunk.PageNumber.HasValue 
            => $"ページ {Chunk.PageNumber.Value} / {Asset.DurationOrPages ?? 0}",
        ModalityType.VideoSegment when Chunk.StartTimeMs.HasValue && Chunk.EndTimeMs.HasValue
            => $"{FormatTime(Chunk.StartTimeMs.Value)} - {FormatTime(Chunk.EndTimeMs.Value)}",
        ModalityType.AudioSegment when Chunk.StartTimeMs.HasValue
            => $"{FormatTime(Chunk.StartTimeMs.Value)}",
        _ => Asset.MediaType.ToString()
    };

    private static string FormatTime(int ms)
    {
        var ts = System.TimeSpan.FromMilliseconds(ms);
        return ts.Hours > 0 ? ts.ToString(@"hh\:mm\:ss") : ts.ToString(@"mm\:ss");
    }
}
