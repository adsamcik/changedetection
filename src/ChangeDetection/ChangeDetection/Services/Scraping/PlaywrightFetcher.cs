using System.Diagnostics;
using ChangeDetection.Core.Interfaces;
using Microsoft.Playwright;

namespace ChangeDetection.Services.Scraping;

/// <summary>
/// Playwright-based content fetcher for JavaScript-rendered pages.
/// </summary>
public class PlaywrightFetcher : IContentFetcher, IAsyncDisposable
{
    private readonly ILogger<PlaywrightFetcher> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _concurrencyLimiter;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private bool _initialized;
    private bool _disposed;

    public PlaywrightFetcher(ILogger<PlaywrightFetcher> logger, int maxConcurrentPages = 5)
    {
        _logger = logger;
        _concurrencyLimiter = new SemaphoreSlim(maxConcurrentPages, maxConcurrentPages);
    }

    public async Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            // Use simple HTTP client for non-JS pages
            if (!options.UseJavaScript)
            {
                return await FetchWithHttpClientAsync(url, options, ct);
            }

            await EnsureInitializedAsync(ct);
            await _concurrencyLimiter.WaitAsync(ct);

            try
            {
                return await FetchWithPlaywrightAsync(url, options, stopwatch, ct);
            }
            finally
            {
                _concurrencyLimiter.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching {Url}", url);
            return new FetchResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    private async Task<FetchResult> FetchWithHttpClientAsync(string url, FetchOptions options, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

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

        var response = await client.GetAsync(url, ct);
        var html = await response.Content.ReadAsStringAsync(ct);

        stopwatch.Stop();

        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value));

        return new FetchResult
        {
            IsSuccess = response.IsSuccessStatusCode,
            Html = html,
            HttpStatusCode = (int)response.StatusCode,
            DurationMs = stopwatch.ElapsedMilliseconds,
            ResponseHeaders = responseHeaders
        };
    }

    private async Task<FetchResult> FetchWithPlaywrightAsync(string url, FetchOptions options, Stopwatch stopwatch, CancellationToken ct)
    {
        var contextOptions = new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = options.ViewportWidth,
                Height = options.ViewportHeight
            },
            UserAgent = options.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
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

        page.Response += (_, response) =>
        {
            if (response.Url == url || response.Request.IsNavigationRequest)
            {
                httpStatus = response.Status;
                responseHeaders = response.Headers.ToDictionary(h => h.Key, h => h.Value);
            }
        };

        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                Timeout = options.TimeoutSeconds * 1000,
                WaitUntil = WaitUntilState.NetworkIdle
            });

            // Wait for specific selector if provided
            if (!string.IsNullOrEmpty(options.WaitForSelector))
            {
                await page.WaitForSelectorAsync(options.WaitForSelector, new PageWaitForSelectorOptions
                {
                    Timeout = options.TimeoutSeconds * 1000
                });
            }

            // Additional delay if configured
            if (options.WaitAfterLoadMs > 0)
            {
                await Task.Delay(options.WaitAfterLoadMs, ct);
            }

            var html = await page.ContentAsync();
            byte[]? screenshot = null;

            if (options.CaptureScreenshot)
            {
                screenshot = await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Png,
                    FullPage = false
                });
            }

            stopwatch.Stop();

            return new FetchResult
            {
                IsSuccess = httpStatus >= 200 && httpStatus < 400,
                Html = html,
                Screenshot = screenshot,
                HttpStatusCode = httpStatus,
                DurationMs = stopwatch.ElapsedMilliseconds,
                ResponseHeaders = responseHeaders
            };
        }
        finally
        {
            await page.CloseAsync();
        }
    }

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
