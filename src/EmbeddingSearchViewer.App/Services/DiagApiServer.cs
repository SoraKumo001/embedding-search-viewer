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

namespace EmbeddingSearchViewer_App.Services;

/// <summary>
/// Localhost diagnostics API for the verification lane: a TcpListener on 127.0.0.1 with an ephemeral port, a tiny
/// hand-written HTTP/1.1 server (no extra NuGet package) and a random 128-bit bearer token that lives in memory only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security.</b> The socket is bound to the loopback address only, every connection whose remote end is not a
/// loopback address is dropped before a single byte is read, and every endpoint requires
/// <c>Authorization: Bearer &lt;token&gt;</c> or <c>?token=&lt;token&gt;</c>. The token is generated at start-up, is
/// printed on the console (that is the only place it can be read from) and is never written to a file; the crash log
/// gets a line with the port and a pointer to the console instead. This is a local verification aid, not a public
/// service: there is no TLS, no rate limiting and no user handling.
/// </para>
/// <para>
/// <b>Indexing.</b> Start and stop go through <see cref="LibraryViewModel"/>, exactly like the buttons on the library
/// page: the start path loads the folders when the page was never opened (the page's own <c>InitializeAsync</c>),
/// refreshes the index statistics (which is what fills <c>IndexModelWarning</c>, the model tag check), then calls
/// <see cref="LibraryViewModel.StartIndexingAsync"/>, which also waits for the engine to be ready and starts the
/// worker. The worker is never called directly from here. The UI thread is used for both, because the view model
/// updates bound properties.
/// </para>
/// <para>
/// <b>Run-bound knobs.</b> Video parallelism and the FFmpeg permit are read by the worker when a run starts, so they
/// cannot be changed while a run is in progress. A PUT that asks for one of them during a run is rejected as a whole
/// (409, nothing applied) and the body declares that it applies from the next run; the other three switches (decoder
/// markers, frame tracing, the FFmpeg worker) are read per video / per frame and can change at any time. The same rule
/// is reported by <c>GET /v1/options</c> in <c>effectiveFrom</c>.
/// </para>
/// </remarks>
public sealed class DiagApiServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>A request that hangs (a start waiting for a slow engine, a stop waiting for a batch) must still end.</summary>
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Path to the allowed methods; used for the 405 answer.</summary>
    private static readonly Dictionary<string, string> Routes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["/v1"] = "GET",
        ["/v1/options"] = "GET, PUT",
        ["/v1/index/start"] = "POST",
        ["/v1/index/stop"] = "POST",
        ["/v1/progress"] = "GET",
        ["/v1/stats"] = "GET",
    };

    private readonly IServiceProvider _services;
    private readonly IUiDispatcher _dispatcher;
    private readonly RunDiagnosticsOptions _options;
    private readonly IngestionPipelineWorker _worker;
    private readonly Action<IngestionProgress> _progressHandler;

    /// <summary>Serializes start/stop so two requests cannot both pass the "is it running" check at once.</summary>
    private readonly SemaphoreSlim _controlGate = new(1, 1);

    private readonly object _gate = new();
    private readonly string _token;

    /// <summary>The last progress report of the worker; null until the first run reports anything.</summary>
    private volatile IngestionProgress? _lastProgress;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public DiagApiServer(
        IServiceProvider services,
        IUiDispatcher dispatcher,
        RunDiagnosticsOptions options,
        IngestionPipelineWorker worker)
    {
        _services = services;
        _dispatcher = dispatcher;
        _options = options;
        _worker = worker;

        // 128 bits from the OS RNG: 32 hex characters. Memory only, for the lifetime of the process.
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        // Progress is a push from the worker; keeping the last report is enough for GET /v1/progress.
        _progressHandler = progress => _lastProgress = progress;
        _worker.ProgressChanged += _progressHandler;
    }

    /// <summary>The base URL of the running server ("http://127.0.0.1:port"), or null when it is not started.</summary>
    public string? BaseUrl { get; private set; }

    /// <summary>
    /// Binds the loopback ephemeral port and starts accepting. The port and how to get the token are logged once to the
    /// console and to the crash log; the token itself only goes to the console. Failures are logged, never thrown: the
    /// app must start without the diagnostics lane.
    /// </summary>
    public void Start()
    {
        int port;
        lock (_gate)
        {
            if (_listener != null) return;

            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
            }
            catch (Exception ex)
            {
                CrashLog.Write("DiagApi.Start (could not bind 127.0.0.1)", ex);
                return;
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";

            var token = _cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(listener, token));
        }

        Console.WriteLine($"[DiagApi] listening on http://127.0.0.1:{port}/v1 - Authorization: Bearer {_token}");
        CrashLog.Write(
            $"[DiagApi] listening on http://127.0.0.1:{port}/v1 (the bearer token is printed on the start-up console and is never written to a file)",
            null);
    }

    /// <summary>Stops accepting and closes the listener. Safe to call more than once, and when Start never ran.</summary>
    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
            BaseUrl = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed: nothing left to cancel.
        }

        try
        {
            listener?.Stop();
        }
        catch (Exception)
        {
            // Closing the socket must never be what fails.
        }
    }

    public void Dispose()
    {
        Stop();
        _worker.ProgressChanged -= _progressHandler;
        _controlGate.Dispose();
    }

    // ---- the socket loop ----

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break; // Stop() closed the listener
            }
            catch (SocketException)
            {
                break;
            }

            // One task per connection: a start or stop request can wait a long time and must not hold up the others.
            _ = Task.Run(() => HandleClientAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                // Loopback only: anything else is dropped before the request is read.
                if (client.Client.RemoteEndPoint is not IPEndPoint remote || !IPAddress.IsLoopback(remote.Address)) return;

                client.NoDelay = true;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ClientTimeout);

                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request == null) return; // empty, malformed or oversized: the connection is just closed

                await HandleRequestAsync(stream, request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The client went away or the server is stopping.
            }
            catch (IOException)
            {
                // The client closed the connection while the answer was being written.
            }
            catch (Exception ex)
            {
                CrashLog.Write("DiagApi connection", ex);
            }
        }
    }

    /// <summary>Reads one HTTP/1.1 request (headers and, with Content-Length, the body); null when it cannot be used.</summary>
    private static async Task<DiagRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var head = new MemoryStream();
        int headerEnd;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read == 0) return null;
            head.Write(buffer, 0, read);
            headerEnd = IndexOfHeaderEnd(head.GetBuffer(), (int)head.Length);
            if (headerEnd >= 0) break;
            if (head.Length > MaxHeaderBytes) return null;
        }

        string headerText = Encoding.Latin1.GetString(head.GetBuffer(), 0, headerEnd);
        string[] lines = headerText.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2) return null;

        // An absolute-form target (http://127.0.0.1:port/v1/...) is accepted as well as the usual origin-form.
        string target = requestLine[1];
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var absolute)) return null;
            target = absolute.PathAndQuery;
        }

        string path = target;
        string query = string.Empty;
        int question = target.IndexOf('?');
        if (question >= 0)
        {
            path = target[..question];
            query = target[(question + 1)..];
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        if (headers.ContainsKey("Transfer-Encoding")) return null; // only Content-Length bodies are served

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out string? lengthText)
            && (!int.TryParse(lengthText, out contentLength) || contentLength < 0))
        {
            return null;
        }
        if (contentLength > MaxBodyBytes) return null;

        var body = new byte[contentLength];
        int headerLength = headerEnd + 4;
        int buffered = Math.Min((int)head.Length - headerLength, contentLength);
        if (buffered > 0) Array.Copy(head.GetBuffer(), headerLength, body, 0, buffered);

        int offset = buffered;
        while (offset < contentLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }

        return new DiagRequest(requestLine[0].ToUpperInvariant(), path, ParseQuery(query), headers, body);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        }
        return -1;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (query.Length == 0) return values;

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals < 0) continue;

            string key = Unescape(pair[..equals]);
            if (key.Length > 0) values[key] = Unescape(pair[(equals + 1)..]);
        }
        return values;
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    // ---- routing ----

    private async Task HandleRequestAsync(NetworkStream stream, DiagRequest request, CancellationToken token)
    {
        if (!IsAuthorized(request))
        {
            await WriteAsync(stream, 401, "Unauthorized",
                JsonSerializer.Serialize(
                    ErrorBody("missing or invalid token: send 'Authorization: Bearer <token>' or ?token=<token>"), JsonOptions),
                token).ConfigureAwait(false);
            return;
        }

        string path = request.Path.TrimEnd('/');
        if (path.Length == 0) path = "/";

        ApiResponse response;
        try
        {
            response = (request.Method, path) switch
            {
                ("GET", "/") => new ApiResponse(200, RoutesBody()),
                ("GET", "/v1") => new ApiResponse(200, RoutesBody()),
                ("GET", "/v1/options") => new ApiResponse(200, OptionsBody()),
                ("PUT", "/v1/options") => await PutOptionsAsync(request, token).ConfigureAwait(false),
                ("POST", "/v1/index/start") => await StartIndexingAsync(token).ConfigureAwait(false),
                ("POST", "/v1/index/stop") => await StopIndexingAsync(token).ConfigureAwait(false),
                ("GET", "/v1/progress") => new ApiResponse(200, ProgressBody()),
                ("GET", "/v1/stats") => new ApiResponse(200, StatsBody()),
                _ => Routes.TryGetValue(path, out string? allowed)
                    ? new ApiResponse(405, ErrorBody($"{request.Method} is not allowed here; use {allowed}"))
                    : new ApiResponse(404, ErrorBody("unknown endpoint; GET /v1 lists the routes")),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CrashLog.Write($"DiagApi {request.Method} {request.Path}", ex);
            response = new ApiResponse(500, ErrorBody("internal error: " + ex.Message));
        }

        string json = JsonSerializer.Serialize(response.Body, JsonOptions);
        await WriteAsync(stream, response.Status, ReasonOf(response.Status), json, token).ConfigureAwait(false);
    }

    private bool IsAuthorized(DiagRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out string? header))
        {
            const string prefix = "Bearer ";
            if (header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && TokenEquals(header[prefix.Length..].Trim()))
            {
                return true;
            }
        }

        return request.Query.TryGetValue("token", out string? query) && TokenEquals(query);
    }

    /// <summary>Compares in constant time, so the answer does not leak how much of the token was right.</summary>
    private bool TokenEquals(string candidate)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(_token));

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
        useFfmpegWorker = _options.UseFfmpegWorker,
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
            useFfmpegWorker = "immediate",
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
            _options.UseFfmpegWorker = useFfmpegWorker.Value;
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
            useFfmpegWorker = _options.UseFfmpegWorker,
            enableDiagApi = _options.EnableDiagApi,
            running = _worker.IsRunning,
            effectiveFrom = new
            {
                videoParallelism = "nextRun",
                ffmpegPermit = "nextRun",
                ignoreDecoderMarkers = "immediate",
                frameTracing = "immediate",
                useFfmpegWorker = "immediate",
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

    // ---- response helpers ----

    private static object ErrorBody(string message) => new { error = message };

    private static string ReasonOf(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        500 => "Internal Server Error",
        _ => "Unknown",
    };

    private static async Task WriteAsync(NetworkStream stream, int status, string reason, string json, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        string head = $"HTTP/1.1 {status} {reason}\r\n"
            + "Content-Type: application/json; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + (status == 401 ? "WWW-Authenticate: Bearer\r\n" : string.Empty)
            + "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head).AsMemory(), token).ConfigureAwait(false);
        await stream.WriteAsync(body.AsMemory(), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private sealed record DiagRequest(
        string Method,
        string Path,
        Dictionary<string, string> Query,
        Dictionary<string, string> Headers,
        byte[] Body);

    private readonly record struct ApiResponse(int Status, object Body);

    private sealed record StartOutcome(bool Started, bool AlreadyRunning, string? Error, bool EngineReady, int Folders);
}
