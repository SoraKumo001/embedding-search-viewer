using System.Diagnostics;
using System.Threading;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>Where the time of image embedding goes, summed over all images since the last reset (milliseconds).</summary>
public sealed record EmbeddingStageSnapshot(int Images, double PrepareMs, double VisionWaitMs, double VisionRunMs, double LanguageModelMs)
{
    public double PerImage(double total) => Images == 0 ? 0 : total / Images;
}

/// <summary>Thread-safe accumulators for the stages of image embedding.</summary>
public sealed class EmbeddingStageStats
{
    private long _images, _prepare, _visionWait, _visionRun, _languageModel;

    private static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    internal void AddImage() => Interlocked.Increment(ref _images);
    internal void AddPrepare(long ticks) => Interlocked.Add(ref _prepare, ticks);
    internal void AddVisionWait(long ticks) => Interlocked.Add(ref _visionWait, ticks);
    internal void AddVisionRun(long ticks) => Interlocked.Add(ref _visionRun, ticks);
    internal void AddLanguageModel(long ticks) => Interlocked.Add(ref _languageModel, ticks);

    public EmbeddingStageSnapshot Snapshot() => new(
        (int)Interlocked.Read(ref _images),
        ToMs(Interlocked.Read(ref _prepare)),
        ToMs(Interlocked.Read(ref _visionWait)),
        ToMs(Interlocked.Read(ref _visionRun)),
        ToMs(Interlocked.Read(ref _languageModel)));

    public void Reset()
    {
        Interlocked.Exchange(ref _images, 0);
        Interlocked.Exchange(ref _prepare, 0);
        Interlocked.Exchange(ref _visionWait, 0);
        Interlocked.Exchange(ref _visionRun, 0);
        Interlocked.Exchange(ref _languageModel, 0);
    }
}
