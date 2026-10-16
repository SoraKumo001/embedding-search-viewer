using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public sealed class SearchHistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_hist_" + Guid.NewGuid().ToString("N"));

    private SearchHistoryStore NewStore() => new(Path.Combine(_dir, "history.json"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Add_PutsNewestFirstAndMovesDuplicatesToTop()
    {
        var store = NewStore();
        store.Add("red car");
        store.Add("graph slide");
        var list = store.Add("RED CAR ");

        Assert.Equal(new[] { "RED CAR", "graph slide" }, list);
        Assert.Equal(list, store.Load());
    }

    [Fact]
    public void Add_KeepsOnlyTheMostRecentEntries()
    {
        var store = NewStore();
        for (int i = 0; i < SearchHistoryStore.MaxEntries + 5; i++) store.Add($"q{i}");

        var list = store.Load();
        Assert.Equal(SearchHistoryStore.MaxEntries, list.Count);
        Assert.Equal($"q{SearchHistoryStore.MaxEntries + 4}", list[0]);
    }

    [Fact]
    public void Add_IgnoresBlankQueries_AndClearEmptiesHistory()
    {
        var store = NewStore();
        Assert.Empty(store.Add("   "));

        store.Add("something");
        store.Clear();
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Load_ReturnsEmptyForCorruptFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "history.json"), "[ broken");

        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public async Task UnavailableEmbeddingService_ReportsReasonInsteadOfCrashing()
    {
        var reason = UnavailableEmbeddingService.DescribeFailure(new FileNotFoundException("ONNX model not found at X"));
        var service = new UnavailableEmbeddingService(reason);

        Assert.False(await service.IsReadyAsync());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateQueryEmbeddingAsync("q"));
        Assert.Contains("models", ex.Message);
    }
}
