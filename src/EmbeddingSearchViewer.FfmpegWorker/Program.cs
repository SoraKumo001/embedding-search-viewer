using System.Globalization;
using EmbeddingSearchViewer.Core.Services;
using FFmpegInteropX;
using SkiaSharp;
using Windows.Storage.Streams;

namespace EmbeddingSearchViewer_FfmpegWorker;

/// <summary>
/// One video per process: opens the file with FFmpeg, plans its segments exactly like the in-process extractor and
/// writes one JPEG per segment into the video's cache directory.
/// </summary>
/// <remarks>
/// <para>
/// The parent (<c>ChildProcessFrameSource</c>) starts this process once per video and waits for one line per segment:
/// <c>DURATION &lt;ms&gt;</c> right after opening the file, then <c>SEG &lt;index&gt; &lt;middleMs&gt;</c> after the JPEG of
/// that segment has been written and flushed. Nothing else is exchanged: the pictures are the JPEG files and the lines
/// only say that a file is complete.
/// </para>
/// <para>
/// Exit codes: 0 = every planned segment was written, 2 = the decoder failed (the file could not be opened, or a frame
/// could not be read; the remaining segments are still tried), anything else = the worker itself failed. Nothing is
/// retried here: a frame that fails is reported, and the parent falls back to Media Foundation.
/// </para>
/// <para>
/// FFmpeg's native objects never leave this process, so the gate the in-process decoder needed is not used at all.
/// </para>
/// </remarks>
internal static class Program
{
    private const int MaxFrameWidth = 960;
    private const int MaxFrameSkipAfterKeyFrame = 120;

    private const int ExitComplete = 0;
    private const int ExitDecoderFailed = 2;
    private const int ExitWorkerFailed = 3;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.Error.WriteLine("usage: EmbeddingSearchViewer.FfmpegWorker <videoPath> <cacheDirectory> <segmentSeconds> <overlapSeconds> <skipIndexList> [maxSegments]");
            return ExitWorkerFailed;
        }

        if (!TryParseInt(args[2], out int segmentSeconds) || segmentSeconds <= 0
            || !TryParseInt(args[3], out int overlapSeconds) || overlapSeconds < 0)
        {
            Console.Error.WriteLine($"invalid segment arguments: segmentSeconds={args[2]} overlapSeconds={args[3]}");
            return ExitWorkerFailed;
        }

        int maxSegments = 0;
        if (args.Length > 5 && !TryParseInt(args[5], out maxSegments))
        {
            Console.Error.WriteLine($"invalid maxSegments: {args[5]}");
            return ExitWorkerFailed;
        }

        string videoPath;
        string cacheDirectory;
        HashSet<int> skip;
        try
        {
            videoPath = Path.GetFullPath(args[0]);
            cacheDirectory = Path.GetFullPath(args[1]);
            skip = ParseSkipList(args[4]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"invalid arguments: {ex.GetType().Name}: {ex.Message}");
            return ExitWorkerFailed;
        }

        FrameGrabber? grabber = null;
        try
        {
            try
            {
                grabber = await FrameGrabber.CreateFromFileAsync(videoPath);
            }
            catch (Exception ex)
            {
                // The parent turns this into "this decoder cannot read the file" and tries Media Foundation.
                Console.Error.WriteLine($"open-failed: {ex.GetType().Name}: {ex.Message}");
                return ExitDecoderFailed;
            }

            // The duration is rounded to whole milliseconds on both sides: the parent plans from the DURATION line, so
            // the plan here must use the same value, or the segment indices would not line up.
            int durationMs = (int)Math.Round(grabber.Duration.TotalMilliseconds);
            if (durationMs <= 0)
            {
                Console.Error.WriteLine($"open-empty: durationMs={durationMs}");
                return ExitDecoderFailed;
            }

            var duration = TimeSpan.FromMilliseconds(durationMs);
            Emit($"DURATION {durationMs}");

            int effectiveSeconds = VideoSegmentPlanner.ResolveSegmentSeconds(duration, segmentSeconds, maxSegments);
            int effectiveOverlap = VideoSegmentPlanner.ResolveOverlapSeconds(segmentSeconds, overlapSeconds, effectiveSeconds);
            var plan = VideoSegmentPlanner.PlanSegments(duration, effectiveSeconds, effectiveOverlap);
            if (plan.Count == 0)
            {
                Console.Error.WriteLine($"plan-empty: durationMs={durationMs} effectiveSeconds={effectiveSeconds}");
                return ExitDecoderFailed;
            }

            Directory.CreateDirectory(cacheDirectory);

            int failures = 0;
            for (int i = 0; i < plan.Count; i++)
            {
                if (skip.Contains(i)) continue; // finished by an earlier run: the parent keeps its frame

                var (start, end) = plan[i];
                int middleMs = (int)((start + end) / 2.0);
                string path = VideoSegmentPlanner.FramePath(cacheDirectory, videoPath, i);

                if (await GrabAsync(grabber, TimeSpan.FromMilliseconds(middleMs), path))
                {
                    // Only a frame that is on disk is announced: the parent takes this line as "the JPEG is complete".
                    Emit($"SEG {i} {middleMs}");
                }
                else
                {
                    failures++;
                    Console.Error.WriteLine($"frame-failed: index={i} startMs={start} endMs={end}");
                }
            }

            if (failures > 0) Console.Error.WriteLine($"partial: segments={plan.Count} failed={failures}");
            return failures == 0 ? ExitComplete : ExitDecoderFailed;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"worker-failed: {ex.GetType().Name}: {ex.Message}");
            return ExitWorkerFailed;
        }
        finally
        {
            try
            {
                grabber?.Dispose();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"dispose-failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Grabs the frame at <paramref name="position"/>, keeps the thumbnail small (the embedding model resizes anyway)
    /// and writes it as a JPEG. The same steps as the in-process FFmpeg source, without the process-wide gate.
    /// </summary>
    private static async Task<bool> GrabAsync(FrameGrabber grabber, TimeSpan position, string path)
    {
        using var frame = await grabber.ExtractVideoFrameAsync(position, exactSeek: false, maxFrameSkip: MaxFrameSkipAfterKeyFrame);
        if (frame == null) return false;

        byte[] bytes;
        using (var stream = new InMemoryRandomAccessStream())
        {
            await frame.EncodeAsJpegAsync(stream);
            stream.Seek(0);

            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
        }

        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap == null) return false;

        SKBitmap? resized = null;
        try
        {
            var source = bitmap;
            if (bitmap.Width > MaxFrameWidth)
            {
                int height = Math.Max(2, (int)Math.Round(bitmap.Height * (double)MaxFrameWidth / bitmap.Width));
                resized = bitmap.Resize(new SKImageInfo(MaxFrameWidth, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized != null) source = resized;
            }

            using var image = SKImage.FromBitmap(source);
            using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            if (jpeg == null) return false;

            await File.WriteAllBytesAsync(path, jpeg.ToArray());
            return true;
        }
        finally
        {
            resized?.Dispose();
        }
    }

    /// <summary>One protocol line, flushed so the parent sees it before the process ends.</summary>
    private static void Emit(string line)
    {
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    private static bool TryParseInt(string text, out int value)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>"3,7" (or an empty list) to the set of segments the parent already has.</summary>
    private static HashSet<int> ParseSkipList(string text)
    {
        var skip = new HashSet<int>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "-") continue;
            if (!TryParseInt(part, out int index) || index < 0)
            {
                throw new FormatException($"skip index '{part}' is not a segment number");
            }
            skip.Add(index);
        }
        return skip;
    }
}
