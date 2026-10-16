using System;

namespace EmbeddingSearchViewer.Core.Models;

public class MediaChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AssetId { get; set; }
    public ModalityType Modality { get; set; }
    public int ChunkIndex { get; set; }
    public int? PageNumber { get; set; }
    public int? StartTimeMs { get; set; }
    public int? EndTimeMs { get; set; }
    public string? SnippetText { get; set; }
    public string? PreviewImagePath { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public int EmbeddingDim => Embedding.Length;
}
