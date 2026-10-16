using System.Collections.Generic;
using System.Linq;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>One file of the model repository: its path, exact size and SHA-256 (from the pinned revision).</summary>
public sealed record ModelFileInfo(string Path, long Size, string Sha256);

/// <summary>Sizes and checksums of the files in <see cref="EmbeddingModelLocation.Revision"/>, used to verify downloads.</summary>
public static class ModelManifest
{
    private static readonly Dictionary<string, ModelFileInfo> All = new()
    {
        ["tokenizer.json"] = new ModelFileInfo("tokenizer.json", 32170510L, "4d777ef5bdc1aa36227abdfb77c3e49e7b9c892d16e1b6bda41c393504828be4"),
        ["onnx/model.onnx"] = new ModelFileInfo("onnx/model.onnx", 421421L, "bc47de15f81208a5c99e5ab10f746d5e33b51ea228b7dc0bef9c133a94f1c1c3"),
        ["onnx/model.onnx_data"] = new ModelFileInfo("onnx/model.onnx_data", 1084170240L, "9fd452bfc6916e5a92f080f8dada35598ca4512b21c65897d4c5974d5f9630a2"),
        ["onnx/vision_encoder.onnx"] = new ModelFileInfo("onnx/vision_encoder.onnx", 121253L, "a3fb44f8b5b152cfee6afe16e2cd69e774b7e1d3b822d1db1190ddf7b08b575e"),
        ["onnx/vision_encoder.onnx_data"] = new ModelFileInfo("onnx/vision_encoder.onnx_data", 671026176L, "ca8d28c99feb8129618f141978a27adae25090e0dadb68a30c235e191c01e37e"),
        ["onnx/model_fp16.onnx"] = new ModelFileInfo("onnx/model_fp16.onnx", 425780L, "a49e227d7e0e5f4ee606d79879d084264366367bc6318f344ab179e35d4fd6a4"),
        ["onnx/model_fp16.onnx_data"] = new ModelFileInfo("onnx/model_fp16.onnx_data", 542085120L, "b9dbe09415d77c8686ba6e216a244086f4ada1608238c13f2bfaa587ba627207"),
        ["onnx/vision_encoder_fp16.onnx"] = new ModelFileInfo("onnx/vision_encoder_fp16.onnx", 112253L, "3e2b6d5648a3ae61b572cc3ec0a23b13ebfda3b63e3c9c343c593636392a879c"),
        ["onnx/vision_encoder_fp16.onnx_data"] = new ModelFileInfo("onnx/vision_encoder_fp16.onnx_data", 335513088L, "9f374070808b8b96252f7d04c9c4eae34df60283473fde5a3fbf4d9205495e19"),
    };

    /// <summary>The files needed for text and image embedding at the given precision.</summary>
    public static IReadOnlyList<ModelFileInfo> For(ModelPrecision precision) =>
        EmbeddingModelLocation.RequiredFiles(precision).Select(path => All[path]).ToList();

    public static long TotalBytes(ModelPrecision precision) => For(precision).Sum(f => f.Size);
}
