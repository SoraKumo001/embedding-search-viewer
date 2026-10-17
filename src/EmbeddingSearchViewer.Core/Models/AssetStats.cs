namespace EmbeddingSearchViewer.Core.Models;

/// <summary>The number of indexed files, in total and by type.</summary>
public readonly record struct AssetStats(int Total, int Images, int Pdfs, int Videos, int Documents = 0);
