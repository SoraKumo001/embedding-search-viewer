using System;
using System.Threading;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>
/// Serialises the native image/video decode+encode work (SkiaSharp, WIC, Media Foundation frame grab, FFmpeg frame
/// decode). Those libraries are not safe to drive from many threads at once, and concurrent decoding corrupts the
/// process heap (0xC0000374). This is a single, process-wide leaf gate: everything inside it is synchronous native
/// work and no lock is ever taken after it.
/// </summary>
/// <remarks>
/// Lock order (outermost first): caller/entity locks, then <c>OnnxDirectMlEmbeddingService._visionRunLock</c> /
/// <c>_languageModelRunLock</c>, and finally <see cref="NativeDecodeGate"/> as the innermost leaf. Never acquire
/// either run lock (or any other lock) while holding this gate, and never <c>await</c> inside <see cref="Lock"/>.
/// <para>
/// Forms of this gate: <see cref="Lock"/> is the leaf lock for <em>synchronous</em> native work (SkiaSharp) and must
/// never contain an <c>await</c>. <see cref="FfmpegGate"/> is an async semaphore whose permit count is
/// <see cref="FfmpegPermit"/> (1 unless the diagnostics store asks for 2) for FFmpeg <c>ExtractVideoFrameAsync</c>,
/// <c>FrameGrabber</c> creation and grabber teardown; the gate instance is replaced only between runs, through
/// <see cref="TryConfigureFfmpegPermit"/>. <see cref="MediaFoundationGate"/> is an async semaphore with a permanent
/// 1 permit for Media Foundation <c>GetThumbnailAsync</c>.
/// </para>
/// <para>
/// Each decoder stack has its own gate so a stall in one (e.g. a hung FFmpeg decode holding its gate for up to the
/// abandoned-work wait limit) cannot block the other, and neither blocks on a codec that only the other uses. At most
/// <see cref="FfmpegPermit"/> FFmpeg native calls may run at once (two only in the opt-in experiment), and Media
/// Foundation is still strictly one-at-a-time: never drive the <em>same</em> stack beyond its permit count, and never
/// hold one gate while awaiting another (that would restore the old cross-stack deadlock risk). <see cref="Lock"/> is
/// shared by all of them and is only ever taken as a leaf, so the Skia section stays mutually exclusive with every
/// decoder's Skia work.
/// </para>
/// </remarks>
public static class NativeDecodeGate
{
    /// <summary>The production FFmpeg permit count: strictly serial, the historical behaviour.</summary>
    public const int DefaultFfmpegPermit = 1;

    /// <summary>Highest permit count: the heap-corruption bug this gate exists for is only probed at two.</summary>
    public const int MaxFfmpegPermit = 2;

    // Guards a swap and keeps readers from seeing a half-written pair. The fields are plain (not volatile) so that
    // Volatile.Read/Write can be used on them by ref.
    private static readonly object FfmpegSwapLock = new();
    private static int _ffmpegPermit = DefaultFfmpegPermit;
    private static SemaphoreSlim _ffmpegGate = CreateFfmpegGate(DefaultFfmpegPermit);

    /// <summary>
    /// Number of concurrent FFmpeg native sections allowed by <see cref="FfmpegGate"/>. 1 (the production default)
    /// keeps the historical strictly serial behaviour; 2 is only set by the diagnostics store and is a measurement
    /// experiment (see the P2c gate experiment). Changed only between runs, through <see cref="TryConfigureFfmpegPermit"/>.
    /// </summary>
    public static int FfmpegPermit => Volatile.Read(ref _ffmpegPermit);

    /// <summary>Leaf lock for synchronous native work (SkiaSharp). Never <c>await</c> while holding it.</summary>
    public static readonly object Lock = new object();

    /// <summary>
    /// Async gate (<see cref="FfmpegPermit"/> permits, 1 by default) for FFmpeg native sections that must
    /// <c>await</c>: frame extraction, grabber creation and grabber teardown. The JPEG encode of an extracted frame is
    /// <em>not</em> inside this gate any more: the frame carries its own pixel buffer, so it is encoded through WIC
    /// outside the gate (P2b). Never hold this while awaiting <see cref="MediaFoundationGate"/>.
    /// <para>
    /// Callers must read the property <em>once</em> per section and use that local for both the wait and the release:
    /// the instance is replaced between runs, and a wait and release on two different instances would corrupt the count.
    /// </para>
    /// </summary>
    public static SemaphoreSlim FfmpegGate => Volatile.Read(ref _ffmpegGate);

    /// <summary>
    /// Async gate (permanent 1 permit) for Media Foundation native sections that must <c>await</c> (thumbnail grab).
    /// Never hold this while awaiting <see cref="FfmpegGate"/>.
    /// </summary>
    public static readonly SemaphoreSlim MediaFoundationGate = new(1, 1);

    /// <summary>
    /// Former single async gate shared by the FFmpeg and Media Foundation frame sources. Kept only so external
    /// references keep compiling and are visible in review; nothing in this solution uses it any more.
    /// </summary>
    [Obsolete("Unused: FFmpeg and Media Foundation now have separate gates (FfmpegGate / MediaFoundationGate).")]
    public static readonly SemaphoreSlim VideoGate = new(1, 1);

    /// <summary>
    /// Replaces the FFmpeg gate with one of <paramref name="permit"/> permits, but only while the current gate is
    /// idle (fully released, no waiter): the worker calls this when a run starts, before any video is opened. If a
    /// section is still in flight the request is refused (false) and the caller keeps the current gate.
    /// </summary>
    /// <remarks>
    /// Swapping is intentionally not done under the <see cref="Lock"/> leaf or while any FFmpeg section is running: a
    /// section that waited on the old instance must release the same instance. The old semaphore is left to the GC
    /// rather than disposed, because a caller that read the property just before the swap may still use it.
    /// </remarks>
    public static bool TryConfigureFfmpegPermit(int permit)
    {
        int wanted = Math.Clamp(permit, 1, MaxFfmpegPermit);
        lock (FfmpegSwapLock)
        {
            int current = Volatile.Read(ref _ffmpegPermit);
            if (wanted == current) return true;

            SemaphoreSlim gate = Volatile.Read(ref _ffmpegGate);
            if (gate.CurrentCount != current) return false; // a section is in flight (or someone is waiting): not idle

            Volatile.Write(ref _ffmpegGate, new SemaphoreSlim(wanted, wanted));
            Volatile.Write(ref _ffmpegPermit, wanted);
            return true;
        }
    }

    /// <summary>
    /// Builds the FFmpeg gate with the given permit count. Kept as a factory so the permit is decided in one place
    /// (the diagnostics store) and a swap creates exactly the same kind of gate.
    /// </summary>
    private static SemaphoreSlim CreateFfmpegGate(int permit) => new(permit, permit);
}
