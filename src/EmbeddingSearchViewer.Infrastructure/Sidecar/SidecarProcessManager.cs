using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EmbeddingSearchViewer.Infrastructure.Sidecar;

public class SidecarProcessManager : IAsyncDisposable
{
    private Process? _process;
    private readonly int _port;
    private readonly HttpClient _httpClient;
    private readonly string _sidecarDir;

    public string BaseUrl => $"http://127.0.0.1:{_port}";
    public bool IsRunning { get; private set; }

    public SidecarProcessManager(int port = 5000, string? sidecarDir = null)
    {
        _port = port;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        _sidecarDir = sidecarDir ?? FindSidecarDirectory();
    }

    private static string FindSidecarDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, "sidecar");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "server.py")))
            {
                return candidate;
            }
            current = current.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, "sidecar");
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // 1. Check if already running (e.g. launched manually or previously)
        if (await CheckHealthAsync(cancellationToken))
        {
            IsRunning = true;
            return;
        }

        // 2. Locate uv or python
        string command = "uv";
        string args = $"run uvicorn server:app --host 127.0.0.1 --port {_port}";

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            WorkingDirectory = Directory.Exists(_sidecarDir) ? _sidecarDir : AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception)
        {
            // Fallback to python directly if uv is not in path
            psi.FileName = "python";
            psi.Arguments = $"-m uvicorn server:app --host 127.0.0.1 --port {_port}";
            try
            {
                _process = Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to start sidecar process: {ex.Message}");
            }
        }

        // 3. Wait for readiness
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (!linkedCts.Token.IsCancellationRequested)
        {
            if (await CheckHealthAsync(linkedCts.Token))
            {
                IsRunning = true;
                return;
            }
            await Task.Delay(500, linkedCts.Token);
        }

        throw new TimeoutException("Python sidecar process did not start within the expected timeout.");
    }

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var res = await _httpClient.GetAsync($"{BaseUrl}/health", cancellationToken);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process != null && !_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // Ignore cleanup errors on shutdown
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }
        _httpClient.Dispose();
        IsRunning = false;
        GC.SuppressFinalize(this);
    }
}
