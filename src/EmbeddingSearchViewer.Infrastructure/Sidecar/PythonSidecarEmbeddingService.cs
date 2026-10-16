using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;

namespace EmbeddingSearchViewer.Infrastructure.Sidecar;

public class PythonSidecarEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public PythonSidecarEmbeddingService(string baseUrl = "http://127.0.0.1:5000", HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await _httpClient.GetAsync($"{_baseUrl}/health", cancellationToken);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<float[]> GenerateQueryEmbeddingAsync(
        string query,
        string modality = "text",
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var payload = new QueryRequestDto
        {
            Query = query,
            Modality = modality,
            TruncateDim = truncateDim
        };

        var content = JsonContent.Create(payload, SidecarJsonContext.Default.QueryRequestDto);
        var res = await _httpClient.PostAsync($"{_baseUrl}/embed/query", content, cancellationToken);
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync(SidecarJsonContext.Default.QueryResponseDto, cancellationToken);
        return body?.Embedding ?? Array.Empty<float>();
    }

    public async Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<EmbeddingItemInput> items,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var itemList = items.Select(i => new BatchItemDto
        {
            Type = i.Type,
            Text = i.Text,
            Title = i.Title,
            Path = i.Path,
            VideoPath = i.VideoPath,
            ThumbnailPath = i.ThumbnailPath,
            StartMs = i.StartMs,
            EndMs = i.EndMs
        }).ToList();

        if (itemList.Count == 0) return Array.Empty<float[]>();

        var payload = new BatchRequestDto
        {
            Items = itemList,
            TruncateDim = truncateDim
        };

        var content = JsonContent.Create(payload, SidecarJsonContext.Default.BatchRequestDto);
        var res = await _httpClient.PostAsync($"{_baseUrl}/embed/batch", content, cancellationToken);
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync(SidecarJsonContext.Default.BatchResponseDto, cancellationToken);
        return body?.Embeddings ?? Array.Empty<float[]>();
    }

    public async Task<PdfPageExtractResultDto> ExtractPdfPagesAsync(
        string pdfPath,
        string outputDir,
        CancellationToken cancellationToken = default)
    {
        var payload = new PdfExtractRequestDto { PdfPath = pdfPath, OutputDir = outputDir };
        var content = JsonContent.Create(payload, SidecarJsonContext.Default.PdfExtractRequestDto);
        var res = await _httpClient.PostAsync($"{_baseUrl}/media/extract-pdf-pages", content, cancellationToken);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync(SidecarJsonContext.Default.PdfPageExtractResultDto, cancellationToken);
        return body ?? new PdfPageExtractResultDto();
    }

    public async Task<VideoSegmentExtractResultDto> ExtractVideoSegmentsAsync(
        string videoPath,
        string outputDir,
        int segmentDurationSec = 15,
        int overlapSec = 3,
        CancellationToken cancellationToken = default)
    {
        var payload = new VideoSegmentRequestDto
        {
            VideoPath = videoPath,
            OutputDir = outputDir,
            SegmentDurationSec = segmentDurationSec,
            OverlapSec = overlapSec
        };
        var content = JsonContent.Create(payload, SidecarJsonContext.Default.VideoSegmentRequestDto);
        var res = await _httpClient.PostAsync($"{_baseUrl}/media/extract-video-segments", content, cancellationToken);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync(SidecarJsonContext.Default.VideoSegmentExtractResultDto, cancellationToken);
        return body ?? new VideoSegmentExtractResultDto();
    }
}

public class QueryRequestDto
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;
    [JsonPropertyName("modality")]
    public string Modality { get; set; } = "text";
    [JsonPropertyName("truncate_dim")]
    public int TruncateDim { get; set; } = 768;
}

public class QueryResponseDto
{
    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; } = Array.Empty<float>();
    [JsonPropertyName("dimension")]
    public int Dimension { get; set; }
}

public class BatchItemDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "text";
    [JsonPropertyName("text")]
    public string? Text { get; set; }
    [JsonPropertyName("title")]
    public string? Title { get; set; }
    [JsonPropertyName("path")]
    public string? Path { get; set; }
    [JsonPropertyName("video_path")]
    public string? VideoPath { get; set; }
    [JsonPropertyName("thumbnail_path")]
    public string? ThumbnailPath { get; set; }
    [JsonPropertyName("start_ms")]
    public int? StartMs { get; set; }
    [JsonPropertyName("end_ms")]
    public int? EndMs { get; set; }
}

public class BatchRequestDto
{
    [JsonPropertyName("items")]
    public List<BatchItemDto> Items { get; set; } = new();
    [JsonPropertyName("truncate_dim")]
    public int TruncateDim { get; set; } = 768;
}

public class BatchResponseDto
{
    [JsonPropertyName("embeddings")]
    public float[][] Embeddings { get; set; } = Array.Empty<float[]>();
    [JsonPropertyName("count")]
    public int Count { get; set; }
    [JsonPropertyName("dimension")]
    public int Dimension { get; set; }
}

public class PdfExtractRequestDto
{
    [JsonPropertyName("pdf_path")]
    public string PdfPath { get; set; } = string.Empty;
    [JsonPropertyName("output_dir")]
    public string OutputDir { get; set; } = string.Empty;
}

public class PdfPageExtractResultDto
{
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
    [JsonPropertyName("pages")]
    public List<PdfPageItemDto> Pages { get; set; } = new();
}

public class PdfPageItemDto
{
    [JsonPropertyName("page_number")]
    public int PageNumber { get; set; }
    [JsonPropertyName("image_path")]
    public string ImagePath { get; set; } = string.Empty;
    [JsonPropertyName("text_content")]
    public string TextContent { get; set; } = string.Empty;
}

public class VideoSegmentRequestDto
{
    [JsonPropertyName("video_path")]
    public string VideoPath { get; set; } = string.Empty;
    [JsonPropertyName("output_dir")]
    public string OutputDir { get; set; } = string.Empty;
    [JsonPropertyName("segment_duration_sec")]
    public int SegmentDurationSec { get; set; } = 15;
    [JsonPropertyName("overlap_sec")]
    public int OverlapSec { get; set; } = 3;
}

public class VideoSegmentExtractResultDto
{
    [JsonPropertyName("total_segments")]
    public int TotalSegments { get; set; }
    [JsonPropertyName("segments")]
    public List<VideoSegmentItemDto> Segments { get; set; } = new();
}

public class VideoSegmentItemDto
{
    [JsonPropertyName("segment_index")]
    public int SegmentIndex { get; set; }
    [JsonPropertyName("start_ms")]
    public int StartMs { get; set; }
    [JsonPropertyName("end_ms")]
    public int EndMs { get; set; }
    [JsonPropertyName("thumbnail_path")]
    public string? ThumbnailPath { get; set; }
    [JsonPropertyName("video_path")]
    public string VideoPath { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(QueryRequestDto))]
[JsonSerializable(typeof(QueryResponseDto))]
[JsonSerializable(typeof(BatchRequestDto))]
[JsonSerializable(typeof(BatchResponseDto))]
[JsonSerializable(typeof(PdfExtractRequestDto))]
[JsonSerializable(typeof(PdfPageExtractResultDto))]
[JsonSerializable(typeof(VideoSegmentRequestDto))]
[JsonSerializable(typeof(VideoSegmentExtractResultDto))]
internal partial class SidecarJsonContext : JsonSerializerContext
{
}
