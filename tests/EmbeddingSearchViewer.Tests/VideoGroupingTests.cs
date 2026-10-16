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

/// <summary>Video scenes are merged into one result per video; the scenes of a video can be listed and searched.</summary>
public sealed class VideoGroupingTests : IDisposable
{
    /// <summary>Every query is the vector [1, 0, ...]; a chunk with offAxis x has the similarity 1 / sqrt(1 + x²).</summary>
    private sealed class AxisEmbedding : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default)
        {
            var v = new float[truncateDim];
            v[0] = 1f;
            return Task.FromResult(v);
        }

        public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<float[]>>(items.Select(_ => new float[truncateDim]).ToList());
    }

    private sealed class Repository : IMediaRepository
    {
        public List<MediaAsset> Assets { get; } = new();
        public List<MediaChunk> Chunks { get; } = new();
        private int _salt;

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult(Assets.FirstOrDefault(a => a.FilePath == filePath));
        public Task<MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.FromResult(Assets.FirstOrDefault(a => a.Id == assetId));
        public Task<IReadOnlyList<MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MediaAsset>>(Assets);
        public Task SaveAssetWithChunksAsync(MediaAsset asset, IEnumerable<MediaChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<MediaChunk>> GetAllChunksWithEmbeddingsAsync(MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MediaChunk>>(Chunks);
        public Task<IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<(Guid ChunkId, float FtsRank)>>(Array.Empty<(Guid, float)>());

        private float[] Vector(float offAxis)
        {
            var v = new float[768];
            v[0] = 1f;
            v[1] = offAxis;
            // Distinct tiny values so that no two chunks are "identical embeddings" (those are merged separately).
            v[2 + (_salt++ % 700)] = 1e-4f * (_salt + 1);
            return v;
        }

        public MediaAsset Image(string name, float offAxis)
        {
            var asset = new MediaAsset { FilePath = @"C:\m\" + name, FileName = name, MediaType = MediaType.Image, LastModifiedUtc = DateTime.UtcNow };
            Assets.Add(asset);
            Chunks.Add(new MediaChunk { AssetId = asset.Id, Modality = ModalityType.Image, Embedding = Vector(offAxis), PreviewImagePath = asset.FilePath });
            return asset;
        }

        /// <summary>A video with one scene (12 s apart) per entry; the entry is how far off the query axis that scene is.</summary>
        public MediaAsset Video(string name, params float[] offAxes)
        {
            var asset = new MediaAsset { FilePath = @"C:\m\" + name, FileName = name, MediaType = MediaType.Video, LastModifiedUtc = DateTime.UtcNow, DurationOrPages = offAxes.Length * 12000 };
            Assets.Add(asset);
            for (int i = 0; i < offAxes.Length; i++)
            {
                Chunks.Add(new MediaChunk
                {
                    AssetId = asset.Id, Modality = ModalityType.VideoSegment, ChunkIndex = i,
                    StartTimeMs = i * 12000, EndTimeMs = i * 12000 + 15000, Embedding = Vector(offAxes[i]),
                    PreviewImagePath = $@"C:\m\{name}_{i}.jpg", SnippetText = $"{name} (Clip {i + 1})"
                });
            }
            return asset;
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_grp_" + Guid.NewGuid().ToString("N"));

    public VideoGroupingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static HybridSearchEngine Engine(Repository repo) => new(repo, new AxisEmbedding());

    private static SearchQuery Query(bool group, int topK = 50, Guid? asset = null) => new()
    {
        QueryText = "x", EnableFts = false, MinSimilarity = 0f, TopK = topK, GroupVideoSegments = group, AssetIdFilter = asset
    };

    // ---- engine ----

    [Fact]
    public async Task Grouped_ShowsEachVideoOnce_WithItsBestSceneAsTheRepresentative()
    {
        var repo = new Repository();
        var a = repo.Video("a.mp4", 0.9f, 0.1f, 0.5f);   // best scene: #1 (off 0.1)
        var b = repo.Video("b.mp4", 0.3f);
        repo.Image("c.png", 0.7f);

        var results = await Engine(repo).SearchAsync(Query(group: true));

        Assert.Equal(new[] { "a.mp4", "b.mp4", "c.png" }, results.Select(r => r.Asset.FileName));
        var top = results[0];
        Assert.Equal(1, top.Chunk.ChunkIndex);
        Assert.Equal(new[] { 1, 2, 0 }, top.Scenes.Select(s => s.Chunk.ChunkIndex)); // best first, itself included
        Assert.Same(top, top.Scenes[0]);
        Assert.Equal("一致した場面 3 件", top.GroupText);
        Assert.Empty(results[1].GroupText);          // a single matching scene: no count to show
        Assert.Single(results[1].Scenes);
        Assert.Empty(results[2].Scenes);             // images are not grouped
        Assert.False(results[2].IsVideoGroup);
    }

    [Fact]
    public async Task NotGrouped_KeepsOneRowPerScene_AsBefore()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.9f, 0.1f, 0.5f);
        repo.Image("c.png", 0.7f);

        var results = await Engine(repo).SearchAsync(Query(group: false));

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Empty(r.Scenes));
    }

    [Fact]
    public async Task AGroupSitsAtTheRankOfItsBestScene_NotOfItsOtherScenes()
    {
        var repo = new Repository();
        repo.Image("img.png", 0.2f);                      // better than the video's best scene
        repo.Video("a.mp4", 0.5f, 0.6f, 0.7f, 0.8f);      // many scenes, none better than the image

        var results = await Engine(repo).SearchAsync(Query(group: true));

        Assert.Equal(new[] { "img.png", "a.mp4" }, results.Select(r => r.Asset.FileName));
    }

    [Fact]
    public async Task OneLongVideo_NoLongerCrowdsOutTheOtherVideos()
    {
        var repo = new Repository();
        repo.Video("long.mp4", Enumerable.Range(0, 120).Select(i => 0.01f + i * 0.001f).ToArray()); // 120 scenes, all very close
        for (int i = 0; i < 10; i++) repo.Video($"other{i}.mp4", 1.0f + i * 0.01f);

        var plain = await Engine(repo).SearchAsync(Query(group: false, topK: 50));
        var grouped = await Engine(repo).SearchAsync(Query(group: true, topK: 50));

        Assert.All(plain, r => Assert.Equal("long.mp4", r.Asset.FileName)); // the old list: nothing but the long video
        Assert.Equal(11, grouped.Count);
        Assert.Equal("long.mp4", grouped[0].Asset.FileName);
        Assert.Equal(120, grouped[0].Scenes.Count);
    }

    [Fact]
    public async Task TheCutAfterGrouping_KeepsEverySceneOfTheVideosThatRemain()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.1f, 0.2f, 0.3f);
        repo.Video("b.mp4", 0.4f);

        var results = await Engine(repo).SearchAsync(Query(group: true, topK: 1));

        var only = Assert.Single(results);
        Assert.Equal(3, only.Scenes.Count);
    }

    [Fact]
    public async Task AssetFilter_SearchesOnlyThatVideo()
    {
        var repo = new Repository();
        var target = repo.Video("a.mp4", 0.6f, 0.2f, 0.4f);
        repo.Video("b.mp4", 0.0f, 0.01f);   // far better matches, but another video
        repo.Image("c.png", 0.0f);

        var results = await Engine(repo).SearchAsync(Query(group: false, asset: target.Id));

        Assert.All(results, r => Assert.Equal(target.Id, r.Asset.Id));
        Assert.Equal(new[] { 1, 2, 0 }, results.Select(r => r.Chunk.ChunkIndex)); // best scene first
    }

    [Fact]
    public void PassesFilters_HonoursTheAssetFilter()
    {
        var a = new MediaAsset { MediaType = MediaType.Video };
        var b = new MediaAsset { MediaType = MediaType.Video };

        Assert.True(HybridSearchEngine.PassesFilters(new SearchQuery { AssetIdFilter = a.Id }, a));
        Assert.False(HybridSearchEngine.PassesFilters(new SearchQuery { AssetIdFilter = a.Id }, b));
        Assert.True(HybridSearchEngine.PassesFilters(new SearchQuery(), b));
    }

    // ---- view model ----

    private SearchViewModel NewViewModel(Repository repo) => new(Engine(repo), new SearchHistoryStore(Path.Combine(_dir, "history.json")));

    private static async Task SearchAsync(SearchViewModel vm, string text = "x")
    {
        vm.QueryText = text;
        await vm.SearchCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task ViewModel_GroupsByDefault_AndListsTheScenesOfTheSelectedVideo()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.9f, 0.1f, 0.5f);
        repo.Image("c.png", 0.7f);
        var vm = NewViewModel(repo);
        Assert.True(vm.GroupVideos);

        await SearchAsync(vm);

        Assert.Equal(new[] { "a.mp4", "c.png" }, vm.SearchResults.Select(r => r.Asset.FileName));
        Assert.True(vm.IsVideoSelected);
        Assert.Equal(new[] { 1, 2, 0 }, vm.Scenes.Select(s => s.Chunk.ChunkIndex));
        Assert.Equal("一致した場面 (3 件)", vm.ScenesHeaderText);
        Assert.Equal(1, vm.SelectedScene!.Chunk.ChunkIndex); // the scene that stands for the video

        vm.SelectedResult = vm.SearchResults[1]; // the image
        Assert.False(vm.IsVideoSelected);
        Assert.Empty(vm.Scenes);
        Assert.Equal(string.Empty, vm.ScenesHeaderText);
    }

    [Fact]
    public async Task ViewModel_ScenesCanBeOrderedByTheirPlaceInTheVideo()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.9f, 0.1f, 0.5f);
        var vm = NewViewModel(repo);
        await SearchAsync(vm);

        vm.SceneSortIndex = 1;
        Assert.Equal(new[] { 0, 1, 2 }, vm.Scenes.Select(s => s.Chunk.ChunkIndex));
        Assert.Equal(1, vm.SelectedScene!.Chunk.ChunkIndex); // the selection stays on the same scene

        vm.SceneSortIndex = 0;
        Assert.Equal(new[] { 1, 2, 0 }, vm.Scenes.Select(s => s.Chunk.ChunkIndex));
    }

    [Fact]
    public async Task ViewModel_WithoutGrouping_StillListsTheMatchingScenesOfThatVideo()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.9f, 0.1f, 0.5f);
        repo.Video("b.mp4", 0.2f);
        var vm = NewViewModel(repo);
        vm.GroupVideos = false;

        await SearchAsync(vm);

        Assert.Equal(4, vm.SearchResults.Count);
        var firstOfA = vm.SearchResults.First(r => r.Asset.FileName == "a.mp4");
        vm.SelectedResult = firstOfA;
        Assert.Equal(3, vm.Scenes.Count);
        Assert.All(vm.Scenes, s => Assert.Equal("a.mp4", s.Asset.FileName));
    }

    [Fact]
    public async Task ViewModel_TogglingTheSwitch_ShowsTheSameSearchInTheOtherForm()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.9f, 0.1f, 0.5f);
        repo.Video("b.mp4", 0.2f);
        var vm = NewViewModel(repo);
        await SearchAsync(vm);
        Assert.Equal(2, vm.SearchResults.Count);

        vm.GroupVideos = false;
        for (int i = 0; i < 100 && (vm.IsSearching || vm.SearchResults.Count != 4); i++) await Task.Delay(20);
        Assert.Equal(4, vm.SearchResults.Count);

        vm.GroupVideos = true;
        for (int i = 0; i < 100 && (vm.IsSearching || vm.SearchResults.Count != 2); i++) await Task.Delay(20);
        Assert.Equal(2, vm.SearchResults.Count);
    }

    [Fact]
    public async Task ViewModel_TogglingTheSwitchWithoutASearch_DoesNotSearch()
    {
        var vm = NewViewModel(new Repository());

        vm.GroupVideos = false;
        await Task.Delay(100);

        Assert.Empty(vm.SearchResults);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task ViewModel_SearchInsideTheVideo_OnlyShowsThatVideosScenes_AndCanGoBack()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.9f, 0.1f, 0.5f, 0.3f);
        repo.Video("b.mp4", 0.0f, 0.01f);   // matches better, but is another video
        var vm = NewViewModel(repo);
        await SearchAsync(vm);
        vm.SelectedResult = vm.SearchResults.First(r => r.Asset.FileName == "a.mp4");

        vm.SceneQueryText = "花火";
        await vm.SearchInVideoCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Scenes.Count);
        Assert.All(vm.Scenes, s => Assert.Equal("a.mp4", s.Asset.FileName));
        Assert.Contains("花火", vm.ScenesHeaderText);
        Assert.Equal(new[] { 1, 3, 2, 0 }, vm.Scenes.Select(s => s.Chunk.ChunkIndex));

        vm.ResetScenesCommand.Execute(null);
        Assert.Equal(string.Empty, vm.SceneQueryText);
        Assert.Equal("一致した場面 (4 件)", vm.ScenesHeaderText);

        vm.SceneQueryText = "花火";
        await vm.SearchInVideoCommand.ExecuteAsync(null);
        vm.SceneQueryText = "   "; // an empty text means: back to the scenes of the main search
        await vm.SearchInVideoCommand.ExecuteAsync(null);
        Assert.Equal("一致した場面 (4 件)", vm.ScenesHeaderText);
    }

    [Fact]
    public async Task ViewModel_SelectingAnotherVideo_ClearsTheSearchInsideTheFirst()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.1f, 0.2f);
        repo.Video("b.mp4", 0.3f);
        var vm = NewViewModel(repo);
        await SearchAsync(vm);

        vm.SceneQueryText = "x";
        await vm.SearchInVideoCommand.ExecuteAsync(null);
        vm.SelectedResult = vm.SearchResults.First(r => r.Asset.FileName == "b.mp4");

        Assert.Equal(string.Empty, vm.SceneQueryText);
        Assert.Equal("b.mp4", Assert.Single(vm.Scenes).Asset.FileName);
    }

    [Fact]
    public async Task ViewModel_SearchInsideTheVideo_DoesNothingWhenNoVideoIsSelected()
    {
        var repo = new Repository();
        repo.Image("c.png", 0.1f);
        var vm = NewViewModel(repo);
        await SearchAsync(vm);

        vm.SceneQueryText = "x";
        await vm.SearchInVideoCommand.ExecuteAsync(null);

        Assert.Empty(vm.Scenes);
        Assert.False(vm.IsVideoSelected);
    }

    [Fact]
    public async Task ViewModel_ClearingTheSearch_ClearsTheScenes()
    {
        var repo = new Repository();
        repo.Video("a.mp4", 0.1f, 0.2f);
        var vm = NewViewModel(repo);
        await SearchAsync(vm);
        Assert.NotEmpty(vm.Scenes);

        vm.ResetForProfileChange();

        Assert.Empty(vm.Scenes);
        Assert.False(vm.IsVideoSelected);
    }
}
