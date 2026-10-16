using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>
/// Small fixes for the published ONNX graphs so that the DirectML execution provider can run them entirely on the GPU.
/// <list type="bullet">
/// <item>The vision encoder marks every Reshape with <c>allowzero=1</c>, which DirectML rejects (E_INVALIDARG).
/// The shapes never contain a zero, so <c>allowzero=0</c> computes exactly the same result.</item>
/// <item>The models use the standard <c>Gelu(approximate="tanh")</c>, which DirectML does not implement, so every
/// occurrence ran on the CPU with a GPU-to-CPU copy and back (about three quarters of the vision encoder's time).
/// The contrib operator <c>com.microsoft::FastGelu</c> is the same tanh formula and runs on the GPU.</item>
/// </list>
/// </summary>
public static class OnnxModelPatches
{
    /// <summary>Bump when the patches change so that stale patched copies on disk are rebuilt.</summary>
    public const int Version = 2;

    // ---- allowzero (same-size byte edit) ----

    // attribute name "allowzero" followed by: field 3 (i) = 1, field 20 (type) = INT
    private static readonly byte[] AllowZeroOn = Concat("allowzero"u8, new byte[] { 0x18, 0x01, 0xA0, 0x01, 0x02 });
    private static readonly byte[] AllowZeroOff = Concat("allowzero"u8, new byte[] { 0x18, 0x00, 0xA0, 0x01, 0x02 });

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r);
        b.CopyTo(r.AsSpan(a.Length));
        return r;
    }

    /// <summary>Returns a copy of the model with every <c>allowzero=1</c> turned into 0, and the number of attributes changed.</summary>
    public static (byte[] Model, int Replaced) DisableAllowZero(byte[] model)
    {
        var result = (byte[])model.Clone();
        int replaced = 0;
        var span = result.AsSpan();
        int offset = 0;
        while (offset <= span.Length - AllowZeroOn.Length)
        {
            int idx = span[offset..].IndexOf(AllowZeroOn);
            if (idx < 0) break;
            AllowZeroOff.CopyTo(span[(offset + idx)..]);
            replaced++;
            offset += idx + AllowZeroOn.Length;
        }
        return (result, replaced);
    }

    // ---- Gelu(tanh) -> com.microsoft::FastGelu (structural edit) ----

    /// <summary>
    /// Rewrites every standard <c>Gelu</c> node with <c>approximate="tanh"</c> into <c>com.microsoft::FastGelu</c>
    /// (identical tanh formula, no attribute). The model must already import the <c>com.microsoft</c> operator set.
    /// </summary>
    public static (byte[] Model, int Replaced) ReplaceTanhGeluWithFastGelu(byte[] model)
    {
        int replaced = 0;
        var output = RewriteMessage(model, field => field == 7 ? RewriteGraph : null, ref replaced);
        return (output, replaced);
    }

    private delegate byte[] Rewriter(byte[] message, ref int counter);

    private static byte[] RewriteGraph(byte[] graph, ref int counter) =>
        RewriteMessage(graph, field => field == 1 ? RewriteNode : null, ref counter);

    private static byte[] RewriteNode(byte[] node, ref int counter)
    {
        var fields = ReadFields(node);
        string? op = null, domain = null;
        bool tanh = false;
        foreach (var f in fields)
        {
            if (f.Number == 4) op = Encoding.UTF8.GetString(f.Payload);
            else if (f.Number == 7) domain = Encoding.UTF8.GetString(f.Payload);
            else if (f.Number == 5 && IsTanhApproximation(f.Payload)) tanh = true;
        }

        if (op != "Gelu" || !string.IsNullOrEmpty(domain) || !tanh) return node;

        using var ms = new MemoryStream();
        bool wroteDomain = false;
        foreach (var f in fields)
        {
            switch (f.Number)
            {
                case 4: WriteBytesField(ms, 4, Encoding.UTF8.GetBytes("FastGelu")); break;
                case 5: break; // drop the "approximate" attribute
                case 7: WriteBytesField(ms, 7, Encoding.UTF8.GetBytes("com.microsoft")); wroteDomain = true; break;
                default: ms.Write(f.Raw); break;
            }
        }
        if (!wroteDomain) WriteBytesField(ms, 7, Encoding.UTF8.GetBytes("com.microsoft"));
        counter++;
        return ms.ToArray();
    }

    private static bool IsTanhApproximation(byte[] attribute)
    {
        string? name = null, value = null;
        foreach (var f in ReadFields(attribute))
        {
            if (f.Number == 1) name = Encoding.UTF8.GetString(f.Payload);
            else if (f.Number == 4) value = Encoding.UTF8.GetString(f.Payload);
        }
        return name == "approximate" && value == "tanh";
    }

    // ---- minimal protobuf reading/writing ----

    private readonly record struct Field(int Number, int WireType, byte[] Payload, byte[] Raw);

    private static List<Field> ReadFields(byte[] data)
    {
        var fields = new List<Field>();
        int i = 0;
        while (i < data.Length)
        {
            int start = i;
            ulong key = ReadVarint(data, ref i);
            int number = (int)(key >> 3), wire = (int)(key & 7);
            byte[] payload;
            switch (wire)
            {
                case 0:
                    int s = i;
                    ReadVarint(data, ref i);
                    payload = data[s..i];
                    break;
                case 1: payload = data[i..(i + 8)]; i += 8; break;
                case 2:
                    int len = (int)ReadVarint(data, ref i);
                    payload = data[i..(i + len)];
                    i += len;
                    break;
                case 5: payload = data[i..(i + 4)]; i += 4; break;
                default: throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
            }
            fields.Add(new Field(number, wire, payload, data[start..i]));
        }
        return fields;
    }

    private static byte[] RewriteMessage(byte[] message, Func<int, Rewriter?> pick, ref int counter)
    {
        var fields = ReadFields(message);
        using var ms = new MemoryStream(message.Length + 64);
        foreach (var f in fields)
        {
            var rewriter = f.WireType == 2 ? pick(f.Number) : null;
            if (rewriter == null)
            {
                ms.Write(f.Raw);
                continue;
            }
            WriteBytesField(ms, f.Number, rewriter(f.Payload, ref counter));
        }
        return ms.ToArray();
    }

    private static ulong ReadVarint(byte[] data, ref int i)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[i++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    private static void WriteVarint(Stream s, ulong value)
    {
        while (value >= 0x80)
        {
            s.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        s.WriteByte((byte)value);
    }

    private static void WriteBytesField(Stream s, int number, byte[] payload)
    {
        WriteVarint(s, ((ulong)number << 3) | 2);
        WriteVarint(s, (ulong)payload.Length);
        s.Write(payload);
    }

    // ---- files ----

    /// <summary>Applies all DirectML patches to a model.</summary>
    public static (byte[] Model, int AllowZero, int Gelu) MakeDirectMlCompatible(byte[] model)
    {
        var (a, allowZero) = DisableAllowZero(model);
        var (b, gelu) = ReplaceTanhGeluWithFastGelu(a);
        return (b, allowZero, gelu);
    }

    /// <summary>
    /// Writes a DirectML-compatible copy of <paramref name="modelPath"/> next to it (reusing an existing, newer copy)
    /// and returns its path. The external weight file keeps being found because it is referenced relative to the directory.
    /// </summary>
    public static string EnsureDirectMlCopy(string modelPath)
    {
        string copyPath = Path.Combine(
            Path.GetDirectoryName(modelPath)!,
            Path.GetFileNameWithoutExtension(modelPath) + $".dml{Version}.onnx");

        if (File.Exists(copyPath) && File.GetLastWriteTimeUtc(copyPath) >= File.GetLastWriteTimeUtc(modelPath))
        {
            return copyPath;
        }

        var (patched, _, _) = MakeDirectMlCompatible(File.ReadAllBytes(modelPath));
        string tmp = copyPath + ".tmp";
        File.WriteAllBytes(tmp, patched);
        File.Move(tmp, copyPath, overwrite: true);
        return copyPath;
    }
}
