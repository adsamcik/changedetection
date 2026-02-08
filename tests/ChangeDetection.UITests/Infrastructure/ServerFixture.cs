using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ChangeDetection.UITests.Infrastructure;

/// <summary>
/// Starts the ChangeDetection app as a real process on a random port.
/// Playwright requires a real HTTP server (not TestServer) for Blazor WASM,
/// SignalR WebSockets, and static file serving.
///
/// The app is started with ASPNETCORE_ENVIRONMENT=Testing and output is
/// captured for debugging.
/// </summary>
public class ServerFixture : IAsyncDisposable
{
    private Process? _process;
    private string? _baseUrl;
    private readonly List<string> _output = [];

    /// <summary>
    /// The base URL the app is listening on (e.g., "http://127.0.0.1:12345").
    /// </summary>
    public string BaseUrl => _baseUrl ?? throw new InvalidOperationException(
        "Server not started. Call StartAsync first.");

    /// <summary>
    /// Starts the ChangeDetection server on a random available port.
    /// Waits for the server to be ready before returning.
    /// </summary>
    public async Task StartAsync()
    {
        if (_baseUrl != null) return;

        var port = GetRandomAvailablePort();
        _baseUrl = $"http://127.0.0.1:{port}";

        var projectPath = FindProjectPath();

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" --no-build --urls {_baseUrl}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                Environment =
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Testing",
                    ["ASPNETCORE_URLS"] = _baseUrl,
                    ["Logging__LogLevel__Default"] = "Warning",
                    // Disable rate limiting for UI tests — Playwright loads many static assets
                    // that count against the rate limiter
                    ["DISABLE_RATE_LIMITING"] = "true"
                }
            }
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null) _output.Add(e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) _output.Add($"[ERR] {e.Data}");
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Wait for the server to be ready
        await WaitForServerReadyAsync(_baseUrl, TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Gets captured server output for debugging.
    /// </summary>
    public IReadOnlyList<string> CapturedOutput => _output;

    private static int GetRandomAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string FindProjectPath()
    {
        // Walk up from the test output directory to find the server project
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ChangeDetection", "ChangeDetection", "ChangeDetection.csproj");
            if (File.Exists(candidate)) return candidate;

            // Also check for the solution root marker
            var slnx = Path.Combine(dir.FullName, "ChangeDetection.slnx");
            if (File.Exists(slnx))
            {
                candidate = Path.Combine(dir.FullName, "src", "ChangeDetection", "ChangeDetection", "ChangeDetection.csproj");
                if (File.Exists(candidate)) return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not find ChangeDetection.csproj. " +
            "Expected it at <repo>/src/ChangeDetection/ChangeDetection/ChangeDetection.csproj");
    }

    private static async Task WaitForServerReadyAsync(string baseUrl, TimeSpan timeout)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await httpClient.GetAsync(baseUrl);
                // Any response (even 404) means the server is up
                return;
            }
            catch (HttpRequestException)
            {
                // Server not ready yet
            }
            catch (TaskCanceledException)
            {
                // Timeout on individual request — try again
            }

            await Task.Delay(500);
        }

        throw new TimeoutException(
            $"Server at {baseUrl} did not become ready within {timeout.TotalSeconds}s");
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // Best effort
            }
        }

        _process?.Dispose();
        _process = null;
    }
}
