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
/// Two forms share this gate: <see cref="Lock"/> is the leaf lock for <em>synchronous</em> native work (SkiaSharp)
/// and must never contain an <c>await</c>; <see cref="VideoGate"/> is an async semaphore for native sections that
/// must <c>await</c> (FFmpeg <c>ExtractVideoFrameAsync</c> + <c>EncodeAsJpegAsync</c>, Media Foundation
/// <c>GetThumbnailAsync</c>). Both serialise the same native libraries, so never hold one while awaiting the other.
/// </para>
/// </remarks>
public static class NativeDecodeGate
{
    /// <summary>Leaf lock for synchronous native work (SkiaSharp). Never <c>await</c> while holding it.</summary>
    public static readonly object Lock = new object();

    /// <summary>
    /// Async gate (1 permit) for native sections that must <c>await</c> (FFmpeg / Media Foundation frame extraction
    /// and JPEG encode). Shared process-wide so those native codecs are never driven concurrently.
    /// </summary>
    public static readonly SemaphoreSlim VideoGate = new(1, 1);
}
