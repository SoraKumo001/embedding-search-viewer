using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EmbeddingSearchViewer.Infrastructure.Ingestion;

/// <summary>
/// The embeddings of the segments of one video that are already finished, so that an interrupted run (stopped by the
/// user, the app closed, a crash) continues where it stopped instead of starting the video over. Records are appended
/// as segments are embedded; a half-written last record (a crash while writing) is ignored when reading.
/// </summary>
internal sealed class VideoCheckpoint
{
    private const int Magic = 0x43565345; // "ESVC"
    private const string FileName = "checkpoint.bin";

    private readonly string _path;
    private readonly int _dimension;

    public VideoCheckpoint(string directory, int dimension)
    {
        _path = Path.Combine(directory, FileName);
        _dimension = dimension;
    }

    /// <summary>
    /// A folder name that changes whenever anything that affects the frames or their embeddings changes:
    /// the file (path, size, modification time), the segment layout (including the segment limit that lengthens the
    /// segments of long videos), the embedding size and the model.
    /// </summary>
    public static string KeyFor(string videoPath, long length, long modifiedTicks, int segmentSeconds, int overlapSeconds, int dimension, string tag, int maxSegments = 0)
    {
        string text = $"{Path.GetFullPath(videoPath).ToLowerInvariant()}|{length}|{modifiedTicks}|{segmentSeconds}|{overlapSeconds}|{dimension}|{tag}";
        if (maxSegments > 0) text += $"|max{maxSegments}"; // unchanged for 0, so existing checkpoints keep their folder
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..20].ToLowerInvariant();
    }

    /// <summary>Segment number to embedding; an empty embedding means "looked at, nothing to store" (a blank frame).</summary>
    public Dictionary<int, float[]> Load()
    {
        var result = new Dictionary<int, float[]>();
        if (!File.Exists(_path)) return result;

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 8 || reader.ReadInt32() != Magic || reader.ReadInt32() != _dimension)
            {
                return new Dictionary<int, float[]>(); // another layout: not usable
            }

            while (stream.Length - stream.Position >= 8)
            {
                int index = reader.ReadInt32();
                int length = reader.ReadInt32();
                if (index < 0 || (length != 0 && length != _dimension)) break;
                if (stream.Length - stream.Position < (long)length * sizeof(float)) break; // torn last record

                var embedding = new float[length];
                for (int i = 0; i < length; i++) embedding[i] = reader.ReadSingle();
                result[index] = embedding;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            // An unreadable checkpoint only costs the work it would have saved.
        }
        return result;
    }

    public void Append(IEnumerable<(int Index, float[] Embedding)> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            bool isNew = !File.Exists(_path) || new FileInfo(_path).Length < 8;
            using var stream = new FileStream(_path, isNew ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(stream);
            if (isNew)
            {
                writer.Write(Magic);
                writer.Write(_dimension);
            }
            foreach (var (index, embedding) in records)
            {
                writer.Write(index);
                writer.Write(embedding.Length);
                foreach (float f in embedding) writer.Write(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Resuming is a convenience; failing to save it must not fail the video.
        }
    }

    public void Delete()
    {
        try { File.Delete(_path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
