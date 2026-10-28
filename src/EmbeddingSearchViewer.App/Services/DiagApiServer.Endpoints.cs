using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbeddingSearchViewer.Core.Interfaces;
using EmbeddingSearchViewer.Infrastructure.Ingestion;
using EmbeddingSearchViewer.Infrastructure.Library;
using EmbeddingSearchViewer_App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using static EmbeddingSearchViewer_App.Services.DiagHttp;

namespace EmbeddingSearchViewer_App.Services;

public sealed partial class DiagApiServer
{
    // ---- endpoints ----

    private static object RoutesBody() => new
    {
        endpoints = new object[]
        {
            new { method = "GET", path = "/v1/options", description = "current run diagnostics options and when each applies" },
            new { method = "PUT", path = "/v1/options", description = "partial merge of the options (clamped, unknown keys ignored)" },
            new { method = "POST", path = "/v1/index/start", description = "start indexing through the library view model (engine and model tag checked)" },
            new { method = "POST", path = "/v1/index/stop", description = "stop indexing through the library view model" },
            new { method = "GET", path = "/v1/progress", description = "the last IngestionProgress of the worker" },
            new { method = "GET", path = "/v1/stats", description = "raw video, decoder and stage totals" },
        },
        auth = "Authorization: Bearer <token> or ?token=<token>",
    };

    private object OptionsBody() => new
    {
        videoParallelism = _options.VideoParallelism,
        ffmpegPermit = _options.FfmpegPermit,
        ignoreDecoderMarkers = _options.IgnoreDecoderMarkers,
        frameTracing = _options.FrameTracing,
        // Saved with the library settings (the same value the library page's checkbox writes), not a diagnostics value.
        useFfmpegWorker = _settingsStore.Load().UseFfmpegWorker,
        // Turning this on through the API cannot be done: this answer only exists while the API already listens, so a
        // PUT of true is a no-op and the switch is really turned on from the library page (or ESV_DIAG_API=1). A PUT of
        // false does stop the API: the answer is still written, then the listener closes.
        enableDiagApi = _options.EnableDiagApi,
        running = _worker.IsRunning,
        effectiveFrom = new
        {
            videoParallelism = "nextRun",
            ffmpegPermit = "nextRun",
            ignoreDecoderMarkers = "immediate",
            frameTracing = "immediate",
            // The library setting is read when a run starts, like the decoder choice.
            useFfmpegWorker = "nextRun",
            enableDiagApi = "immediate",
        },
        ranges = new
        {
            videoParallelism = new { min = RunDiagnosticsOptions.MinVideoParallelism, max = RunDiagnosticsOptions.MaxVideoParallelism },
            ffmpegPermit = new { min = RunDiagnosticsOptions.MinFfmpegPermit, max = RunDiagnosticsOptions.MaxFfmpegPermit },
        },
    };

    /// <summary>
    /// Partial merge of the diagnostics switches. Values outside the accepted range are clamped by the store, keys
    /// that are not switches are reported in <c>ignored</c> and left alone. A run-bound knob (parallelism, permit)
    /// during a run rejects the whole request with 409 and applies nothing; retry it after the stop for the next run.
    /// <c>enableDiagApi</c> is applied at once: true is a no-op (the API is answering, so it is already on; it is turned
    /// on from the library page or <c>ESV_DIAG_API=1</c>) and false stops the listener after this answer is written.
    /// <c>useFfmpegWorker</c> is a saved library setting: it is written through the view model's save path and applies
    /// to the videos of the next run.
    /// </summary>
    private async Task<ApiResponse> PutOptionsAsync(DiagRequest request, CancellationToken token)
    {
        int? videoParallelism = null;
        int? ffmpegPermit = null;
        bool? ignoreDecoderMarkers = null;
        bool? frameTracing = null;
        bool? useFfmpegWorker = null;
        bool? enableDiagApi = null;
        var ignored = new List<string>();

        try
        {
            using var document = JsonDocument.Parse(request.Body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ApiResponse(400, ErrorBody("the body must be a JSON object with the options to change"));
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                switch (property.Name.ToLowerInvariant())
                {
                    case "videoparallelism":
                        if (TryReadInt(property.Value, out int parallelism)) videoParallelism = parallelism;
                        else ignored.Add(property.Name);
                        break;
                    case "ffmpegpermit":
                        if (TryReadInt(property.Value, out int permit)) ffmpegPermit = permit;
                        else ignored.Add(property.Name);
                        break;
                    case "ignoredecodermarkers":
                        if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) ignoreDecoderMarkers = property.Value.GetBoolean();
                        else ignored.Add(property.Name);
                        break;
                    case "frametracing":
                        if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) frameTracing = property.Value.GetBoolean();
                        else ignored.Add(property.Name);
                        break;
                    case "useffmpegworker":
                        if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) useFfmpegWorker = property.Value.GetBoolean();
                        else ignored.Add(property.Name);
                        break;
                    case "enablediagapi":
                        if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) enableDiagApi = property.Value.GetBoolean();
                        else ignored.Add(property.Name);
                        break;
                    default:
                        ignored.Add(property.Name); // unknown keys are ignored, not rejected
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            return new ApiResponse(400, ErrorBody("invalid JSON: " + ex.Message));
        }

        // Run-bound knobs cannot be re-applied mid-run: reject the whole request and say when it would apply.
        if ((videoParallelism.HasValue || ffmpegPermit.HasValue) && _worker.IsRunning)
        {
            return new ApiResponse(409, new
            {
                error = "videoParallelism and ffmpegPermit cannot change while indexing is running; nothing was applied",
                effectiveFrom = "nextRun",
                running = true,
                current = new { videoParallelism = _options.VideoParallelism, ffmpegPermit = _options.FfmpegPermit },
            });
        }

        var applied = new List<string>();
        if (videoParallelism.HasValue)
        {
            _options.VideoParallelism = videoParallelism.Value; // the store clamps to its range
            applied.Add("videoParallelism");
        }
        if (ffmpegPermit.HasValue)
        {
            _options.FfmpegPermit = ffmpegPermit.Value;
            applied.Add("ffmpegPermit");
        }
        if (ignoreDecoderMarkers.HasValue)
        {
            _options.IgnoreDecoderMarkers = ignoreDecoderMarkers.Value;
            applied.Add("ignoreDecoderMarkers");
        }
        if (frameTracing.HasValue)
        {
            _options.FrameTracing = frameTracing.Value;
            applied.Add("frameTracing");
        }
        if (useFfmpegWorker.HasValue)
        {
            // A saved setting, not a diagnostics value: it goes through the library view model, whose property writes
            // the settings file (the same path as the checkbox). The page may never have been opened, so its own
            // start-up is run first: that loads the folders the save would otherwise write back as empty.
            bool value = useFfmpegWorker.Value;
            await _dispatcher.EnqueueAsync(async () =>
            {
                var viewModel = _services.GetRequiredService<LibraryViewModel>();
                if (viewModel.TargetFolders.Count == 0) await viewModel.InitializeAsync();
                viewModel.UseFfmpegWorker = value;
            }).ConfigureAwait(false);
            applied.Add("useFfmpegWorker");
        }
        if (enableDiagApi.HasValue)
        {
            // The app follows the store (RunDiagnosticsOptions.EnableDiagApiChanged) and starts or stops the listener.
            // A PUT of true cannot take effect here: the API is answering this request, so it is already on. A PUT of
            // false stops the listener, and the app defers that until this answer has been written.
            _options.EnableDiagApi = enableDiagApi.Value;
            applied.Add("enableDiagApi");
        }

        // The values are read back from the store, so a clamped value is what the caller sees.
        return new ApiResponse(200, new
        {
            applied,
            ignored,
            videoParallelism = _options.VideoParallelism,
            ffmpegPermit = _options.FfmpegPermit,
            ignoreDecoderMarkers = _options.IgnoreDecoderMarkers,
            frameTracing = _options.FrameTracing,
            useFfmpegWorker = _settingsStore.Load().UseFfmpegWorker,
            enableDiagApi = _options.EnableDiagApi,
            running = _worker.IsRunning,
            effectiveFrom = new
            {
                videoParallelism = "nextRun",
                ffmpegPermit = "nextRun",
                ignoreDecoderMarkers = "immediate",
                frameTracing = "immediate",
                useFfmpegWorker = "nextRun",
                enableDiagApi = "immediate",
            },
        });
    }

    private static bool TryReadInt(JsonElement value, out int result)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out result)) return true;
        result = 0;
        return false;
    }

    /// <summary>
    /// Starts indexing on the same path as the library page's button: the statistics (and with them the model tag
    /// warning) are refreshed, then <see cref="LibraryViewModel.StartIndexingAsync"/> checks the engine and the tag and
    /// starts the worker. The worker is never touched directly.
    /// </summary>
    private async Task<ApiResponse> StartIndexingAsync(CancellationToken token)
    {
        await _controlGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            StartOutcome? outcome = null;
            await _dispatcher.EnqueueAsync(async () =>
            {
                // Resolved on the UI thread: the view model updates bound properties from here on.
                var viewModel = _services.GetRequiredService<LibraryViewModel>();
                if (viewModel.IsIndexing)
                {
                    outcome = new StartOutcome(true, true, null, viewModel.IsEngineReady, viewModel.TargetFolders.Count);
                    return;
                }

                // The library page is not the start page: when it has never been opened, the folders and settings have
                // not been loaded yet. Loading them is the page's own start-up method, not a second path to the worker.
                if (viewModel.TargetFolders.Count == 0) await viewModel.InitializeAsync();

                // Same checks as the page: the full stats refresh fills IndexModelWarning via CheckIndexModel.
                await viewModel.RefreshLibraryStatsAsync();
                await viewModel.StartIndexingAsync();

                string? error = viewModel.IsIndexing
                    ? null
                    : viewModel.ErrorMessage
                      ?? (viewModel.TargetFolders.Count == 0
                          ? "no folders to index (add one on the library page)"
                          : "indexing did not start");

                outcome = new StartOutcome(viewModel.IsIndexing, false, error, viewModel.IsEngineReady, viewModel.TargetFolders.Count);
            }).ConfigureAwait(false);

            StartOutcome result = outcome!;
            return result.Started
                ? new ApiResponse(200, new
                {
                    started = true,
                    alreadyRunning = result.AlreadyRunning,
                    folders = result.Folders,
                    engineReady = result.EngineReady,
                })
                : new ApiResponse(409, new
                {
                    started = false,
                    error = result.Error,
                    folders = result.Folders,
                    engineReady = result.EngineReady,
                });
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <summary>Stops indexing through <see cref="LibraryViewModel.StopIndexingAsync"/>, the same path as the button.</summary>
    private async Task<ApiResponse> StopIndexingAsync(CancellationToken token)
    {
        await _controlGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            bool wasRunning = _worker.IsRunning;
            await _dispatcher.EnqueueAsync(async () =>
            {
                var viewModel = _services.GetRequiredService<LibraryViewModel>();
                await viewModel.StopIndexingAsync();
            }).ConfigureAwait(false);

            return new ApiResponse(200, new { stopped = true, wasRunning, running = _worker.IsRunning });
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <summary>The last <see cref="IngestionProgress"/> of the worker, as-is (a synthetic idle one before the first report).</summary>
    private object ProgressBody()
        => _lastProgress ?? new IngestionProgress(0, 0, string.Empty, _worker.IsRunning);

    /// <summary>
    /// Raw totals only: the display strings of <see cref="VideoIngestStats"/> and <see cref="IngestionStageStats"/> are
    /// not used, so the numbers can be compared and charted without parsing the UI wording.
    /// </summary>
    private object StatsBody()
    {
        var video = _worker.VideoStats;
        var stage = _worker.StageStats;

        var byDecoder = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (decoder, totals) in VideoDecoderStats.Shared.GetRaw().OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            byDecoder[decoder] = new
            {
                frames = totals.Frames,
                seconds = totals.Seconds,
                msPerFrame = totals.Frames > 0 ? totals.Seconds * 1000.0 / totals.Frames : (double?)null,
            };
        }

        double stageSeconds = stage.EmbedSeconds + stage.SaveSeconds;
        return new
        {
            video = new
            {
                videos = video.Videos,
                segments = video.Segments,
                extractSeconds = video.ExtractSeconds,
                embedSeconds = video.EmbedSeconds,
                videoSeconds = video.VideoSeconds,
            },
            byDecoder,
            stage = new
            {
                embedSeconds = stage.EmbedSeconds,
                saveSeconds = stage.SaveSeconds,
                saveShare = stageSeconds > 0 ? stage.SaveSeconds / stageSeconds : 0.0, // 0..1
                saveSharePercent = stage.SaveSharePercent,
                hasSamples = stage.HasSamples,
                embedCalls = stage.EmbedCalls,
                embedItems = stage.EmbedItems,
                saveCalls = stage.SaveCalls,
                saveItems = stage.SaveItems,
            },
            running = _worker.IsRunning,
        };
    }
}
