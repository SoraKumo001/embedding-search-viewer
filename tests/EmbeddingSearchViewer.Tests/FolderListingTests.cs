using System.Linq;
using EmbeddingSearchViewer.Infrastructure.Library;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public class FolderListingTests
{
    private static readonly string[] Files =
    {
        @"D:\work\manga\a\1.png", @"D:\work\manga\a\2.png", @"D:\work\manga\a\sub\3.png",
        @"D:\work\manga\ab\1.png",                 // not inside "a"
        @"D:\movie\x.mp4", @"d:\MOVIE\y.mp4",
    };

    [Fact]
    public void CountFiles_CountsFilesBelowEachFolder_IgnoringCaseAndSimilarNames()
    {
        var counts = FolderListing.CountFiles(new[] { @"D:\work\manga\a", @"D:\movie\", @"D:\work", @"D:\empty" }, Files);

        Assert.Equal(3, counts[@"D:\work\manga\a"]);
        Assert.Equal(2, counts[@"D:\movie\"]);
        Assert.Equal(4, counts[@"D:\work"]); // a folder inside another one counts for both
        Assert.Equal(0, counts[@"D:\empty"]);
    }

    [Fact]
    public void Order_ByAddedNameAndCount()
    {
        var folders = new[] { @"D:\b", @"D:\a", @"D:\c" };
        var counts = new System.Collections.Generic.Dictionary<string, int> { [@"D:\b"] = 5, [@"D:\a"] = 9, [@"D:\c"] = 5 };

        Assert.Equal(folders, FolderListing.Order(folders, counts, FolderSortMode.Added));
        Assert.Equal(new[] { @"D:\a", @"D:\b", @"D:\c" }, FolderListing.Order(folders, counts, FolderSortMode.Name));
        Assert.Equal(new[] { @"D:\a", @"D:\b", @"D:\c" }, FolderListing.Order(folders, counts, FolderSortMode.Count)); // ties by name
        Assert.Equal(new[] { @"D:\b" }, FolderListing.Order(new[] { @"D:\b" }, counts, FolderSortMode.Count).ToArray());
    }

    [Fact]
    public void Order_ByCount_PutsFoldersWithoutACountLast()
    {
        var counts = new System.Collections.Generic.Dictionary<string, int> { [@"D:\a"] = 0 };

        Assert.Equal(new[] { @"D:\a", @"D:\z" }, FolderListing.Order(new[] { @"D:\z", @"D:\a" }, counts, FolderSortMode.Count));
    }
}
