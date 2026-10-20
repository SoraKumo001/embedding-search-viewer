using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Data;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>
/// The <c>ffmpeg.active</c> marker is the signal that a decode died. A decode that returns clears it, but a marker a
/// frame source left behind because its decode crashed (the FFmpeg worker child) has to survive the run, so the next
/// run of that video goes straight to Media Foundation.
/// </summary>
public sealed class VideoDecoderMarkerTests : IDisposable
{
    private const string MarkerName = "ffmpeg.active";

    private sealed class EmbeddingService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult(new float[truncateDim]);

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
            IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items.Select(_ => new float[] { 1f, 2f, 3f, 4f }).ToList());
    }

    /// <summary>Decodes one segment and, like a dying decoder, leaves the marker content the test asks for.</summary>
    private sealed class MarkerWritingExtractor : IVideoSegmentExtractor
    {
        private readonly string? _markerContent;

        public MarkerWritingExtractor(string? markerContent) => _markerContent = markerContent;

        public Task<IReadOnlyList<VideoSegmentContent>> ExtractAsync(
            string videoPath, string outputDirectory, int segmentSeconds, int overlapSeconds, CancellationToken cancellationToken = default)
        {
            if (_markerContent != null)
            {
                File.WriteAllText(Path.Combine(outputDirectory, MarkerName), _markerContent);
            }

            string frame = Path.Combine(outputDirectory, "frame.jpg");
            File.WriteAllBytes(frame, new byte[] { 0xFF, 0xD8, 0xFF });
            IReadOnlyList<VideoSegmentContent> segments = new[] { new VideoSegmentContent(0, 0, 1000, frame) };
            return Task.FromResult(segments);
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_marker_" + Guid.NewGuid().ToString("N"));

    public VideoDecoderMarkerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Video()
    {
        string path = Path.Combine(_dir, "movie.mp4");
        File.WriteAllText(path, "dummy");
        return path;
    }

    private async Task<string> IngestAsync(IVideoSegmentExtractor extractor)
    {
        string video = Video();
        await new VideoMediaIngestor(new EmbeddingService(), extractor).IngestAsync(video, Path.Combine(_dir, "cache"));
        return Directory.EnumerateFiles(Path.Combine(_dir, "cache"), MarkerName, SearchOption.AllDirectories).FirstOrDefault()
            ?? string.Empty;
    }

    [Fact]
    public async Task AMarkerOfACrashedDecode_SurvivesTheRun()
    {
        string marker = await IngestAsync(new MarkerWritingExtractor("pid=1 crashed=2026-10-20T00:00:00 exitCode=3"));

        Assert.NotEqual(string.Empty, marker);
        Assert.Contains("crashed=", File.ReadAllText(marker));
    }

    [Fact]
    public async Task TheMarkerOfADecodeThatReturned_IsCleared()
    {
        // The ingestor writes this marker itself before the decode; a decode that returns (no crash) consumes it.
        string marker = await IngestAsync(new MarkerWritingExtractor(null));

        Assert.Equal(string.Empty, marker);
    }
}
