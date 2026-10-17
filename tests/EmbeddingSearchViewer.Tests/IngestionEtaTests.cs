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

    private static IngestionProgress Progress(int total, int processed, int quick)
        => new(total, processed, "f", true, QuickFiles: quick);

    [Fact]
    public void FilesThatNeededNoWork_DoNotMakeTheEstimateShorter()
    {
        var eta = new IngestionEta();
        eta.Update(T0, Progress(1000, 0, 0));

        // 30 s of checking 600 files that are already indexed: no estimate, because nothing has been worked on yet.
        for (int s = 5; s <= 30; s += 5) Assert.Null(eta.Update(T0.AddSeconds(s), Progress(1000, s * 20, s * 20)));

        // Then 20 files of real work in 20 s (1 per second); 380 files are left and all of them are assumed to need work
        // at the share seen so far (20 of 620: about 3%), but the speed is the speed of the work only.
        TimeSpan? left = null;
        for (int s = 1; s <= 20; s++) left = eta.Update(T0.AddSeconds(30 + s), Progress(1000, 600 + s, 600));

        Assert.NotNull(left);
        // 380 files x 3.2% = about 12 files of work at 1 file per second: about 12 s. Counting the checking too would give 3 s.
        Assert.InRange(left!.Value.TotalSeconds, 8, 20);
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
    public void FormatEta_UsesCoarseSteps()
    {
        Assert.Equal("完了まで: 1 分未満", IngestionEta.Format(TimeSpan.FromSeconds(30)));
        Assert.Equal("完了まで: 約 3 分", IngestionEta.Format(TimeSpan.FromSeconds(121)));
        Assert.Equal("完了まで: 約 1 時間 5 分", IngestionEta.Format(TimeSpan.FromMinutes(65)));
    }
}
