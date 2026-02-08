using Microsoft.Playwright;

namespace ChangeDetection.UITests.Infrastructure;

/// <summary>
/// Manages Playwright browser lifecycle for UI tests.
/// Shared across tests to avoid repeated browser launches.
/// Set HEADED=true environment variable for visible browser during debugging.
/// </summary>
public class PlaywrightFixture : IAsyncDisposable
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private readonly bool _headless;

    public PlaywrightFixture()
    {
        _headless = Environment.GetEnvironmentVariable("HEADED") != "true";
    }

    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Browser not initialized. Call InitializeAsync first.");

    public async Task InitializeAsync()
    {
        if (_playwright != null) return;

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _headless,
            Args = ["--disable-gpu", "--no-sandbox"]
        });
    }

    public async Task<IBrowserContext> CreateContextAsync()
    {
        await InitializeAsync();
        return await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 720 }
        });
    }

    public async Task<IPage> CreatePageAsync()
    {
        var context = await CreateContextAsync();
        return await context.NewPageAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
    }
}
