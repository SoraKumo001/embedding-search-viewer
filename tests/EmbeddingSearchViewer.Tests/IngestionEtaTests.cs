using System;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public class IngestionEtaTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GivesNoEstimateDuringTheWarmUp()
    {
        var eta = new IngestionEta();

        Assert.Null(eta.Update(T0, 0, 100));
        Assert.Null(eta.Update(T0.AddSeconds(5), 10, 100));
        Assert.NotNull(eta.Update(T0.AddSeconds(11), 22, 100));
    }

    [Fact]
    public void EstimatesFromTheSpeedSoFar()
    {
        var eta = new IngestionEta();
        eta.Update(T0, 0, 100);

        // 2 files per second, 80 left: 40 seconds.
        var left = eta.Update(T0.AddSeconds(10), 20, 100);

        Assert.Equal(40, left!.Value.TotalSeconds, 1);
    }

    [Fact]
    public void FollowsAChangeOfSpeed_ByLookingAtTheLastMinuteOnly()
    {
        var eta = new IngestionEta();
        eta.Update(T0, 0, 1000);
        // A fast start (10 files per second for 60 s), then a slow stretch (1 file per second).
        for (int s = 1; s <= 60; s++) eta.Update(T0.AddSeconds(s), s * 10, 1000);
        TimeSpan? left = null;
        for (int s = 61; s <= 180; s++) left = eta.Update(T0.AddSeconds(s), 600 + (s - 60), 1000);

        // 280 files are left. At the recent 1 file per second that is about 280 s; the whole-run average (4 per second) would say 70 s.
        Assert.InRange(left!.Value.TotalSeconds, 250, 320);
    }

    [Fact]
    public void GivesNoEstimateWithoutProgress_AndNothingLeftMeansZero()
    {
        var eta = new IngestionEta();
        eta.Update(T0, 5, 100);
        Assert.Null(eta.Update(T0.AddSeconds(30), 5, 100)); // nothing done yet: no speed

        Assert.Equal(TimeSpan.Zero, eta.Update(T0.AddSeconds(40), 100, 100));
    }

    private static IngestionProgress Progress(int total, int processed, int quick,
        double videoDone = 0, double videoRemaining = 0)
        => new(total, processed, "f", true, QuickFiles: quick,
            VideoDoneScenes: videoDone, VideoRemainingScenes: videoRemaining);

    [Fact]
    public void FilesThatNeededNoWork_DoNotMakeTheEstimateShorter()
    {
        var eta = new IngestionEta();
        eta.Update(T0, Progress(1000, 0, 0));

        // 30 s of checking 600 files that are already indexed: no estimate, because nothing has been worked on yet.
        for (int s = 5; s <= 30; s += 5) Assert.Null(eta.Update(T0.AddSeconds(s), Progress(1000, s * 20, s * 20)));

        // Then 20 files of real work in 20 s (1 per second). The speed is the speed of the work only (1 per second), and
        // because the last minute is all work the 380 files left are assumed to need work too: about 380 s. Reading the
        // whole-run share instead (20 of 620: about 3%) would say about 12 s, which is the underestimate being fixed.
        TimeSpan? left = null;
        for (int s = 1; s <= 20; s++) left = eta.Update(T0.AddSeconds(30 + s), Progress(1000, 600 + s, 600));

        Assert.NotNull(left);
        Assert.InRange(left!.Value.TotalSeconds, 300, 460);
    }

    [Fact]
    public void ResumingBehindAQuickStart_DoesNotUnderestimateTheRest()
    {
        var eta = new IngestionEta();
        eta.Update(T0, Progress(2000, 0, 0));

        // A resume: 600 files are already indexed and fly through in the first 30 s.
        for (int s = 5; s <= 30; s += 5) eta.Update(T0.AddSeconds(s), Progress(2000, s * 20, s * 20));

        // Then real work at 1 file per second, long enough (70 s) that the whole last minute is work.
        TimeSpan? left = null;
        for (int s = 1; s <= 70; s++) left = eta.Update(T0.AddSeconds(30 + s), Progress(2000, 600 + s, 600));

        Assert.NotNull(left);
        // 1330 files left, all assumed to need work at 1 per second: about 1330 s. The whole-run share (70 of 670:
        // about 10%) would say about 133 s.
        Assert.InRange(left!.Value.TotalSeconds, 1200, 1450);
    }

    [Fact]
    public void AStallDoesNotDiluteTheSpeed()
    {
        var eta = new IngestionEta();
        eta.Update(T0, 0, 1000);

        // 2 files per second for 10 s, then a 30 s stall with nothing to report, then the same speed again.
        for (int s = 1; s <= 10; s++) eta.Update(T0.AddSeconds(s), s * 2, 1000);
        for (int s = 11; s <= 40; s++) eta.Update(T0.AddSeconds(s), 20, 1000);
        TimeSpan? left = null;
        for (int s = 1; s <= 10; s++) left = eta.Update(T0.AddSeconds(40 + s), 20 + s * 2, 1000);

        Assert.NotNull(left);
        // The 30 s without work must not count: 40 files in 20 s of work is 2 per second, so 960 left is about 480 s.
        // Counting the stall would give 0.8 per second, or about 1200 s.
        Assert.InRange(left!.Value.TotalSeconds, 430, 530);
    }

    [Fact]
    public void Reset_StartsAFreshRun()
    {
        var eta = new IngestionEta();
        eta.Update(T0, 0, 100);
        eta.Update(T0.AddSeconds(20), 50, 100);

        eta.Reset();

        Assert.Null(eta.Update(T0.AddSeconds(30), 0, 10)); // first report of the new run
    }

    [Fact]
    public void ScenesLeftInVideos_CanOutlastTheFileEstimate()
    {
        var eta = new IngestionEta();
        eta.Update(T0, Progress(1000, 890, 0));

        // Files at 1 per second, 50 left at the end (about 50 s), while the videos still need 200 scenes at 2 per second
        // (about 100 s). The video side must win: a video counts as one file however many scenes it needs.
        TimeSpan? left = null;
        for (int s = 1; s <= 60; s++)
            left = eta.Update(T0.AddSeconds(s), Progress(1000, 890 + s, 0, videoDone: s * 2, videoRemaining: 200));

        Assert.NotNull(left);
        Assert.InRange(left!.Value.TotalSeconds, 90, 115);
    }

    [Fact]
    public void NoScenesLeftInVideos_KeepsTheFileEstimate()
    {
        var eta = new IngestionEta();
        eta.Update(T0, Progress(1000, 890, 0));

        TimeSpan? left = null;
        for (int s = 1; s <= 60; s++)
            left = eta.Update(T0.AddSeconds(s), Progress(1000, 890 + s, 0, videoDone: s * 2, videoRemaining: 0));

        Assert.NotNull(left);
        // 50 files at 1 per second; the scene speed (2 per second) must not be used when nothing is left.
        Assert.InRange(left!.Value.TotalSeconds, 40, 60);
    }

    [Fact]
    public void AJustStartedVideo_DoesNotProduceAWildSceneEstimate()
    {
        var eta = new IngestionEta();
        eta.Update(T0, Progress(1000, 890, 0));

        // Files at 1 per second, 30 left. Then a video starts and reports 10000 scenes left during its first 5 s only:
        // with no scene speed measured yet the file estimate must be kept (10000 scenes would otherwise be hours).
        TimeSpan? left = null;
        for (int s = 1; s <= 20; s++)
            left = eta.Update(T0.AddSeconds(s), Progress(940, 890 + s, 0, videoDone: 0, videoRemaining: 0));
        for (int s = 21; s <= 25; s++)
            left = eta.Update(T0.AddSeconds(s), Progress(940, 890 + s, 0, videoDone: (s - 20) * 2, videoRemaining: 10000));

        Assert.NotNull(left);
        Assert.InRange(left!.Value.TotalSeconds, 20, 35);
    }

    [Fact]
    public void FormatEta_UsesCoarseSteps()
    {
        Assert.Equal("完了まで: 1 分未満", IngestionEta.Format(TimeSpan.FromSeconds(30)));
        Assert.Equal("完了まで: 約 3 分", IngestionEta.Format(TimeSpan.FromSeconds(121)));
        Assert.Equal("完了まで: 約 1 時間 5 分", IngestionEta.Format(TimeSpan.FromMinutes(65)));
    }
}
