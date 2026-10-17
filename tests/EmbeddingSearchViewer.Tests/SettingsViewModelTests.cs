using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Localization;
using EmbeddingSearchViewer.Core.Services;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer_App.ViewModels;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_setvm_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class ReadyEmbeddingService : IEmbeddingService
    {
        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<float[]> GenerateQueryEmbeddingAsync(string query, string modality = "text", int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult(new float[truncateDim]);
        public Task<System.Collections.Generic.IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(System.Collections.Generic.IEnumerable<EmbeddingItemInput> items, int truncateDim = 768, CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<float[]>>(Array.Empty<float[]>());
    }

    private sealed class DummyRepo : IMediaRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<EmbeddingSearchViewer.Core.Models.MediaAsset?> GetAssetByPathAsync(string filePath, CancellationToken cancellationToken = default) => Task.FromResult<EmbeddingSearchViewer.Core.Models.MediaAsset?>(null);
        public Task<EmbeddingSearchViewer.Core.Models.MediaAsset?> GetAssetByIdAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.FromResult<EmbeddingSearchViewer.Core.Models.MediaAsset?>(null);
        public Task<System.Collections.Generic.IReadOnlyList<EmbeddingSearchViewer.Core.Models.MediaAsset>> GetAllAssetsAsync(CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<EmbeddingSearchViewer.Core.Models.MediaAsset>>(Array.Empty<EmbeddingSearchViewer.Core.Models.MediaAsset>());
        public Task SaveAssetWithChunksAsync(EmbeddingSearchViewer.Core.Models.MediaAsset asset, System.Collections.Generic.IEnumerable<EmbeddingSearchViewer.Core.Models.MediaChunk> chunks, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAssetAsync(Guid assetId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<System.Collections.Generic.IReadOnlyList<EmbeddingSearchViewer.Core.Models.MediaChunk>> GetAllChunksWithEmbeddingsAsync(EmbeddingSearchViewer.Core.Models.MediaType? mediaTypeFilter = null, CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<EmbeddingSearchViewer.Core.Models.MediaChunk>>(Array.Empty<EmbeddingSearchViewer.Core.Models.MediaChunk>());
        public Task<System.Collections.Generic.IReadOnlyList<(Guid ChunkId, float FtsRank)>> SearchFtsAsync(string keyword, int limit = 50, CancellationToken cancellationToken = default) => Task.FromResult<System.Collections.Generic.IReadOnlyList<(Guid ChunkId, float FtsRank)>>(Array.Empty<(Guid ChunkId, float FtsRank)>());
    }

    [Fact]
    public void Constructor_InitializesLanguageFromStore()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "app_settings.json"));
        var settings = store.Load();
        settings.Language = Loc.English;
        store.Save(settings);

        var embedding = new ReadyEmbeddingService();
        var searchVm = new SearchViewModel(new HybridSearchEngine(new DummyRepo(), embedding), new SearchHistoryStore(Path.Combine(_dir, "hist.json")));

        var vm = new SettingsViewModel(embedding, searchVm, store, startupLanguage: Loc.English);

        Assert.Equal(2, vm.LanguageIndex); // 0: Auto, 1: Japanese, 2: English
        Assert.False(vm.RestartRequired);
    }

    [Fact]
    public void ChangingLanguage_SetsRestartRequiredWhenDifferentFromStartup()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "app_settings.json"));
        var embedding = new ReadyEmbeddingService();
        var searchVm = new SearchViewModel(new HybridSearchEngine(new DummyRepo(), embedding), new SearchHistoryStore(Path.Combine(_dir, "hist.json")));

        var vm = new SettingsViewModel(embedding, searchVm, store, startupLanguage: Loc.Japanese);

        // Switch to English (index 2)
        vm.LanguageIndex = 2;
        Assert.True(vm.RestartRequired);
        Assert.Equal(Loc.English, store.Load().Language);

        // Switch back to Japanese (index 1)
        vm.LanguageIndex = 1;
        Assert.False(vm.RestartRequired);
    }

    [Fact]
    public async Task CheckHealthAsync_UpdatesReadyStatus()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "app_settings.json"));
        var embedding = new ReadyEmbeddingService();
        var searchVm = new SearchViewModel(new HybridSearchEngine(new DummyRepo(), embedding), new SearchHistoryStore(Path.Combine(_dir, "hist.json")));

        var vm = new SettingsViewModel(embedding, searchVm, store, startupLanguage: Loc.Auto);
        Assert.False(vm.IsEngineReady);

        await vm.CheckHealthAsync();

        Assert.True(vm.IsEngineReady);
    }
}
