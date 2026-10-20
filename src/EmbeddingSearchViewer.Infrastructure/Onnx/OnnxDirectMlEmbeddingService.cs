using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Core.Models;

namespace EmbeddingSearchViewer.Infrastructure.Onnx;

/// <summary>
/// In-process EmbeddingGemma 2 (text + image) using ONNX Runtime.
/// Text goes straight through the language model; an image is first turned into soft tokens by the vision encoder,
/// which the language model then places at the image placeholder tokens of its input.
/// With a DirectML device, the vision encoder and the language model pass for images run on the GPU (the language model
/// with a fixed padded length, because DirectML re-plans the graph whenever an input shape changes). Short text
/// queries stay on the CPU, which answers them in a few milliseconds without touching the GPU.
/// </summary>
public class OnnxDirectMlEmbeddingService : IEmbeddingService, IDisposable
{
    private const int HiddenSize = 512;            // width of the soft tokens fed to the language model
    private const int MaxTextTokens = 1024;
    private const long BosId = 2, EosId = 1;
    private const long BeginImageId = 255999, ImageId = 258880, EndImageId = 258882;

    private const string QueryPrefix = "task: search result | query: ";

    // <bos> <|image> + up to 280 soft tokens + <image|> <eos>: every image sequence is padded to this length on the GPU.
    private const int GpuImageSequenceLength = Gemma4ImagePreprocessor.MaxSoftTokens + 4;

    private readonly string _modelsDir;
    private readonly ModelPrecision _precision;
    private readonly int _deviceId;
    private readonly Tokenizers.DotNet.Tokenizer _tokenizer;
    private readonly InferenceSession _textSession;
    private readonly object _gate = new();

    // A DirectML session must not run concurrently, so vision inference is serialised.
    private readonly object _visionRunLock = new();

    // The native tokenizer is not safe to call concurrently (the app encodes queries and indexes files on different threads).
    private static readonly object TokenizerLock = new();

    // A DirectML session must not run concurrently, so each GPU session has its own lock.
    private readonly object _languageModelRunLock = new();

    private InferenceSession? _visionSession;
    private InferenceSession? _gpuLanguageModel;
    private bool _visionOnGpu;
    private bool _languageModelOnGpu;
    private bool _disposed;

    /// <param name="modelsDir">Directory laid out like the Hugging Face repository; located automatically when null.</param>
    /// <param name="deviceId">DirectML adapter index for the vision encoder, or a negative value to use the CPU only.</param>
    /// <param name="precision">Which weight files to use.</param>
    /// <remarks>
    /// Creating the engine reads the model files and builds the inference sessions, which takes a moment. The engine is
    /// therefore created in the background (see <see cref="EmbeddingServiceHost"/>), so the app starts right away and
    /// waits for the engine only when it is actually used.
    /// </remarks>
    public OnnxDirectMlEmbeddingService(string? modelsDir = null, int deviceId = 0, ModelPrecision precision = EmbeddingModelLocation.DefaultPrecision)
    {
        _precision = precision;
        _modelsDir = EmbeddingModelLocation.Find(modelsDir, precision)
            ?? throw new FileNotFoundException(
                $"EmbeddingGemma 2 model files not found (looked in {modelsDir ?? EmbeddingModelLocation.DefaultDirectory}).");
        _deviceId = deviceId;
        _visionOnGpu = deviceId >= 0;
        _languageModelOnGpu = deviceId >= 0;

        if (deviceId >= 0)
        {
            // The adapter list is DXGI order, which is what DirectML indexes devices by, so deviceId maps to a named GPU.
            Console.WriteLine($"[DirectML] Adapters: {DirectMlAdapters.Summary()}");
            Console.WriteLine($"[DirectML] deviceId {deviceId} -> {DirectMlAdapters.Describe(deviceId)}");
        }

        lock (TokenizerLock)
        {
            _tokenizer = new Tokenizers.DotNet.Tokenizer(PathOf(EmbeddingModelLocation.TokenizerFile));
        }
        _textSession = new InferenceSession(PathOf(EmbeddingModelLocation.TextModelFile(precision)), new SessionOptions());
    }

    /// <summary>Timing of the image-embedding stages, for finding the bottleneck.</summary>
    public EmbeddingStageStats Stats { get; } = new();

    // ---- diagnostics (verification only; nothing below changes any inference shape, batch size or precision) ----

    private const int PatchHistogramBinWidth = 100;
    private readonly int[] _patchHistogram = new int[Gemma4ImagePreprocessor.MaxPatches / PatchHistogramBinWidth + 1];
    private long _patchCount, _patchSum;

    /// <summary>Records the patch count of one prepared image, so its distribution can be logged (diagnostics only).</summary>
    private void RecordPatchCount(int numPatches)
    {
        Interlocked.Increment(ref _patchCount);
        Interlocked.Add(ref _patchSum, numPatches);
        int bin = Math.Min(numPatches / PatchHistogramBinWidth, _patchHistogram.Length - 1);
        Interlocked.Increment(ref _patchHistogram[bin]);
    }

    /// <summary>
    /// One console line with the image-embedding stage timings and the patch-count distribution so far, so that the
    /// bottleneck (and whether fixed image shapes would help) can be judged from a real indexing run. Read-only.
    /// </summary>
    public string DescribeStats()
    {
        var s = Stats.Snapshot();
        double total = s.PrepareMs + s.VisionWaitMs + s.VisionRunMs + s.LanguageModelMs;
        var text = new StringBuilder();
        text.Append($"images={s.Images} prepare={s.PrepareMs:F0}ms visionWait={s.VisionWaitMs:F0}ms visionRun={s.VisionRunMs:F0}ms languageModel={s.LanguageModelMs:F0}ms perImage={(s.Images == 0 ? 0 : total / s.Images):F1}ms");

        long count = Interlocked.Read(ref _patchCount);
        if (count > 0)
        {
            double avg = (double)Interlocked.Read(ref _patchSum) / count;
            text.Append($"; patches avg={avg:F0} maxPatches={Gemma4ImagePreprocessor.MaxPatches} histogram=");
            bool first = true;
            for (int i = 0; i < _patchHistogram.Length; i++)
            {
                int bucket = Volatile.Read(ref _patchHistogram[i]);
                if (bucket == 0) continue;
                if (!first) text.Append(' ');
                first = false;
                text.Append($"{i * PatchHistogramBinWidth}-{i * PatchHistogramBinWidth + PatchHistogramBinWidth - 1}:{bucket}");
            }
        }
        return text.ToString();
    }

    /// <summary>
    /// How many images of a batch are processed at once. The GPU stage is serialised, so more than one image only
    /// helps by letting decoding/resizing and the CPU language model of other images overlap with it.
    /// </summary>
    public int MaxImageParallelism { get; set; } = 4;

    /// <summary>True while the vision encoder runs on the GPU (it falls back to the CPU if the GPU path fails).</summary>
    public bool IsVisionOnGpu => _visionOnGpu;

    /// <summary>True while the image language-model pass runs on the GPU (false until first used or after a fallback).</summary>
    public bool IsLanguageModelOnGpu => _languageModelOnGpu;

    private string PathOf(string relative) => Path.Combine(_modelsDir, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Creates a GPU session from the DirectML-compatible copy of a model, or null if that is not possible.</summary>
    private InferenceSession? TryCreateGpuSession(string modelPath, string what)
    {
        try
        {
            using var options = new SessionOptions();
            options.AppendExecutionProvider_DML(_deviceId);
            options.EnableMemoryPattern = false;
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            // The published graphs use operators DirectML cannot run (see OnnxModelPatches); use the equivalent patched copy.
            Console.WriteLine($"[DirectML] Creating the GPU {what} session on deviceId {_deviceId} ({DirectMlAdapters.Describe(_deviceId)}).");
            return new InferenceSession(OnnxModelPatches.EnsureDirectMlCopy(modelPath), options);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[DirectML] Could not create the GPU {what} session ({ex.Message}). Using the CPU.");
            return null;
        }
    }

    private InferenceSession CreateVisionSession()
    {
        string modelPath = PathOf(EmbeddingModelLocation.VisionModelFile(_precision));

        if (_visionOnGpu)
        {
            var gpu = TryCreateGpuSession(modelPath, "vision");
            if (gpu != null) return gpu;
            _visionOnGpu = false;
        }

        return new InferenceSession(modelPath, new SessionOptions());
    }

    /// <summary>The GPU language-model session, or null when it is unavailable (the CPU session is used instead).</summary>
    private InferenceSession? GpuLanguageModel
    {
        get
        {
            lock (_gate)
            {
                if (!_languageModelOnGpu) return null;
                _gpuLanguageModel ??= TryCreateGpuSession(PathOf(EmbeddingModelLocation.TextModelFile(_precision)), "language model");
                if (_gpuLanguageModel == null) _languageModelOnGpu = false;
                return _gpuLanguageModel;
            }
        }
    }

    private InferenceSession VisionSession
    {
        get
        {
            lock (_gate)
            {
                return _visionSession ??= CreateVisionSession();
            }
        }
    }

    /// <summary>
    /// The engine is ready as soon as it exists: the text session is built in the constructor and the vision session
    /// is built when the first image is embedded (which does not change the readiness of the engine).
    /// </summary>
    public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!_disposed);
    }

    public Task<float[]> GenerateQueryEmbeddingAsync(
        string query,
        string modality = EmbeddingModalities.Text,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        // Inference is CPU/GPU bound and synchronous; keep it off the caller's (UI) thread.
        return Task.Run(() =>
        {
            if (string.Equals(modality, EmbeddingModalities.Image, StringComparison.OrdinalIgnoreCase) && File.Exists(query))
            {
                return EncodeImage(query, truncateDim);
            }
            return EncodeText(QueryPrefix + query, truncateDim);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<float[]>> GenerateBatchEmbeddingsAsync(
        IEnumerable<EmbeddingItemInput> items,
        int truncateDim = 768,
        CancellationToken cancellationToken = default)
    {
        var itemList = items.ToList();
        return Task.Run<IReadOnlyList<float[]>>(() =>
        {
            var results = new float[itemList.Count][];
            var textIndexes = new List<int>();
            var texts = new List<string>();
            var images = new List<(int Index, string Path)>();

            for (int i = 0; i < itemList.Count; i++)
            {
                var item = itemList[i];

                string? imagePath = ImagePathOf(item);
                if (imagePath != null)
                {
                    images.Add((i, imagePath));
                }
                else
                {
                    string text = item.Text ?? string.Empty;
                    string title = string.IsNullOrWhiteSpace(item.Title) ? "none" : item.Title;
                    textIndexes.Add(i);
                    texts.Add($"title: {title} | text: {text}");
                }
            }

            EncodeImages(images, results, truncateDim, cancellationToken);

            if (texts.Count > 0)
            {
                var encoded = EncodeTextBatch(texts, truncateDim, useGpu: true);
                for (int k = 0; k < textIndexes.Count; k++) results[textIndexes[k]] = encoded[k];
            }

            return results;
        }, cancellationToken);
    }

    /// <summary>
    /// Embeds several images with overlapping stages: while one image occupies the GPU, others are being decoded
    /// and resized, or run through the CPU language model.
    /// </summary>
    private void EncodeImages(List<(int Index, string Path)> images, float[][] results, int truncateDim, CancellationToken cancellationToken)
    {
        if (images.Count == 0) return;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(MaxImageParallelism, images.Count)),
            CancellationToken = cancellationToken
        };

        try
        {
            Parallel.ForEach(images, options, image => results[image.Index] = EncodeImage(image.Path, truncateDim, skipBlank: true));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count > 0)
        {
            // Report the real failure rather than the wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }
    }

    /// <summary>The image file an item should be embedded from, or null when it is a text item.</summary>
    private static string? ImagePathOf(EmbeddingItemInput item)
    {
        bool isImageLike = item.Type.Equals(EmbeddingModalities.Image, StringComparison.OrdinalIgnoreCase)
            || item.Type.Equals(EmbeddingModalities.VideoSegment, StringComparison.OrdinalIgnoreCase);
        if (!isImageLike) return null;

        foreach (var candidate in new[] { item.Path, item.ThumbnailPath })
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---- text ----

    private long[] TokenizeText(string text)
    {
        uint[] encoded;
        lock (TokenizerLock)
        {
            encoded = _tokenizer.Encode(text);
        }
        var ids = encoded.Select(id => (long)id).ToList();

        // The tokenizer may or may not add the special tokens; the model expects exactly <bos> ... <eos>.
        if (ids.Count > 0 && ids[0] == BosId) ids.RemoveAt(0);
        if (ids.Count > 0 && ids[^1] == EosId) ids.RemoveAt(ids.Count - 1);
        if (ids.Count > MaxTextTokens - 2) ids.RemoveRange(MaxTextTokens - 2, ids.Count - (MaxTextTokens - 2));

        ids.Insert(0, BosId);
        ids.Add(EosId);
        return ids.ToArray();
    }

    public float[] EncodeText(string text, int truncateDim = 768) => EncodeTextBatch(new[] { text }, truncateDim)[0];

    // The language model keeps per-token buffers for all 24 layers (tens of KB per token), so one run must stay small:
    // a document with hundreds of pages would otherwise try to allocate gigabytes at once.
    internal const int MaxTokensPerTextRun = 4096;
    internal const int MaxSequencesPerTextRun = 32;

    /// <summary>
    /// Groups sequences (by index) into runs whose padded size stays within the token budget. Similar lengths are
    /// grouped together so little padding is wasted.
    /// </summary>
    internal static List<int[]> PlanTextRuns(IReadOnlyList<int> lengths, int maxTokensPerRun = MaxTokensPerTextRun, int maxSequencesPerRun = MaxSequencesPerTextRun)
    {
        var runs = new List<int[]>();
        var current = new List<int>();
        foreach (int index in Enumerable.Range(0, lengths.Count).OrderBy(i => lengths[i]))
        {
            // sorted ascending, so the sequence being added is the longest of the run
            bool fits = current.Count < maxSequencesPerRun && (current.Count + 1) * (long)lengths[index] <= maxTokensPerRun;
            if (!fits && current.Count > 0)
            {
                runs.Add(current.ToArray());
                current.Clear();
            }
            current.Add(index);
        }
        if (current.Count > 0) runs.Add(current.ToArray());
        return runs;
    }

    // Fixed shapes for documents on the GPU: DirectML re-plans the graph for every new input shape (about 300 ms), so
    // sequences are padded up to one of a few lengths, and each run always has the same number of rows.
    // length x rows stays within the same token budget as the CPU runs.
    private static readonly (int Length, int Rows)[] GpuTextBuckets = { (64, 32), (128, 32), (256, 16), (512, 8), (1024, 4) };

    // Fewer texts than this are not worth waking the GPU path for (a bucket is padded with filler rows).
    private const int MinTextsForGpu = 4;

    internal static (int Length, int Rows) BucketFor(int tokens) =>
        GpuTextBuckets.FirstOrDefault(b => tokens <= b.Length) is { Length: > 0 } bucket ? bucket : GpuTextBuckets[^1];

    /// <param name="useGpu">Run on the GPU language model with fixed shapes (for batches of documents). Short queries stay on the CPU.</param>
    public IReadOnlyList<float[]> EncodeTextBatch(IReadOnlyList<string> texts, int truncateDim = 768, bool useGpu = false)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();

        var tokenized = texts.Select(TokenizeText).ToList();
        if (useGpu && texts.Count >= MinTextsForGpu && GpuLanguageModel != null)
        {
            var onGpu = TryEncodeOnGpu(tokenized, truncateDim);
            if (onGpu != null) return onGpu;
        }

        var results = new float[texts.Count][];
        foreach (var run in PlanTextRuns(tokenized.Select(t => t.Length).ToList()))
        {
            var embeddings = RunLanguageModel(run.Select(i => tokenized[i]).ToList(), imageFeatures: null, truncateDim);
            for (int k = 0; k < run.Length; k++) results[run[k]] = embeddings[k];
        }
        return results;
    }

    /// <summary>Embeds tokenized texts on the GPU, or returns null (after switching the GPU off) if the GPU fails.</summary>
    private float[][]? TryEncodeOnGpu(IReadOnlyList<long[]> tokenized, int truncateDim)
    {
        var gpu = GpuLanguageModel;
        if (gpu == null) return null;

        var results = new float[tokenized.Count][];
        try
        {
            foreach (var group in Enumerable.Range(0, tokenized.Count).GroupBy(i => BucketFor(tokenized[i].Length)))
            {
                var (length, rows) = group.Key;
                foreach (var chunk in group.Chunk(rows))
                {
                    var sequences = chunk.Select(i => tokenized[i]).ToList();
                    while (sequences.Count < rows) sequences.Add(new[] { BosId, EosId }); // filler rows keep the shape fixed

                    IReadOnlyList<float[]> embeddings;
                    lock (_languageModelRunLock)
                    {
                        embeddings = RunLanguageModel(gpu, sequences, null, truncateDim, padToLength: length);
                    }
                    for (int k = 0; k < chunk.Length; k++) results[chunk[k]] = embeddings[k];
                }
            }
            return results;
        }
        catch (OnnxRuntimeException ex)
        {
            Console.WriteLine($"[DirectML] Text inference failed on the GPU ({ex.Message}). Falling back to the CPU.");
            lock (_gate)
            {
                _languageModelOnGpu = false;
                _gpuLanguageModel?.Dispose();
                _gpuLanguageModel = null;
            }
            return null;
        }
    }

    // ---- image ----

    /// <param name="skipBlank">Return an empty array (no embedding) for an almost single-colour image instead of embedding it.</param>
    public float[] EncodeImage(string imagePath, int truncateDim = 768, bool skipBlank = false)
    {
        long t0 = Stopwatch.GetTimestamp();
        var prepared = Gemma4ImagePreprocessor.Prepare(imagePath, detectBlank: skipBlank);
        Stats.AddPrepare(Stopwatch.GetTimestamp() - t0);
        if (prepared.IsNearlyBlank) return Array.Empty<float>();
        RecordPatchCount(prepared.NumPatches);

        var softTokens = RunVisionEncoder(prepared);
        int n = softTokens.Dimensions[0];

        // <bos> <|image> <|image|> x n <image|> <eos>
        var ids = new long[n + 4];
        ids[0] = BosId;
        ids[1] = BeginImageId;
        for (int i = 0; i < n; i++) ids[2 + i] = ImageId;
        ids[n + 2] = EndImageId;
        ids[n + 3] = EosId;

        long t1 = Stopwatch.GetTimestamp();
        var embedding = RunImageLanguageModel(ids, softTokens, truncateDim);
        Stats.AddLanguageModel(Stopwatch.GetTimestamp() - t1);
        Stats.AddImage();
        return embedding;
    }

    private DenseTensor<float> RunVisionEncoder(PreparedImage image)
    {
        try
        {
            return RunVisionEncoderOnce(image);
        }
        catch (OnnxRuntimeException ex) when (_visionOnGpu)
        {
            Console.WriteLine($"[DirectML] Vision inference failed on the GPU ({ex.Message}). Falling back to the CPU.");
            lock (_gate)
            {
                if (_visionOnGpu)
                {
                    _visionOnGpu = false;
                    _visionSession?.Dispose();
                    _visionSession = null;
                }
            }
            return RunVisionEncoderOnce(image);
        }
    }

    private DenseTensor<float> RunVisionEncoderOnce(PreparedImage image)
    {
        // DirectML re-plans the graph whenever an input shape changes, so on the GPU every image is sent at the full,
        // fixed patch budget (the -1 padding positions are ignored by the model; results match). The CPU skips the padding.
        int n = _visionOnGpu ? image.MaxPatches : image.NumPatches;
        var pixelValues = new DenseTensor<float>(image.Patches.AsMemory(0, n * image.PatchDim), new[] { 1, n, image.PatchDim });
        var positionIds = new DenseTensor<long>(image.PositionIds.AsMemory(0, n * 2), new[] { 1, n, 2 });

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("pixel_values", pixelValues),
            NamedOnnxValue.CreateFromTensor("pixel_position_ids", positionIds)
        };

        long waitStart = Stopwatch.GetTimestamp();
        lock (_visionRunLock)
        {
            long runStart = Stopwatch.GetTimestamp();
            Stats.AddVisionWait(runStart - waitStart);
            using var results = VisionSession.Run(inputs);
            var features = results.First(r => r.Name == "image_features").AsTensor<float>();

            // Copy out of the session-owned buffer before the results are disposed.
            var copy = new DenseTensor<float>(features.Dimensions.ToArray());
            features.ToArray().CopyTo(copy.Buffer.Span);
            Stats.AddVisionRun(Stopwatch.GetTimestamp() - runStart);
            return copy;
        }
    }

    // ---- language model ----

    private float[] RunImageLanguageModel(long[] ids, DenseTensor<float> softTokens, int truncateDim)
    {
        var gpu = GpuLanguageModel;
        if (gpu != null)
        {
            try
            {
                lock (_languageModelRunLock)
                {
                    return RunLanguageModel(gpu, new List<long[]> { ids }, softTokens, truncateDim, padToLength: GpuImageSequenceLength)[0];
                }
            }
            catch (OnnxRuntimeException ex)
            {
                Console.WriteLine($"[DirectML] Language-model inference failed on the GPU ({ex.Message}). Falling back to the CPU.");
                lock (_gate)
                {
                    _languageModelOnGpu = false;
                    _gpuLanguageModel?.Dispose();
                    _gpuLanguageModel = null;
                }
            }
        }

        return RunLanguageModel(_textSession, new List<long[]> { ids }, softTokens, truncateDim)[0];
    }

    private IReadOnlyList<float[]> RunLanguageModel(IReadOnlyList<long[]> sequences, DenseTensor<float>? imageFeatures, int truncateDim) =>
        RunLanguageModel(_textSession, sequences, imageFeatures, truncateDim);

    private static IReadOnlyList<float[]> RunLanguageModel(
        InferenceSession session, IReadOnlyList<long[]> sequences, DenseTensor<float>? imageFeatures, int truncateDim, int padToLength = 0)
    {
        int batch = sequences.Count;
        int maxLen = Math.Max(padToLength, sequences.Max(s => s.Length));

        var inputIds = new long[batch * maxLen];
        var attentionMask = new long[batch * maxLen];
        for (int b = 0; b < batch; b++)
        {
            var seq = sequences[b];
            for (int s = 0; s < seq.Length; s++)
            {
                inputIds[b * maxLen + s] = seq[s];
                attentionMask[b * maxLen + s] = 1L;
            }
            // remaining positions stay 0 (<pad>, masked out)
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds.AsMemory(), new[] { batch, maxLen })),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask.AsMemory(), new[] { batch, maxLen })),
            NamedOnnxValue.CreateFromTensor("image_features", imageFeatures ?? new DenseTensor<float>(new[] { 0, HiddenSize })),
            NamedOnnxValue.CreateFromTensor("video_features", new DenseTensor<float>(new[] { 0, HiddenSize })),
            NamedOnnxValue.CreateFromTensor("audio_features", new DenseTensor<float>(new[] { 0, HiddenSize })),
        };

        using var results = session.Run(inputs, new[] { "sentence_embedding" });
        var tensor = results.First().AsTensor<float>();
        int dim = tensor.Dimensions[1];
        var flat = tensor.ToArray();

        var rows = new List<float[]>(batch);
        for (int b = 0; b < batch; b++)
        {
            var row = new float[dim];
            Array.Copy(flat, b * dim, row, 0, dim);
            rows.Add(PostProcessEmbedding(row, truncateDim));
        }
        return rows;
    }

    /// <summary>Matryoshka truncation followed by L2 re-normalisation (the model output is already normalised at full size).</summary>
    internal static float[] PostProcessEmbedding(float[] raw, int truncateDim)
    {
        int dim = Math.Min(truncateDim, raw.Length);
        var result = new float[dim];
        Array.Copy(raw, result, dim);

        float norm = MathF.Sqrt(TensorPrimitives.SumOfSquares(result));
        if (norm > 0)
        {
            for (int i = 0; i < result.Length; i++) result[i] /= norm;
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textSession.Dispose();
        _visionSession?.Dispose();
        _gpuLanguageModel?.Dispose();
        lock (TokenizerLock)
        {
            _tokenizer.Dispose();
        }
    }
}
