using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;
using ChangeDetection.Core.Interfaces;
using Microsoft.Playwright;

namespace ChangeDetection.Services.Scraping;

/// <summary>
/// Playwright-based content fetcher for JavaScript-rendered pages.
/// Provides intelligent timeout handling and detailed error diagnostics.
/// </summary>
public class PlaywrightFetcher : IContentFetcher, IAsyncDisposable
{
    private readonly ILogger<PlaywrightFetcher> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _concurrencyLimiter;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _initialized;
    private bool _disposed;

    public PlaywrightFetcher(ILogger<PlaywrightFetcher> logger, IHttpClientFactory httpClientFactory, int maxConcurrentPages = 5)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrentPages, maxConcurrentPages);
    }

    public async Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var progress = new FetchProgress();
        var timeouts = options.EffectiveTimeouts;

        using var totalTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeouts.TotalTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, totalTimeoutCts.Token);
        
        try
        {
            // Use simple HTTP client for non-JS pages
            if (!options.UseJavaScript)
            {
                return await FetchWithHttpClientAsync(url, options, progress, linkedCts.Token);
            }

            await EnsureInitializedAsync(linkedCts.Token);
            await _concurrencyLimiter.WaitAsync(linkedCts.Token);

            try
            {
                return await FetchWithPlaywrightAsync(url, options, stopwatch, progress, linkedCts.Token);
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
        }
        catch (OperationCanceledException) when (totalTimeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return CreateTimeoutResult(
                FetchErrorCategory.TotalTimeout,
                $"Total operation timeout of {timeouts.TotalTimeoutSeconds}s exceeded",
                stopwatch.ElapsedMilliseconds,
                progress,
                url,
                [
                    "The overall fetch operation took too long",
                    "Consider increasing the total timeout in fetch settings",
                    "The website may be experiencing high load"
                ]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new FetchResult
            {
                IsSuccess = false,
                ErrorCategory = FetchErrorCategory.Cancelled,
                ErrorMessage = "Operation was cancelled",
                DurationMs = stopwatch.ElapsedMilliseconds,
                Progress = progress
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching {Url}", url);
            return ClassifyException(ex, url, stopwatch.ElapsedMilliseconds, progress);
        }
    }

    /// <summary>
    /// Maximum allowed response size in bytes (10 MB).
    /// </summary>
    private const long MaxResponseSizeBytes = 10 * 1024 * 1024;

    private async Task<FetchResult> FetchWithHttpClientAsync(
        string url, 
        FetchOptions options, 
        FetchProgress progress,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var timeouts = options.EffectiveTimeouts;
        var connectionTimer = Stopwatch.StartNew();
        
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeouts.NavigationTimeoutSeconds);

        if (!string.IsNullOrEmpty(options.UserAgent))
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        }
        else
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }

        foreach (var header in options.Headers)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        try
        {
            // Use ResponseHeadersRead to detect response timeout vs content download timeout
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            
            progress.TimeToFirstByteMs = connectionTimer.ElapsedMilliseconds;
            progress.ReceivedInitialResponse = true;
            progress.PageLoadStarted = true;

            var responseHeaders = response.Headers
                .Concat(response.Content.Headers)
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

            // Check Content-Length header if available
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength > MaxResponseSizeBytes)
            {
                _logger.LogWarning("Response from {Url} exceeds size limit: {Size} bytes (limit: {Limit} bytes)", 
                    url, contentLength, MaxResponseSizeBytes);
                return new FetchResult
                {
                    IsSuccess = false,
                    ErrorCategory = FetchErrorCategory.ResponseTooLarge,
                    ErrorMessage = $"Response size ({contentLength:N0} bytes) exceeds maximum allowed size ({MaxResponseSizeBytes:N0} bytes)",
                    DetailedError = $"The server indicated the response would be {contentLength:N0} bytes, which exceeds the {MaxResponseSizeBytes:N0} byte limit.",
                    HttpStatusCode = (int)response.StatusCode,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseHeaders = responseHeaders,
                    Progress = progress,
                    Suggestions = 
                    [
                        "The page content is very large",
                        "Consider using JavaScript mode to extract only visible content",
                        "Check if this is a file download rather than a webpage"
                    ]
                };
            }

            // Read content with streaming size limit for responses without Content-Length
            var html = await ReadContentWithSizeLimitAsync(response, ct);
            if (html is null)
            {
                _logger.LogWarning("Response from {Url} exceeds size limit during streaming (limit: {Limit} bytes)", 
                    url, MaxResponseSizeBytes);
                return new FetchResult
                {
                    IsSuccess = false,
                    ErrorCategory = FetchErrorCategory.ResponseTooLarge,
                    ErrorMessage = $"Response exceeded maximum allowed size ({MaxResponseSizeBytes:N0} bytes) during download",
                    HttpStatusCode = (int)response.StatusCode,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseHeaders = responseHeaders,
                    Progress = progress,
                    Suggestions = 
                    [
                        "The page content exceeded size limits while downloading",
                        "Consider using JavaScript mode to extract only visible content"
                    ]
                };
            }

            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult
                {
                    IsSuccess = false,
                    ErrorCategory = FetchErrorCategory.HttpError,
                    ErrorMessage = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                    DetailedError = GetHttpErrorExplanation((int)response.StatusCode),
                    Html = html,
                    HttpStatusCode = (int)response.StatusCode,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseHeaders = responseHeaders,
                    Progress = progress,
                    Suggestions = GetHttpErrorSuggestions((int)response.StatusCode)
                };
            }

            return new FetchResult
            {
                IsSuccess = true,
                Html = html,
                HttpStatusCode = (int)response.StatusCode,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ResponseHeaders = responseHeaders,
                Progress = progress
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException or null)
        {
            stopwatch.Stop();
            var phase = progress.ReceivedInitialResponse ? "content download" : "server response";
            return CreateTimeoutResult(
                progress.ReceivedInitialResponse ? FetchErrorCategory.NavigationTimeout : FetchErrorCategory.ResponseTimeout,
                $"Timeout waiting for {phase}",
                stopwatch.ElapsedMilliseconds,
                progress,
                url,
                progress.ReceivedInitialResponse
                    ? ["Server responded but content download was too slow", "The page content may be very large", "Consider increasing navigation timeout"]
                    : ["Server did not respond in time", "The server may be overloaded or unreachable", "Consider increasing response timeout"]);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return ClassifyHttpException(ex, url, stopwatch.ElapsedMilliseconds, progress);
        }
    }

    /// <summary>
    /// Reads HTTP response content with a size limit to prevent memory exhaustion.
    /// Returns null if the content exceeds the size limit.
    /// </summary>
    private static async Task<string?> ReadContentWithSizeLimitAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder();
        long totalBytesRead = 0;
        int charsRead;

        while ((charsRead = await reader.ReadAsync(buffer, ct)) > 0)
        {
            // Approximate byte count (chars * 2 for UTF-16, but actual encoding varies)
            // Use a conservative estimate based on UTF-8 average
            totalBytesRead += charsRead * 2;
            
            if (totalBytesRead > MaxResponseSizeBytes)
            {
                return null; // Exceeded size limit
            }

            builder.Append(buffer, 0, charsRead);
        }

        return builder.ToString();
    }

    private async Task<FetchResult> FetchWithPlaywrightAsync(
        string url, 
        FetchOptions options, 
        Stopwatch stopwatch,
        FetchProgress progress,
        CancellationToken ct)
    {
        var timeouts = options.EffectiveTimeouts;
        
        // Apply device profile if not Desktop (Desktop uses explicit viewport/UA settings)
        var deviceSettings = options.DeviceProfile != DeviceProfile.Desktop
            ? DeviceProfileSettings.FromProfile(options.DeviceProfile)
            : null;

        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = deviceSettings?.ViewportWidth ?? options.ViewportWidth,
                Height = deviceSettings?.ViewportHeight ?? options.ViewportHeight
            },
            UserAgent = options.UserAgent
                ?? deviceSettings?.UserAgent
                ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            IsMobile = deviceSettings?.IsMobile ?? false,
            HasTouch = deviceSettings?.HasTouch ?? false,
            DeviceScaleFactor = deviceSettings?.DeviceScaleFactor ?? 1.0f
        };

        if (!string.IsNullOrEmpty(options.ProxyUrl))
        {
            contextOptions.Proxy = new Proxy { Server = options.ProxyUrl };
        }

        await using var context = await _browser!.NewContextAsync(contextOptions);
        var page = await context.NewPageAsync();

        // Set extra headers
        if (options.Headers.Count > 0)
        {
            await page.SetExtraHTTPHeadersAsync(options.Headers);
        }

        int httpStatus = 0;
        Dictionary<string, string> responseHeaders = [];
        var pendingRequests = new HashSet<string>();
        var requestCount = 0;
        var navigationTimer = Stopwatch.StartNew();

        // Track network requests for diagnostic purposes
        page.Request += (_, request) =>
        {
            Interlocked.Increment(ref requestCount);
            lock (pendingRequests)
            {
                pendingRequests.Add(request.Url);
            }
        };

        page.RequestFinished += (_, request) =>
        {
            lock (pendingRequests)
            {
                pendingRequests.Remove(request.Url);
            }
        };

        page.RequestFailed += (_, request) =>
        {
            lock (pendingRequests)
            {
                pendingRequests.Remove(request.Url);
            }
        };

        page.Response += (_, response) =>
        {
            if (response.Url == url || response.Request.IsNavigationRequest)
            {
                httpStatus = response.Status;
                responseHeaders = response.Headers.ToDictionary(h => h.Key, h => h.Value);
                
                if (!progress.ReceivedInitialResponse)
                {
                    progress.TimeToFirstByteMs = navigationTimer.ElapsedMilliseconds;
                    progress.ReceivedInitialResponse = true;
                }
            }
        };

        try
        {
            // Navigation with specific timeout
            await page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = timeouts.NavigationTimeoutSeconds * 1000,
                WaitUntil = WaitUntilState.DOMContentLoaded // Start with DOM loaded, then wait for network
            });

            progress.PageLoadStarted = true;
            progress.NavigationMs = navigationTimer.ElapsedMilliseconds;
            progress.LastKnownUrl = page.Url;

            // Separate wait for network idle with its own timeout
            var networkIdleTimer = Stopwatch.StartNew();
            try
            {
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions
                {
                    Timeout = timeouts.NetworkIdleTimeoutSeconds * 1000
                });
                progress.NetworkIdleMs = networkIdleTimer.ElapsedMilliseconds;
            }
            catch (TimeoutException)
            {
                // Capture pending request info for diagnostics
                lock (pendingRequests)
                {
                    progress.PendingRequestCount = pendingRequests.Count;
                    progress.PendingRequestUrls = [.. pendingRequests.Take(10)];
                }
                progress.NetworkRequestCount = requestCount;

                _logger.LogWarning(
                    "Network idle timeout for {Url} after {Ms}ms with {Pending} pending requests",
                    url, networkIdleTimer.ElapsedMilliseconds, progress.PendingRequestCount);

                // Continue anyway - page may be usable
            }

            progress.NetworkRequestCount = requestCount;

            // Wait for specific selector if provided
            if (!string.IsNullOrEmpty(options.WaitForSelector))
            {
                var selectorTimer = Stopwatch.StartNew();
                try
                {
                    await page.WaitForSelectorAsync(options.WaitForSelector, new PageWaitForSelectorOptions
                    {
                        Timeout = timeouts.SelectorTimeoutSeconds * 1000
                    });
                    progress.SelectorWaitMs = selectorTimer.ElapsedMilliseconds;
                }
                catch (TimeoutException)
                {
                    stopwatch.Stop();
                    return CreateTimeoutResult(
                        FetchErrorCategory.SelectorTimeout,
                        $"Selector '{options.WaitForSelector}' not found within {timeouts.SelectorTimeoutSeconds}s",
                        stopwatch.ElapsedMilliseconds,
                        progress,
                        url,
                        [
                            "The specified element selector was not found on the page",
                            "The selector may be incorrect or the element loads dynamically",
                            "Try increasing selector timeout or verifying the selector in browser DevTools"
                        ]);
                }
            }

            // Additional delay if configured
            if (options.WaitAfterLoadMs > 0)
            {
                await Task.Delay(options.WaitAfterLoadMs, ct);
            }

            var html = await page.ContentAsync();
            
            // Capture screenshots based on settings
            var screenshotResult = await CaptureScreenshotsAsync(page, options, ct);

            stopwatch.Stop();

            if (httpStatus >= 400)
            {
                return new FetchResult
                {
                    IsSuccess = false,
                    ErrorCategory = FetchErrorCategory.HttpError,
                    ErrorMessage = $"HTTP {httpStatus}",
                    DetailedError = GetHttpErrorExplanation(httpStatus),
                    Html = html,
                    Screenshot = screenshotResult.PageScreenshot,
                    ElementScreenshot = screenshotResult.ElementScreenshot,
                    ElementBoundingBox = screenshotResult.BoundingBox,
                    HttpStatusCode = httpStatus,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    ResponseHeaders = responseHeaders,
                    Progress = progress,
                    Suggestions = GetHttpErrorSuggestions(httpStatus)
                };
            }

            return new FetchResult
            {
                IsSuccess = httpStatus >= 200 && httpStatus < 400,
                Html = html,
                Screenshot = screenshotResult.PageScreenshot,
                ElementScreenshot = screenshotResult.ElementScreenshot,
                ElementBoundingBox = screenshotResult.BoundingBox,
                HttpStatusCode = httpStatus,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ResponseHeaders = responseHeaders,
                Progress = progress
            };
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            lock (pendingRequests)
            {
                progress.PendingRequestCount = pendingRequests.Count;
                progress.PendingRequestUrls = [.. pendingRequests.Take(10)];
            }
            progress.NetworkRequestCount = requestCount;

            return ClassifyPlaywrightTimeout(ex, url, stopwatch.ElapsedMilliseconds, progress, timeouts);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private FetchResult ClassifyPlaywrightTimeout(
        TimeoutException ex,
        string url,
        long durationMs,
        FetchProgress progress,
        TimeoutSettings timeouts)
    {
        var message = ex.Message.ToLowerInvariant();

        if (message.Contains("waiting until") && message.Contains("networkidle"))
        {
            var suggestions = new List<string>
            {
                "The page has continuous background network activity",
                "Some websites never become fully 'idle' due to analytics, ads, or live updates"
            };

            if (progress.PendingRequestCount > 0)
            {
                suggestions.Add($"There were {progress.PendingRequestCount} requests still pending");
            }

            suggestions.Add("Try using 'DOMContentLoaded' instead or increase network idle timeout");

            return CreateTimeoutResult(
                FetchErrorCategory.NetworkIdleTimeout,
                "Network did not become idle - page has continuous background requests",
                durationMs,
                progress,
                url,
                suggestions);
        }

        if (message.Contains("navigating to"))
        {
            return CreateTimeoutResult(
                progress.ReceivedInitialResponse ? FetchErrorCategory.NavigationTimeout : FetchErrorCategory.ResponseTimeout,
                progress.ReceivedInitialResponse 
                    ? "Page navigation timed out after receiving initial response"
                    : "No response received from server",
                durationMs,
                progress,
                url,
                progress.ReceivedInitialResponse
                    ? [
                        "Server responded but page took too long to load",
                        "The page may be very large or complex",
                        $"Consider increasing navigation timeout (current: {timeouts.NavigationTimeoutSeconds}s)"
                    ]
                    : [
                        "The server did not respond at all within the timeout period",
                        "The server may be down, blocking requests, or very slow",
                        $"Consider increasing response timeout (current: {timeouts.ResponseTimeoutSeconds}s)"
                    ]);
        }

        return CreateTimeoutResult(
            FetchErrorCategory.NavigationTimeout,
            ex.Message,
            durationMs,
            progress,
            url,
            ["A timeout occurred during page loading", "Consider increasing timeout settings"]);
    }

    private FetchResult ClassifyHttpException(
        HttpRequestException ex,
        string url,
        long durationMs,
        FetchProgress progress)
    {
        var innerException = ex.InnerException;

        // DNS resolution failure
        if (innerException is SocketException { SocketErrorCode: SocketError.HostNotFound })
        {
            return new FetchResult
            {
                IsSuccess = false,
                ErrorCategory = FetchErrorCategory.DnsResolutionFailed,
                ErrorMessage = "DNS lookup failed - hostname not found",
                DetailedError = $"Could not resolve hostname from URL: {new Uri(url).Host}",
                DurationMs = durationMs,
                Progress = progress,
                Suggestions = 
                [
                    "The domain name could not be resolved",
                    "Check that the URL is spelled correctly",
                    "The website may no longer exist"
                ]
            };
        }

        // Connection refused
        if (innerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return new FetchResult
            {
                IsSuccess = false,
                ErrorCategory = FetchErrorCategory.ConnectionFailed,
                ErrorMessage = "Connection refused - server not accepting connections",
                DetailedError = "The server actively refused the connection. The service may not be running on the expected port.",
                DurationMs = durationMs,
                Progress = progress,
                Suggestions = 
                [
                    "The server refused the connection",
                    "The website may be down or the port may be incorrect",
                    "Check if the URL uses the correct protocol (http vs https)"
                ]
            };
        }

        // Connection timeout
        if (innerException is SocketException { SocketErrorCode: SocketError.TimedOut })
        {
            return new FetchResult
            {
                IsSuccess = false,
                ErrorCategory = FetchErrorCategory.ConnectionFailed,
                ErrorMessage = "Connection timed out - server unreachable",
                DetailedError = "Could not establish a connection to the server within the timeout period.",
                DurationMs = durationMs,
                Progress = progress,
                Suggestions = 
                [
                    "Could not connect to the server",
                    "The server may be down or behind a firewall",
                    "Network connectivity issues may be preventing access"
                ]
            };
        }

        // SSL/TLS errors
        if (innerException is AuthenticationException or System.Security.Cryptography.CryptographicException)
        {
            return new FetchResult
            {
                IsSuccess = false,
                ErrorCategory = FetchErrorCategory.SslError,
                ErrorMessage = "SSL/TLS error - certificate problem",
                DetailedError = $"SSL handshake failed: {innerException.Message}",
                DurationMs = durationMs,
                Progress = progress,
                Suggestions = 
                [
                    "There is a problem with the site's SSL certificate",
                    "The certificate may be expired, self-signed, or invalid",
                    "Check if you can access the site in a browser"
                ]
            };
        }

        // Generic HTTP error
        return new FetchResult
        {
            IsSuccess = false,
            ErrorCategory = FetchErrorCategory.Unknown,
            ErrorMessage = ex.Message,
            DetailedError = $"HTTP request failed: {ex.Message}",
            DurationMs = durationMs,
            Progress = progress,
            Suggestions = 
            [
                "An unexpected error occurred while fetching the page",
                "Check the URL is accessible in a browser",
                $"Error details: {ex.InnerException?.Message ?? ex.Message}"
            ]
        };
    }

    private FetchResult ClassifyException(
        Exception ex,
        string url,
        long durationMs,
        FetchProgress progress)
    {
        if (ex is HttpRequestException httpEx)
        {
            return ClassifyHttpException(httpEx, url, durationMs, progress);
        }

        if (ex is TimeoutException timeoutEx)
        {
            return ClassifyPlaywrightTimeout(timeoutEx, url, durationMs, progress, TimeoutSettings.FromLegacyTimeout(30));
        }

        if (ex is PlaywrightException playwrightEx)
        {
            var message = playwrightEx.Message;
            
            if (message.Contains("net::ERR_NAME_NOT_RESOLVED"))
            {
                return new FetchResult
                {
                    IsSuccess = false,
                    ErrorCategory = FetchErrorCategory.DnsResolutionFailed,
                    ErrorMessage = "DNS lookup failed",
                    DetailedError = "Browser could not resolve the hostname",
                    DurationMs = durationMs,
                    Progress = progress,
                    Suggestions = ["Check URL spelling", "The domain may not exist"]
                };
            }

            if (message.Contains("net::ERR_CONNECTION_REFUSED"))
            {
                return new FetchResult
                {
                    IsSuccess = false,
                    ErrorCategory = FetchErrorCategory.ConnectionFailed,
                    ErrorMessage = "Connection refused",
                    DetailedError = "Browser could not connect to the server",
                    DurationMs = durationMs,
                    Progress = progress,
                    Suggestions = ["Server may be down", "Check port and protocol"]
                };
            }

            if (message.Contains("net::ERR_CONNECTION_TIMED_OUT"))
            {
                return new FetchResult
                {
                    IsSuccess = false,
                    ErrorCategory = FetchErrorCategory.ConnectionFailed,
                    ErrorMessage = "Connection timed out",
                    DetailedError = "Browser timed out trying to connect",
                    DurationMs = durationMs,
                    Progress = progress,
                    Suggestions = ["Server may be unreachable", "Check network connectivity"]
                };
            }

            if (message.Contains("net::ERR_CERT") || message.Contains("net::ERR_SSL"))
            {
                return new FetchResult
                {
                    IsSuccess = false,
                    ErrorCategory = FetchErrorCategory.SslError,
                    ErrorMessage = "SSL certificate error",
                    DetailedError = message,
                    DurationMs = durationMs,
                    Progress = progress,
                    Suggestions = ["Certificate may be invalid or expired", "Try accessing the site in a browser"]
                };
            }
        }

        return new FetchResult
        {
            IsSuccess = false,
            ErrorCategory = FetchErrorCategory.Unknown,
            ErrorMessage = ex.Message,
            DetailedError = $"{ex.GetType().Name}: {ex.Message}",
            DurationMs = durationMs,
            Progress = progress,
            Suggestions = ["An unexpected error occurred", "Check the URL is valid and accessible"]
        };
    }

    private static FetchResult CreateTimeoutResult(
        FetchErrorCategory category,
        string message,
        long durationMs,
        FetchProgress progress,
        string url,
        List<string> suggestions)
    {
        progress.LastKnownUrl ??= url;

        return new FetchResult
        {
            IsSuccess = false,
            ErrorCategory = category,
            ErrorMessage = message,
            DetailedError = BuildTimeoutDetailedError(category, progress, durationMs),
            DurationMs = durationMs,
            Progress = progress,
            Suggestions = suggestions
        };
    }

    private static string BuildTimeoutDetailedError(FetchErrorCategory category, FetchProgress progress, long durationMs)
    {
        var details = new List<string> { $"Total time: {durationMs}ms" };

        if (progress.TimeToFirstByteMs.HasValue)
            details.Add($"Time to first byte: {progress.TimeToFirstByteMs}ms");
        else
            details.Add("No response received from server");

        if (progress.NavigationMs.HasValue)
            details.Add($"Navigation completed in: {progress.NavigationMs}ms");

        if (progress.NetworkIdleMs.HasValue)
            details.Add($"Network idle after: {progress.NetworkIdleMs}ms");
        else if (progress.PageLoadStarted && category == FetchErrorCategory.NetworkIdleTimeout)
            details.Add("Network never became idle");

        if (progress.NetworkRequestCount > 0)
            details.Add($"Total network requests: {progress.NetworkRequestCount}");

        if (progress.PendingRequestCount > 0)
        {
            details.Add($"Pending requests at timeout: {progress.PendingRequestCount}");
            if (progress.PendingRequestUrls.Count > 0)
            {
                details.Add("Pending URLs (first 10):");
                foreach (var pendingUrl in progress.PendingRequestUrls.Take(10))
                {
                    // Truncate long URLs
                    var displayUrl = pendingUrl.Length > 80 ? pendingUrl[..77] + "..." : pendingUrl;
                    details.Add($"  - {displayUrl}");
                }
            }
        }

        return string.Join("\n", details);
    }

    /// <summary>
    /// Result of screenshot capture operations.
    /// </summary>
    private record ScreenshotCaptureResult(
        byte[]? PageScreenshot,
        byte[]? ElementScreenshot,
        ElementBoundingBox? BoundingBox);

    /// <summary>
    /// Captures screenshots based on the configured options.
    /// </summary>
    private async Task<ScreenshotCaptureResult> CaptureScreenshotsAsync(
        IPage page,
        FetchOptions options,
        CancellationToken ct)
    {
        byte[]? pageScreenshot = null;
        byte[]? elementScreenshot = null;
        ElementBoundingBox? boundingBox = null;

        var settings = options.ScreenshotSettings;
        var shouldCapturePage = options.CaptureScreenshot || settings.CaptureFullPage || settings.CaptureViewport;
        var shouldCaptureElement = settings.CaptureElement && !string.IsNullOrEmpty(options.ElementSelector);

        // Determine screenshot format
        var screenshotType = settings.Format.Equals("jpeg", StringComparison.OrdinalIgnoreCase)
            ? ScreenshotType.Jpeg
            : ScreenshotType.Png;

        // Capture full page or viewport screenshot
        if (shouldCapturePage)
        {
            try
            {
                var pageScreenshotOptions = new PageScreenshotOptions
                {
                    Type = screenshotType,
                    FullPage = settings.CaptureFullPage,
                    Quality = screenshotType == ScreenshotType.Jpeg ? settings.JpegQuality : null
                };

                // If we need to highlight the element, do it before capturing
                if (settings.HighlightElement && !string.IsNullOrEmpty(options.ElementSelector))
                {
                    await HighlightElementAsync(page, options.ElementSelector, settings);
                }

                pageScreenshot = await page.ScreenshotAsync(pageScreenshotOptions);

                // Remove highlight after screenshot
                if (settings.HighlightElement && !string.IsNullOrEmpty(options.ElementSelector))
                {
                    await RemoveHighlightAsync(page, options.ElementSelector);
                }

                _logger.LogDebug("Captured {Type} screenshot ({Size} bytes)",
                    settings.CaptureFullPage ? "full page" : "viewport",
                    pageScreenshot.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture page screenshot");
            }
        }

        // Capture element-specific screenshot
        if (shouldCaptureElement)
        {
            try
            {
                var element = await page.QuerySelectorAsync(options.ElementSelector!);
                if (element != null)
                {
                    // Get bounding box for the element
                    var box = await element.BoundingBoxAsync();
                    if (box != null)
                    {
                        boundingBox = new ElementBoundingBox
                        {
                            X = box.X,
                            Y = box.Y,
                            Width = box.Width,
                            Height = box.Height
                        };

                        // Calculate clip area with padding
                        var padding = settings.ElementPadding;
                        var clipX = Math.Max(0, box.X - padding);
                        var clipY = Math.Max(0, box.Y - padding);
                        var clipWidth = box.Width + (padding * 2);
                        var clipHeight = box.Height + (padding * 2);

                        // Capture element screenshot using clip
                        var elementScreenshotOptions = new PageScreenshotOptions
                        {
                            Type = screenshotType,
                            Quality = screenshotType == ScreenshotType.Jpeg ? settings.JpegQuality : null,
                            Clip = new Clip
                            {
                                X = (float)clipX,
                                Y = (float)clipY,
                                Width = (float)clipWidth,
                                Height = (float)clipHeight
                            }
                        };

                        elementScreenshot = await page.ScreenshotAsync(elementScreenshotOptions);

                        _logger.LogDebug("Captured element screenshot for '{Selector}' ({Size} bytes)",
                            options.ElementSelector,
                            elementScreenshot.Length);
                    }
                    else
                    {
                        _logger.LogWarning("Element '{Selector}' has no bounding box (may not be visible)",
                            options.ElementSelector);
                    }
                }
                else
                {
                    _logger.LogWarning("Element '{Selector}' not found for screenshot",
                        options.ElementSelector);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to capture element screenshot for '{Selector}'",
                    options.ElementSelector);
            }
        }

        return new ScreenshotCaptureResult(pageScreenshot, elementScreenshot, boundingBox);
    }

    /// <summary>
    /// Adds a visual highlight border around an element.
    /// </summary>
    private async Task HighlightElementAsync(IPage page, string selector, ScreenshotCaptureOptions settings)
    {
        try
        {
            var script = $@"
                (function() {{
                    var element = document.querySelector('{EscapeJsString(selector)}');
                    if (element) {{
                        element.setAttribute('data-original-outline', element.style.outline || '');
                        element.setAttribute('data-original-outline-offset', element.style.outlineOffset || '');
                        element.style.outline = '{settings.HighlightBorderWidth}px solid {settings.HighlightColor}';
                        element.style.outlineOffset = '-{settings.HighlightBorderWidth}px';
                    }}
                }})();
            ";
            await page.EvaluateAsync(script);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to highlight element '{Selector}'", selector);
        }
    }

    /// <summary>
    /// Removes the visual highlight from an element.
    /// </summary>
    private async Task RemoveHighlightAsync(IPage page, string selector)
    {
        try
        {
            var script = $@"
                (function() {{
                    var element = document.querySelector('{EscapeJsString(selector)}');
                    if (element) {{
                        element.style.outline = element.getAttribute('data-original-outline') || '';
                        element.style.outlineOffset = element.getAttribute('data-original-outline-offset') || '';
                        element.removeAttribute('data-original-outline');
                        element.removeAttribute('data-original-outline-offset');
                    }}
                }})();
            ";
            await page.EvaluateAsync(script);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to remove highlight from element '{Selector}'", selector);
        }
    }

    /// <summary>
    /// Escapes a string for safe use in JavaScript.
    /// </summary>
    private static string EscapeJsString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private static string GetHttpErrorExplanation(int statusCode) => statusCode switch
    {
        400 => "Bad Request - The server could not understand the request",
        401 => "Unauthorized - Authentication is required",
        403 => "Forbidden - Access is denied (may require login or be geo-blocked)",
        404 => "Not Found - The page does not exist at this URL",
        405 => "Method Not Allowed - GET requests are not allowed for this resource",
        408 => "Request Timeout - The server timed out waiting for the request",
        429 => "Too Many Requests - Rate limited, too many requests sent",
        500 => "Internal Server Error - The server encountered an error",
        502 => "Bad Gateway - The server received an invalid response from upstream",
        503 => "Service Unavailable - The server is temporarily overloaded or under maintenance",
        504 => "Gateway Timeout - The upstream server did not respond in time",
        _ when statusCode >= 400 && statusCode < 500 => $"Client Error ({statusCode}) - There was a problem with the request",
        _ when statusCode >= 500 => $"Server Error ({statusCode}) - The server encountered a problem",
        _ => $"HTTP {statusCode}"
    };

    private static List<string> GetHttpErrorSuggestions(int statusCode) => statusCode switch
    {
        401 => ["This page requires authentication", "Consider providing login credentials in headers"],
        403 => ["Access is forbidden - you may be blocked", "Try using a different user agent", "The site may be geo-restricted"],
        404 => ["The page was not found", "Check that the URL is correct", "The page may have been moved or deleted"],
        429 => ["Too many requests - being rate limited", "Reduce check frequency", "Add delay between checks"],
        503 => ["The server is temporarily unavailable", "The site may be under maintenance", "Try again later"],
        _ => ["Check that the URL is correct and accessible", "Try accessing the page in a browser"]
    };

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            _logger.LogInformation("Initializing Playwright browser...");
            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
            _initialized = true;
            _logger.LogInformation("Playwright browser initialized successfully");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_browser != null)
        {
            _logger.LogInformation("Closing Playwright browser...");
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _initLock.Dispose();
        _concurrencyLimiter.Dispose();
        
        GC.SuppressFinalize(this);
    }
}
