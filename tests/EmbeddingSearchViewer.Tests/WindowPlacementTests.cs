using System;
using System.IO;
using EmbeddingSearchViewer.Infrastructure.Library;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

/// <summary>A saved window placement is only used again when it still makes sense on the screens that exist now.</summary>
public sealed class WindowPlacementTests : IDisposable
{
    private static readonly ScreenRect Main = new(0, 0, 1920, 1040);          // 1080p work area (taskbar excluded)
    private static readonly ScreenRect Left = new(-1920, 0, 1920, 1040);      // a second monitor to the left
    private static readonly ScreenRect Right = new(1920, 0, 2560, 1400);      // and one to the right

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "esv_win_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static WindowPlacement Place(int x, int y, int w = 1280, int h = 840, bool max = false) => new(x, y, w, h, max);

    private static void AssertUsed(WindowPlacement saved, params ScreenRect[] screens)
        => Assert.Equal(saved, WindowPlacementValidator.Validate(saved, screens));

    private static void AssertRejected(WindowPlacement? saved, params ScreenRect[] screens)
        => Assert.Null(WindowPlacementValidator.Validate(saved, screens));

    // ---- accepted ----

    [Fact]
    public void ANormalWindow_IsUsed()
    {
        AssertUsed(Place(100, 80), Main);
        AssertUsed(Place(0, 0), Main);
        AssertUsed(Place(640, 200, 1280, 840, max: true), Main); // the maximize flag does not change the verdict
    }

    [Fact]
    public void AWindowOnASecondMonitor_IsUsed_EvenWithNegativeCoordinates()
    {
        AssertUsed(Place(-1800, 100), Left, Main);
        AssertUsed(Place(2000, 50, 1600, 1000), Main, Right);
    }

    [Fact]
    public void AWindowSpanningTwoMonitors_IsUsed()
    {
        AssertUsed(Place(1000, 100, 1800, 900), Main, Right);
    }

    [Fact]
    public void AWindowThatFillsAllScreensTogether_IsUsed()
    {
        // 1920 + 2560 wide: a window spread over both, a few pixels larger than the work areas (window borders).
        AssertUsed(Place(-8, -8, 4480 + 16, 1400 + 16), Main, Right);
    }

    [Fact]
    public void AWindowPartlyOffTheScreen_IsUsed_AsLongAsItsTitleBarCanBeGrabbed()
    {
        AssertUsed(Place(-1000, 100), Main);      // 280 px of the 1280-wide window are visible
        AssertUsed(Place(1700, 100), Main);       // 220 px visible on the right
    }

    // ---- rejected ----

    [Fact]
    public void NothingSaved_OrNoScreens_MeansTheDefault()
    {
        AssertRejected(null, Main);
        AssertRejected(Place(100, 100));
    }

    [Theory]
    [InlineData(WindowPlacementValidator.MinWidth - 1, 840)]
    [InlineData(1280, WindowPlacementValidator.MinHeight - 1)]
    [InlineData(50, 40)]
    [InlineData(0, 0)]
    [InlineData(-5, -5)]
    public void ATooSmallWindow_IsRejected(int width, int height)
    {
        AssertRejected(Place(100, 100, width, height), Main);
    }

    [Fact]
    public void TheSmallestAllowedWindow_IsUsed()
    {
        AssertUsed(Place(100, 100, WindowPlacementValidator.MinWidth, WindowPlacementValidator.MinHeight), Main);
    }

    [Theory]
    [InlineData(30000, 20000)]
    [InlineData(1920 + 65, 800)]
    [InlineData(1280, 1040 + 65)]
    public void AWindowLargerThanAllScreensTogether_IsRejected(int width, int height)
    {
        AssertRejected(Place(0, 0, width, height), Main);
    }

    [Fact]
    public void AWindowThatWasOnAMonitorThatIsGone_IsRejected()
    {
        AssertRejected(Place(-1800, 100), Main);       // the left monitor was unplugged
        AssertRejected(Place(2500, 100), Main);        // so was the right one
    }

    [Theory]
    [InlineData(90000, 90000)]
    [InlineData(-32000, -32000)]                       // what Windows reports for a minimized window
    [InlineData(100, 2000)]                            // far below the screen
    [InlineData(100, -300)]                            // the title bar above the top of the screen
    public void AWindowWhoseTitleBarCannotBeReached_IsRejected(int x, int y)
    {
        AssertRejected(Place(x, y), Main);
    }

    [Fact]
    public void ATitleBarThatIsOnlyAFewPixelsVisible_IsRejected()
    {
        AssertRejected(Place(1800, 100), Main);        // 120 px of the title bar on screen: less than can be grabbed
        AssertRejected(Place(1850, 100), Main);        // 70 px
        AssertRejected(Place(100, 1035), Main);        // 5 px of it above the bottom edge
        AssertRejected(Place(100, -28), Main);         // only 4 px of the 32-px title bar below the top edge
    }

    [Fact]
    public void TheBoundaryOfTheVisibleTitleBar_IsInclusive()
    {
        int width = 1280;
        AssertUsed(Place(1920 - WindowPlacementValidator.MinVisibleTitleWidth, 100, width), Main);
        AssertRejected(Place(1920 - WindowPlacementValidator.MinVisibleTitleWidth + 1, 100, width), Main);
    }

    // ---- store ----

    [Fact]
    public void Store_RoundTripsThePlacement_IncludingTheMaximizedFlag()
    {
        var store = new WindowPlacementStore(Path.Combine(_dir, "window.json"));
        Assert.Null(store.Load());

        store.Save(Place(-1800, 120, 1500, 900, max: true));
        Assert.Equal(Place(-1800, 120, 1500, 900, max: true), store.Load());

        store.Save(Place(10, 20, 1000, 700));
        Assert.Equal(Place(10, 20, 1000, 700), store.Load());
    }

    [Theory]
    [InlineData("not json {")]
    [InlineData("")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"X\": \"left\", \"Width\": true }")]
    public void Store_TreatsAnUnreadableFileAsNothingSaved(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "window.json");
        File.WriteAllText(path, content);

        Assert.Null(new WindowPlacementStore(path).Load());
    }

    [Fact]
    public void Store_FailingToSave_DoesNotThrow()
    {
        Directory.CreateDirectory(_dir);
        string blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where a folder is needed");

        var store = new WindowPlacementStore(Path.Combine(blocker, "window.json"));

        store.Save(Place(1, 2)); // closing the app must not fail because the position could not be written
        Assert.Null(store.Load());
    }

    [Fact]
    public void ASavedFileWithAbsurdValues_LoadsButIsRejectedByTheValidator()
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "window.json");
        File.WriteAllText(path, "{ \"X\": 90000, \"Y\": 90000, \"Width\": 50, \"Height\": 40, \"IsMaximized\": true }");

        var loaded = new WindowPlacementStore(path).Load();

        Assert.NotNull(loaded);
        Assert.Null(WindowPlacementValidator.Validate(loaded, new[] { Main }));
    }
}
