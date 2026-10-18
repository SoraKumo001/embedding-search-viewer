using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>Weight precision of the ONNX files (the repository ships all of them side by side).</summary>
public enum ModelPrecision
{
    Fp32,
    Fp16,
}

/// <summary>
/// Where the EmbeddingGemma 2 ONNX files live, and which files make a complete installation.
/// The directory mirrors the Hugging Face repository layout, so a downloader can map repo paths 1:1.
/// </summary>
public static class EmbeddingModelLocation
{
    public const string RepoId = "onnx-community/embeddinggemma-2-ONNX";

    /// <summary>Pinned commit so every install gets the same files (and the index stays consistent with the model).</summary>
    public const string Revision = "daa72c51243991dfcaf9f9137d2c573d8f7790c0";

    public const string TokenizerFile = "tokenizer.json";

    /// <summary>Precision used when none is requested explicitly.</summary>
    public const ModelPrecision DefaultPrecision = ModelPrecision.Fp16;

    public static string TextModelFile(ModelPrecision p) => p switch
    {
        ModelPrecision.Fp32 => "onnx/model.onnx",
        ModelPrecision.Fp16 => "onnx/model_fp16.onnx",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static string VisionModelFile(ModelPrecision p) => p switch
    {
        ModelPrecision.Fp32 => "onnx/vision_encoder.onnx",
        ModelPrecision.Fp16 => "onnx/vision_encoder_fp16.onnx",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>Identifier stored with the index so embeddings from a different model are never mixed.</summary>
    public static string ModelTag(ModelPrecision p) => $"embeddinggemma-2-onnx-{p.ToString().ToLowerInvariant()}";

    /// <summary>Files (relative to the model directory) that must exist for text and image embedding.</summary>
    public static IReadOnlyList<string> RequiredFiles(ModelPrecision p) => new[]
    {
        TokenizerFile,
        TextModelFile(p),
        TextModelFile(p) + "_data",
        VisionModelFile(p),
        VisionModelFile(p) + "_data",
    };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EmbeddingSearchViewer",
        "models",
        "embeddinggemma-2-ONNX");

    public static string DownloadUrl(string relativePath) =>
        $"https://huggingface.co/{RepoId}/resolve/{Revision}/{relativePath}";

    private static string Combine(string directory, string relative) =>
        Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

    public static bool IsInstalled(string directory, ModelPrecision precision = DefaultPrecision) =>
        MissingFiles(directory, precision).Count == 0;

    public static IReadOnlyList<string> MissingFiles(string directory, ModelPrecision precision = DefaultPrecision) =>
        RequiredFiles(precision).Where(f => !File.Exists(Combine(directory, f))).ToList();

    /// <summary>
    /// Finds an installed model: the explicit directory if given, otherwise the per-user directory,
    /// otherwise a <c>models/embeddinggemma-2-ONNX</c> folder next to the app or in one of its parents (development layout).
    /// Returns null when nothing complete is found.
    /// </summary>
    public static string? Find(string? explicitDirectory = null, ModelPrecision precision = DefaultPrecision)
    {
        if (!string.IsNullOrEmpty(explicitDirectory))
        {
            return IsInstalled(explicitDirectory, precision) ? explicitDirectory : null;
        }

        if (IsInstalled(DefaultDirectory, precision)) return DefaultDirectory;

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "models", "embeddinggemma-2-ONNX");
            if (IsInstalled(candidate, precision)) return candidate;
            current = current.Parent;
        }
        return null;
    }
}
