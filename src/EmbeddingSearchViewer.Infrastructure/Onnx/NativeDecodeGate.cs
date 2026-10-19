namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>
/// Serialises the native image/video decode+encode work (SkiaSharp, WIC, Media Foundation frame grab, FFmpeg frame
/// decode). Those libraries are not safe to drive from many threads at once, and concurrent decoding corrupts the
/// process heap (0xC0000374). This is a single, process-wide leaf lock: everything inside it is synchronous native
/// work and no lock is ever taken after it.
/// </summary>
/// <remarks>
/// Lock order (outermost first): caller/entity locks, then <c>OnnxDirectMlEmbeddingService._visionRunLock</c> /
/// <c>_languageModelRunLock</c>, and finally <see cref="NativeDecodeGate"/> as the innermost leaf. Never acquire
/// either run lock (or any other lock) while holding this gate, and never <c>await</c> inside it.
/// </remarks>
public static class NativeDecodeGate
{
    public static readonly object Lock = new object();
}
