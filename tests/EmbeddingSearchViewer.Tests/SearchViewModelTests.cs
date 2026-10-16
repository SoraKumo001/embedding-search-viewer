using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;
using EmbeddingSearchViewer.Core.Services;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer_App.ViewModels;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class SearchViewModelTests : IDisposable
{
    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public bool Fail { get; set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("engine down");
            var v = new float[truncateDim];
            v[0] = 1f;
            return Task.FromResult(v);
        }

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items.Select(_ => new float[truncateDim]).ToList());
    }

    private sealed class FakeRepository : IMediaRepository
    {
        public List<MediaAsset> Assets { get; } = new();
        public List<MediaChunk> Chunks { get; } = new();
        public List<(Guid ChunkId, float FtsRank)> Fts { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(Assets.FirstOrDefault(a => a.FilePath == filePath));
        public Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.FromResult(Assets.FirstOrDefault(a => a.Id == assetId));
        public Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MediaAsset>>(Assets);
        public Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MediaChunk>>(Chunks);
        public Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<(Guid ChunkId, float FtsRank)>>(Fts);

        /// <summary>Adds an asset whose similarity to the fake query vector ([1,0,...]) falls as <paramref name="offAxis"/> grows.</summary>
        public MediaAsset Add(string path, float offAxis, MediaType type = MediaType.Image, long size = 1024, DateTime? modifiedUtc = null)
        {
            var asset = new MediaAsset
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                MediaType = type,
                FileSizeBytes = size,
                LastModifiedUtc = modifiedUtc ?? new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc)
            };
            var emb = new float[768];
            emb[0] = 1f;
            emb[1] = offAxis;
            Assets.Add(asset);
            Chunks.Add(new MediaChunk { AssetId = asset.Id, Modality = ModalityType.Image, Embedding = emb, SnippetText = asset.FileName });
            return asset;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_vm_" + Guid.NewGuid().ToString("N"));
    private readonly FakeRepository _repo = new();
    private readonly FakeEmbeddingService _embedding = new();
    private readonly SearchHistoryStore _history;
    private readonly SearchViewModel _vm;

    public SearchViewModelTests()
    {
        _history = new SearchHistoryStore(Path.Combine(_dir, "history.json"));
        _vm = new SearchViewModel(new HybridSearchEngine(_repo, _embedding), _history) { QueryText = "anything", EnableFts = false };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string[] Names(SearchViewModel vm) => vm.SearchResults.Select(r => r.Asset.FileName).ToArray();

    // ---- search flow ----

    [Fact]
    public async Task Search_PopulatesResultsSelectsBestAndClearsEmptyState()
    {
        _repo.Add(@"C:\p\far.png", 1.0f);
        _repo.Add(@"C:\p\near.png", 0.1f);

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "near.png", "far.png" }, Names(_vm));
        Assert.Equal("near.png", _vm.SelectedResult?.Asset.FileName);
        Assert.Equal("検索結果 (2 件)", _vm.ResultsHeaderText);
        Assert.Equal(string.Empty, _vm.EmptyStateText);
        Assert.False(_vm.IsSearching);
    }

    [Fact]
    public async Task Search_WithNoMatches_ShowsNoResultsMessage()
    {
        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(_vm.SearchResults);
        Assert.Null(_vm.SelectedResult);
        Assert.Contains("一致する結果がありません", _vm.EmptyStateText);
    }

    [Fact]
    public async Task Search_WithBlankQueryAndNoImage_DoesNothing()
    {
        _vm.QueryText = "   ";

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Empty(_vm.SearchResults);
        Assert.Contains("検索語を入力", _vm.EmptyStateText);
    }

    [Fact]
    public async Task Search_WhenEngineFails_ReportsErrorAndStopsBusyIndicator()
    {
        _embedding.Fail = true;

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Contains("engine down", _vm.ErrorMessage);
        Assert.False(_vm.IsSearching);
    }

    [Fact]
    public async Task Search_MediaFilterIndexRestrictsResults()
    {
        _repo.Add(@"C:\p\pic.png", 0.1f, MediaType.Image);
        _repo.Add(@"C:\p\doc.pdf", 0.2f, MediaType.Pdf);
        _vm.SelectedMediaTypeIndex = 1; // PDF

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "doc.pdf" }, Names(_vm));
    }

    // ---- history ----

    [Fact]
    public async Task Search_RecordsTextQueriesButNotImageQueries()
    {
        _repo.Add(@"C:\p\a.png", 0.1f);
        _vm.QueryText = "red car";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "red car" }, _vm.History);

        _vm.SetQueryImage(@"C:\q\query.png");
        _vm.QueryText = "ignored while image active";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "red car" }, _vm.History);
    }

    // ---- sorting ----

    [Fact]
    public async Task ChangingSort_ReordersWithoutSearchingAgainAndKeepsSelection()
    {
        _repo.Add(@"C:\p\b.png", 0.1f);
        _repo.Add(@"C:\p\c.png", 0.5f);
        _repo.Add(@"C:\p\a.png", 1.0f);
        await _vm.SearchCommand.ExecuteAsync(null);
        _vm.SelectedResult = _vm.SearchResults.First(r => r.Asset.FileName == "c.png");
        _embedding.Fail = true; // proves no new search happens

        _vm.SortIndex = 2; // file name

        Assert.Equal(new[] { "a.png", "b.png", "c.png" }, Names(_vm));
        Assert.Equal("c.png", _vm.SelectedResult?.Asset.FileName);
        Assert.Null(_vm.ErrorMessage);

        _vm.SortIndex = 1; // similarity
        Assert.Equal(new[] { "b.png", "c.png", "a.png" }, Names(_vm));
    }

    // ---- metadata filters ----

    [Fact]
    public async Task Filters_BySizeInMegabytes()
    {
        const long mb = 1024 * 1024;
        _repo.Add(@"C:\p\small.png", 0.1f, size: 1 * mb);
        _repo.Add(@"C:\p\medium.png", 0.2f, size: 5 * mb);
        _repo.Add(@"C:\p\large.png", 0.3f, size: 50 * mb);

        _vm.FilterMinSizeMb = 2;
        _vm.FilterMaxSizeMb = 10;
        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "medium.png" }, Names(_vm));
    }

    [Fact]
    public async Task Filters_EmptyNumberBoxMeansNoLimit()
    {
        _repo.Add(@"C:\p\a.png", 0.1f, size: 10);
        _repo.Add(@"C:\p\b.png", 0.2f, size: 10L * 1024 * 1024 * 1024);

        _vm.FilterMinSizeMb = double.NaN;
        _vm.FilterMaxSizeMb = double.NaN;
        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(2, _vm.SearchResults.Count);
    }

    [Fact]
    public async Task Filters_ByModifiedDateRange_IncludesBothEndDays()
    {
        var local = new DateTime(2024, 3, 10, 12, 0, 0, DateTimeKind.Local);
        _repo.Add(@"C:\p\before.png", 0.1f, modifiedUtc: local.AddDays(-5).ToUniversalTime());
        _repo.Add(@"C:\p\startday.png", 0.2f, modifiedUtc: local.ToUniversalTime());
        _repo.Add(@"C:\p\endday.png", 0.3f, modifiedUtc: local.AddDays(4).ToUniversalTime());
        _repo.Add(@"C:\p\after.png", 0.4f, modifiedUtc: local.AddDays(5).ToUniversalTime());

        _vm.FilterDateFrom = new DateTimeOffset(local.Date);          // 3/10
        _vm.FilterDateTo = new DateTimeOffset(local.AddDays(4).Date); // 3/14
        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "endday.png", "startday.png" }.OrderBy(x => x), Names(_vm).OrderBy(x => x));
    }

    [Fact]
    public async Task Filters_ByFolderSubstring_IgnoringCase_AndClearFiltersResets()
    {
        _repo.Add(@"C:\Photos\2024\trip.png", 0.1f);
        _repo.Add(@"C:\Work\slides.png", 0.2f);

        _vm.FilterFolder = "photos\\2024";
        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "trip.png" }, Names(_vm));

        _vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(string.Empty, _vm.FilterFolder);
        Assert.True(double.IsNaN(_vm.FilterMinSizeMb));
        Assert.Null(_vm.FilterDateFrom);

        await _vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal(2, _vm.SearchResults.Count);
    }

    [Fact]
    public async Task Filters_AlsoApplyToKeywordOnlyHits()
    {
        var kept = _repo.Add(@"C:\Keep\a.png", 0.1f);
        var dropped = _repo.Add(@"C:\Drop\b.png", 0.1f);
        _repo.Fts.Add((_repo.Chunks.First(c => c.AssetId == dropped.Id).Id, 1f));
        _vm.EnableFts = true;
        _vm.MinSimilarity = 0.99f; // vector hits dropped by threshold; the fallback keeps candidates, FTS adds the rest
        _vm.FilterFolder = "Keep";

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.All(_vm.SearchResults, r => Assert.Equal(kept.Id, r.Asset.Id));
    }

    // ---- identical embeddings ----

    [Fact]
    public async Task IdenticalEmbeddings_AreCollapsedIntoOneResultThatNotesTheOthers()
    {
        _repo.Add(@"C:\p\b.png", 0.1f);
        _repo.Add(@"C:\p\a.png", 0.1f);   // same embedding as b.png
        _repo.Add(@"C:\p\c.png", 0.5f);

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "a.png", "c.png" }, Names(_vm)); // ties resolve by path, so the representative is stable
        var shown = _vm.SearchResults[0];
        Assert.Equal(new[] { @"C:\p\b.png" }, shown.DuplicatePaths);
        Assert.Equal(1, shown.DuplicateCount);
        Assert.Equal("同一画像 他1件", shown.DuplicateText);
        Assert.Equal(string.Empty, _vm.SearchResults[1].DuplicateText);
    }

    [Fact]
    public async Task ManyIdenticalImages_CountOnceTowardsTheResultLimit()
    {
        for (int i = 0; i < 60; i++) _repo.Add($@"C:\blank\{i:D2}.png", 0.9f);
        _repo.Add(@"C:\p\x.png", 0.1f);
        _repo.Add(@"C:\p\y.png", 0.2f);
        _repo.Add(@"C:\p\z.png", 0.3f);

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal(4, _vm.SearchResults.Count);
        Assert.Equal(59, _vm.SearchResults.Single(r => r.DuplicateCount > 0).DuplicateCount);
    }

    [Fact]
    public async Task ChunksOfTheSameFile_AreNotReportedAsDuplicateFiles()
    {
        var asset = _repo.Add(@"C:\p\doc.png", 0.1f);
        _repo.Chunks.Add(new MediaChunk { AssetId = asset.Id, Modality = ModalityType.Image, Embedding = _repo.Chunks[0].Embedding });

        await _vm.SearchCommand.ExecuteAsync(null);

        Assert.Single(_vm.SearchResults);
        Assert.Equal(0, _vm.SearchResults[0].DuplicateCount);
    }

    // ---- misc state ----

    [Fact]
    public async Task Clear_ResetsQueryResultsAndEmptyState()
    {
        _repo.Add(@"C:\p\a.png", 0.1f);
        await _vm.SearchCommand.ExecuteAsync(null);

        _vm.ClearCommand.Execute(null);

        Assert.Equal(string.Empty, _vm.QueryText);
        Assert.Empty(_vm.SearchResults);
        Assert.Null(_vm.SelectedResult);
        Assert.Equal("検索結果 (0 件)", _vm.ResultsHeaderText);
        Assert.Contains("検索語を入力", _vm.EmptyStateText);
    }

    [Theory]
    [InlineData(0f, "制限なし")]
    [InlineData(0.6f, "60% 以上")]
    public void MinSimilarityText_DescribesThreshold(float value, string expectedFragment)
    {
        _vm.MinSimilarity = value;

        Assert.Contains(expectedFragment.Replace("%", ""), _vm.MinSimilarityText.Replace("%", ""));
    }
}

public class SearchFilterTests
{
    private static MediaAsset Asset(long size = 100, DateTime? modified = null, string path = @"C:\a\b.png", MediaType type = MediaType.Image) => new()
    {
        FilePath = path,
        FileName = Path.GetFileName(path),
        FileSizeBytes = size,
        MediaType = type,
        LastModifiedUtc = modified ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void NoFilters_AcceptsEverything()
    {
        var q = new SearchQuery();
        Assert.False(q.HasMetadataFilters);
        Assert.True(HybridSearchEngine.PassesFilters(q, Asset()));
    }

    [Fact]
    public void SizeBounds_AreInclusive()
    {
        var q = new SearchQuery { MinSizeBytes = 100, MaxSizeBytes = 200 };
        Assert.True(HybridSearchEngine.PassesFilters(q, Asset(size: 100)));
        Assert.True(HybridSearchEngine.PassesFilters(q, Asset(size: 200)));
        Assert.False(HybridSearchEngine.PassesFilters(q, Asset(size: 99)));
        Assert.False(HybridSearchEngine.PassesFilters(q, Asset(size: 201)));
    }

    [Fact]
    public void ModifiedRange_IsInclusiveFromAndExclusiveTo()
    {
        var from = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2024, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        var q = new SearchQuery { ModifiedFromUtc = from, ModifiedToUtc = to };

        Assert.True(HybridSearchEngine.PassesFilters(q, Asset(modified: from)));
        Assert.False(HybridSearchEngine.PassesFilters(q, Asset(modified: to)));
        Assert.False(HybridSearchEngine.PassesFilters(q, Asset(modified: from.AddTicks(-1))));
    }

    [Fact]
    public void MediaTypeAndFolder_AreCombinedWithAnd()
    {
        var q = new SearchQuery { MediaTypeFilter = MediaType.Pdf, FolderContains = "reports" };

        Assert.True(HybridSearchEngine.PassesFilters(q, Asset(path: @"D:\Reports\q1.pdf", type: MediaType.Pdf)));
        Assert.False(HybridSearchEngine.PassesFilters(q, Asset(path: @"D:\Reports\q1.png", type: MediaType.Image)));
        Assert.False(HybridSearchEngine.PassesFilters(q, Asset(path: @"D:\Other\q1.pdf", type: MediaType.Pdf)));
    }
}
