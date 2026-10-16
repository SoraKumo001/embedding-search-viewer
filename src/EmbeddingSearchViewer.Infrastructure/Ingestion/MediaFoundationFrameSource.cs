using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Windows.Media.Editing;
using Windows.Storage;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// Grabs frames with the Windows media stack (Media Foundation via <c>Windows.Media.Editing</c>).
/// It needs no extra files and covers the common containers and codecs (mp4/H.264, wmv, avi, mov, and on current
/// Windows mkv). It is the fallback behind FFmpeg in the app, and the only decoder when none is configured.
/// </summary>
public sealed class MediaFoundationFrameSource : IVideoFrameSource
{
    private const int MaxFrameWidth = 960;

    public string Name => "Media Foundation";

    public async Task<IVideoFrameSession?> OpenAsync(string videoPath, CancellationToken cancellationToken)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(videoPath));
            var clip = await MediaClip.CreateFromFileAsync(file);
            if (clip.OriginalDuration <= TimeSpan.Zero) return null;

            var props = clip.GetVideoEncodingProperties();
            if (props == null || props.Width == 0 || props.Height == 0) return null; // audio only or unreadable video stream

            var composition = new MediaComposition();
            composition.Clips.Add(clip);
            return new Session(composition, clip.OriginalDuration, (int)props.Width, (int)props.Height);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or FileNotFoundException
                                       or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private sealed class Session : IVideoFrameSession
    {
        private readonly MediaComposition _composition;
        private readonly int _width, _height;

        public Session(MediaComposition composition, TimeSpan duration, int width, int height)
        {
            _composition = composition;
            Duration = duration;
            // Display size without upscaling; the thumbnail API scales to exactly the size we ask for.
            double scale = Math.Min(1.0, (double)MaxFrameWidth / width);
            _width = Math.Max(2, (int)Math.Round(width * scale));
            _height = Math.Max(2, (int)Math.Round(height * scale));
        }

        public TimeSpan Duration { get; }

        public async Task<bool> SaveFrameAsync(TimeSpan position, string jpegPath, CancellationToken cancellationToken)
        {
            using var stream = await _composition.GetThumbnailAsync(position, _width, _height, VideoFramePrecision.NearestFrame);

            using var input = stream.AsStreamForRead();
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer, cancellationToken);

            using var bitmap = SKBitmap.Decode(buffer.ToArray());
            if (bitmap == null) return false;

            using var image = SKImage.FromBitmap(bitmap);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            await File.WriteAllBytesAsync(jpegPath, jpeg.ToArray(), cancellationToken);
            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
