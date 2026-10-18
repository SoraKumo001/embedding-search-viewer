using System.Diagnostics;
using System.Threading;
using EmbeddingSearchViewer.Core.Localization;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// Lightweight probe for the two stages that decide how a run spends its time: embedding (GPU, image batches) and
/// saving (SQLite). It answers one question only - how much of the run the save path takes (input for the P5
/// decision) - and is deliberately not a permanent UI feature: one timestamp around each call and a few
/// <see cref="Interlocked"/> counters, nothing else.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="VideoIngestStats"/> on purpose: that type keeps its own numbers and wording, and this
/// probe is only appended next to them (never summed into them), so the existing summary line keeps its meaning.
/// <para>
/// <b>Manual measurement with 10,000 synthetic assets</b> (not automated; the test suite only checks that the probe
/// counts, see <c>IngestionWorkerTests</c>):
/// </para>
/// <list type="number">
/// <item><description>
/// Build the folder and fill it with 10,000 files. Copies of one small seed image are enough: the pipeline keys on
/// path plus content hash, so every copy is embedded on its own.
/// <c>mkdir C:\tmp\bench; 1..10000 | % { Copy-Item C:\tmp\seed.jpg "C:\tmp\bench\img_$_.jpg" }</c>
/// (a mix of sizes/PDFs is closer to a real library if that is what is being judged).
/// </description></item>
/// <item><description>
/// Point a library at that folder and start indexing (app, or the worker's test harness). A cold cache and an empty
/// index are what make the numbers comparable between runs.
/// </description></item>
/// <item><description>
/// Read the one console line this probe prints when the run ends, next to the existing
/// <c>[EmbeddingStats]</c> lines: <c>[StageStats] embed 123.4s in 625 calls (10000 assets), save 8.7s in 625 calls
/// (10000 assets), save share 6.6%</c>. The same split is appended to the run's completion summary on screen.
/// </description></item>
/// <item><description>
/// Judge P5 from the save share: a few percent means the save path needs no work; a large share (say above 10-15%)
/// means it is worth optimising (fewer transactions, larger batches, WAL/PRAGMA tuning).
/// </description></item>
/// <item><description>
/// Repeat once more without changing anything (all files are "unchanged"): the probe then reports no samples, which
/// is the second half of the decision - nothing to save means nothing the save path could win back.
/// </description></item>
/// </list>
/// </remarks>
public sealed class IngestionStageStats
{
    private long _embedTicks;
    private long _embedCalls;
    private long _embedItems;
    private long _saveTicks;
    private long _saveCalls;
    private long _saveItems;

    /// <summary>Wall time spent in the image embedding call, summed over batches.</summary>
    public double EmbedSeconds => ToSeconds(Interlocked.Read(ref _embedTicks));

    /// <summary>
    /// Wall time spent in the save calls, summed over image batches and single non-image files. Saves of videos that
    /// ran in parallel add up, so this total can exceed the wall clock of the run.
    /// </summary>
    public double SaveSeconds => ToSeconds(Interlocked.Read(ref _saveTicks));

    public long EmbedCalls => Interlocked.Read(ref _embedCalls);
    public long EmbedItems => Interlocked.Read(ref _embedItems);
    public long SaveCalls => Interlocked.Read(ref _saveCalls);
    public long SaveItems => Interlocked.Read(ref _saveItems);

    /// <summary>True once either stage was timed at least once in this run.</summary>
    public bool HasSamples => Interlocked.Read(ref _embedCalls) + Interlocked.Read(ref _saveCalls) > 0;

    /// <summary>Saving as a share of embedding plus saving, in percent; 0 when nothing was timed.</summary>
    public double SaveSharePercent
    {
        get
        {
            double total = EmbedSeconds + SaveSeconds;
            return total > 0 ? SaveSeconds * 100.0 / total : 0;
        }
    }

    private static double ToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;

    internal void AddEmbed(long ticks, int items)
    {
        Interlocked.Increment(ref _embedCalls);
        Interlocked.Add(ref _embedItems, items);
        Interlocked.Add(ref _embedTicks, ticks);
    }

    internal void AddSave(long ticks, int items)
    {
        Interlocked.Increment(ref _saveCalls);
        Interlocked.Add(ref _saveItems, items);
        Interlocked.Add(ref _saveTicks, ticks);
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _embedTicks, 0);
        Interlocked.Exchange(ref _embedCalls, 0);
        Interlocked.Exchange(ref _embedItems, 0);
        Interlocked.Exchange(ref _saveTicks, 0);
        Interlocked.Exchange(ref _saveCalls, 0);
        Interlocked.Exchange(ref _saveItems, 0);
    }

    /// <summary>The probe as one clause for the completion summary, or an empty string before any stage ran.</summary>
    public string Summary()
        => HasSamples ? Loc.T("Ingest.StageStats", EmbedSeconds, SaveSeconds, SaveSharePercent) : string.Empty;

    /// <summary>
    /// Language-independent one-liner for the console, in a fixed shape so that runs (and the 10k measurement) can be
    /// compared regardless of the UI language.
    /// </summary>
    public string Describe()
    {
        if (!HasSamples) return "no samples this run";
        return FormattableString.Invariant(
            $"embed {EmbedSeconds:F1}s in {EmbedCalls} calls ({EmbedItems} assets), save {SaveSeconds:F1}s in {SaveCalls} calls ({SaveItems} assets), save share {SaveSharePercent:F1}%");
    }
}
