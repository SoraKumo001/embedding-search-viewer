using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EmbeddingSearchViewer.Infrastructure.Onnx;
using Xunit;

namespace EmbeddingSearchViewer.Tests;

public class OnnxModelPatchesTests
{
    // ---- a tiny ONNX writer, enough to build test models ----

    private static byte[] Varint(ulong v)
    {
        var bytes = new System.Collections.Generic.List<byte>();
        while (v >= 0x80) { bytes.Add((byte)(v | 0x80)); v >>= 7; }
        bytes.Add((byte)v);
        return bytes.ToArray();
    }

    private static byte[] Tag(int field, int wire) => Varint(((ulong)field << 3) | (uint)wire);
    private static byte[] Len(int field, byte[] payload) => Tag(field, 2).Concat(Varint((ulong)payload.Length)).Concat(payload).ToArray();
    private static byte[] Str(int field, string s) => Len(field, Encoding.UTF8.GetBytes(s));
    private static byte[] Int(int field, long v) => Tag(field, 0).Concat(Varint((ulong)v)).ToArray();
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] StringAttr(string name, string value) => Len(5, Cat(Str(1, name), Str(4, value), Int(20, 3)));
    private static byte[] IntAttr(string name, long value) => Len(5, Cat(Str(1, name), Int(3, value), Int(20, 2)));

    private static byte[] ValueInfo(int graphField, string name, int dim)
    {
        // TypeProto.tensor_type { elem_type = FLOAT, shape { dim { dim_value } x2 } }
        var shape = Cat(Len(1, Int(1, 1)), Len(1, Int(1, dim)));
        var tensorType = Cat(Int(1, 1), Len(2, shape));
        return Len(graphField, Cat(Str(1, name), Len(2, Len(1, tensorType))));
    }

    private static byte[] Node(string op, string domain, string input, string output, params byte[][] attrs) =>
        Len(1, Cat(Str(1, input), Str(2, output), Str(3, op + "_" + output), Str(4, op), Cat(attrs), domain.Length > 0 ? Str(7, domain) : Array.Empty<byte>()));

    private static byte[] Model(params byte[][] nodes)
    {
        var graph = Cat(Cat(nodes), Str(2, "g"), ValueInfo(11, "X", 8), ValueInfo(12, "Y", 8));
        var opsets = Cat(Len(8, Cat(Str(1, ""), Int(2, 20))), Len(8, Cat(Str(1, "com.microsoft"), Int(2, 1))));
        return Cat(Int(1, 9), opsets, Len(7, graph));
    }

    private static float[] Run(byte[] model, float[] x)
    {
        using var session = new InferenceSession(model);
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor("X", new DenseTensor<float>(x, new[] { 1, x.Length })) });
        return results.First().AsTensor<float>().ToArray();
    }

    private static readonly float[] Samples = { -6f, -2.5f, -0.3f, 0f, 0.4f, 1.7f, 4.2f, 9f };

    // ---- Gelu(tanh) -> FastGelu ----

    [Fact]
    public void ReplaceTanhGelu_ProducesAModelWithTheSameOutputs()
    {
        var original = Model(Node("Gelu", "", "X", "Y", StringAttr("approximate", "tanh")));

        var (patched, replaced) = OnnxModelPatches.ReplaceTanhGeluWithFastGelu(original);

        Assert.Equal(1, replaced);
        var text = Encoding.ASCII.GetString(patched);
        Assert.Contains("FastGelu", text);
        Assert.DoesNotContain("approximate", text);
        var expected = Run(original, Samples);
        var actual = Run(patched, Samples);
        for (int i = 0; i < Samples.Length; i++) Assert.Equal(expected[i], actual[i], 4);
    }

    [Fact]
    public void ReplaceTanhGelu_HandlesSeveralNodesAndLeavesOtherNodesAlone()
    {
        var original = Model(
            Node("Gelu", "", "X", "A", StringAttr("approximate", "tanh")),
            Node("Relu", "", "A", "B"),
            Node("Gelu", "", "B", "Y", StringAttr("approximate", "tanh")));

        var (patched, replaced) = OnnxModelPatches.ReplaceTanhGeluWithFastGelu(original);

        Assert.Equal(2, replaced);
        var expected = Run(original, Samples);
        var actual = Run(patched, Samples);
        for (int i = 0; i < Samples.Length; i++) Assert.Equal(expected[i], actual[i], 4);
    }

    [Fact]
    public void ReplaceTanhGelu_LeavesExactGeluAlone()
    {
        var original = Model(Node("Gelu", "", "X", "Y", StringAttr("approximate", "none")));

        var (patched, replaced) = OnnxModelPatches.ReplaceTanhGeluWithFastGelu(original);

        Assert.Equal(0, replaced);
        Assert.Equal(original, patched);
    }

    // ---- allowzero ----

    [Fact]
    public void DisableAllowZero_FlipsOnlyAttributesThatAreOn_AndKeepsTheSize()
    {
        byte[] On = Cat(Encoding.ASCII.GetBytes("allowzero"), new byte[] { 0x18, 1, 0xA0, 0x01, 0x02 });
        byte[] Off = Cat(Encoding.ASCII.GetBytes("allowzero"), new byte[] { 0x18, 0, 0xA0, 0x01, 0x02 });
        var model = Cat(new byte[] { 9, 9 }, On, new byte[] { 7 }, Off, On);

        var (patched, replaced) = OnnxModelPatches.DisableAllowZero(model);

        Assert.Equal(2, replaced);
        Assert.Equal(model.Length, patched.Length);
        Assert.Equal(Cat(new byte[] { 9, 9 }, Off, new byte[] { 7 }, Off, Off), patched);
    }

    [Fact]
    public void DisableAllowZero_LeavesModelsWithoutTheAttributeUnchanged()
    {
        var model = new byte[] { 1, 2, 3, 4, 5 };

        var (patched, replaced) = OnnxModelPatches.DisableAllowZero(model);

        Assert.Equal(0, replaced);
        Assert.Equal(model, patched);
    }

    // ---- files ----

    [Fact]
    public void EnsureDirectMlCopy_WritesAPatchedCopyNextToTheModelAndReusesIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "esv_patch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var model = Path.Combine(dir, "vision.onnx");
            var original = Model(Node("Gelu", "", "X", "Y", StringAttr("approximate", "tanh")));
            File.WriteAllBytes(model, original);

            var copy = OnnxModelPatches.EnsureDirectMlCopy(model);

            Assert.Equal(Path.Combine(dir, $"vision.dml{OnnxModelPatches.Version}.onnx"), copy);
            Assert.Contains("FastGelu", Encoding.ASCII.GetString(File.ReadAllBytes(copy)));
            Assert.Equal(original, File.ReadAllBytes(model)); // original untouched

            var stamp = File.GetLastWriteTimeUtc(copy);
            Assert.Equal(copy, OnnxModelPatches.EnsureDirectMlCopy(model));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(copy)); // not rewritten
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- the real models ----

    [FactIfModelInstalled]
    public void RealVisionEncoder_AfterPatching_HasNoStandardGeluLeft()
    {
        var path = Path.Combine(EmbeddingModelLocation.Find()!, EmbeddingModelLocation.VisionModelFile(EmbeddingModelLocation.DefaultPrecision));

        var (patched, allowZero, gelu) = OnnxModelPatches.MakeDirectMlCompatible(File.ReadAllBytes(path));

        Assert.Equal(128, allowZero);
        Assert.Equal(16, gelu);
        Assert.Contains("FastGelu", Encoding.ASCII.GetString(patched));
    }
}
