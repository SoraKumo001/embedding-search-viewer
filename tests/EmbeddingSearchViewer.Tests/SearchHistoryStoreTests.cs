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

        Assert.Equal(new[] { "RED CAR", "graph slide" }, Texts(list));
        Assert.Equal(Texts(list), Texts(store.Load()));
    }

    private static string?[] Texts(System.Collections.Generic.IEnumerable<SearchHistoryEntry> entries) => entries.Select(e => e.Text).ToArray();

    [Fact]
    public void Add_KeepsImageSearchesAndTheirOptions_ApartFromTextOnes()
    {
        var store = NewStore();
        store.Add("cat");
        store.Add(new SearchHistoryEntry { ImagePath = @"C:\q\cat.png" });
        store.Add(new SearchHistoryEntry { Text = "cat", ImagePath = @"C:\q\cat.png" }); // text + image is its own search
        var list = store.Add(new SearchHistoryEntry { ImagePath = @"C:\Q\CAT.PNG", Options = new SearchHistoryOptions { MinSimilarity = 0.4f, Folder = "pics" } });

        Assert.Equal(3, list.Count);
        Assert.Equal(@"C:\Q\CAT.PNG", list[0].ImagePath); // the same image search again: replaced, not added

        var reloaded = NewStore().Load();
        Assert.Equal(new[] { 0.4f }, reloaded.Where(e => e.Options != null).Select(e => e.Options!.MinSimilarity));
    }

    [Fact]
    public void Pinned_StaysOnTop_SurvivesTheLimitAndIsKeptByAnAddOfTheSameSearch()
    {
        var store = NewStore();
        var first = store.Add("keep me")[0];
        Assert.True(store.SetPinned(first.Id, true));
        for (int i = 0; i < SearchHistoryStore.MaxEntries + 5; i++) store.Add($"q{i}");

        var list = store.Load();
        Assert.Equal(SearchHistoryStore.MaxEntries + 1, list.Count);
        Assert.Equal("keep me", list[0].Text);
        Assert.True(list[0].Pinned);

        store.Add("KEEP ME");
        Assert.True(store.Load()[0].Pinned);
        Assert.Equal(first.Id, store.Load()[0].Id);
    }

    [Fact]
    public void SetPinned_RefusesMoreThanTheLimit_AndUnknownIds()
    {
        var store = NewStore();
        for (int i = 0; i < SearchHistoryStore.MaxPinned + 1; i++) store.Add($"q{i}");
        var all = store.Load();

        foreach (var e in all.Take(SearchHistoryStore.MaxPinned)) Assert.True(store.SetPinned(e.Id, true));
        Assert.False(store.SetPinned(all[^1].Id, true));
        Assert.False(store.SetPinned(Guid.NewGuid(), true));
        Assert.True(store.SetPinned(all[0].Id, false));
        Assert.True(store.SetPinned(all[^1].Id, true));
    }

    [Fact]
    public void Remove_And_Clear_KeepPinnedUnlessAsked()
    {
        var store = NewStore();
        store.Add("a");
        store.Add("b");
        store.Add("c");
        var list = store.Load(); // c, b, a
        store.SetPinned(list[1].Id, true); // b

        store.Remove(list[0].Id); // c
        Assert.Equal(new[] { "b", "a" }, Texts(store.Load()));

        store.Clear();
        Assert.Equal(new[] { "b" }, Texts(store.Load()));

        store.Clear(includePinned: true);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void Load_ReadsTheOlderFormatOfPlainTexts()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "history.json"), "[\"newer\",\"older\"]");

        var store = NewStore();
        Assert.Equal(new[] { "newer", "older" }, Texts(store.Load()));

        store.Add("newest"); // written in the new format from now on
        Assert.Equal(new[] { "newest", "newer", "older" }, Texts(NewStore().Load()));
    }

    [Fact]
    public void Add_KeepsOnlyTheMostRecentEntries()
    {
        var store = NewStore();
        for (int i = 0; i < SearchHistoryStore.MaxEntries + 5; i++) store.Add($"q{i}");

        var list = store.Load();
        Assert.Equal(SearchHistoryStore.MaxEntries, list.Count);
        Assert.Equal($"q{SearchHistoryStore.MaxEntries + 4}", list[0].Text);
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
