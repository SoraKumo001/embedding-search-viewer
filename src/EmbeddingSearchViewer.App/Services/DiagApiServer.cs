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
/// (409, nothing applied) and the body declares that it applies from the next run; decoder markers and frame tracing
/// are read per video / per frame and can change at any time. The FFmpeg worker choice is a saved setting
/// (<c>LibrarySettings.UseFfmpegWorker</c>): it is persisted through the library view model's save path and applies to
/// the videos of the next run. The same rule is reported by <c>GET /v1/options</c> in <c>effectiveFrom</c>.
/// </para>
/// </remarks>
public sealed partial class DiagApiServer : IDisposable
{
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
    private readonly LibrarySettingsStore _settingsStore;
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
        _settingsStore = services.GetRequiredService<LibrarySettingsStore>();
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


    // ---- response helpers ----

    private static object ErrorBody(string message) => new { error = message };

    private readonly record struct ApiResponse(int Status, object Body);

    private sealed record StartOutcome(bool Started, bool AlreadyRunning, string? Error, bool EngineReady, int Folders);
}
