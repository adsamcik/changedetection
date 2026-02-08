using Microsoft.Playwright;
using TUnit.Core;

namespace ChangeDetection.UITests.Infrastructure;

/// <summary>
/// Base class for all Playwright UI tests.
/// Provides a fresh IPage per test with access to the shared server.
/// Captures screenshots on test failure for debugging.
/// </summary>
public abstract class UITestBase : IAsyncDisposable
{
    private static readonly PlaywrightFixture SharedPlaywright = new();
    private static readonly ServerFixture SharedServer = new();
    private static bool _initialized;
    private static readonly SemaphoreSlim InitLock = new(1, 1);

    private IBrowserContext? _context;

    protected IPage Page { get; private set; } = null!;
    protected string ServerUrl => SharedServer.BaseUrl;

    [Before(Test)]
    public async Task SetUpBase()
    {
        await EnsureInitializedAsync();

        _context = await SharedPlaywright.CreateContextAsync();
        Page = await _context.NewPageAsync();
    }

    [After(Test)]
    public async Task TearDownBase()
    {
        await CaptureScreenshotAsync();

        if (Page != null)
        {
            await Page.CloseAsync();
        }

        if (_context != null)
        {
            await _context.DisposeAsync();
            _context = null;
        }
    }

    /// <summary>
    /// Navigate to a page relative to the server root.
    /// </summary>
    protected async Task NavigateToAsync(string path)
    {
        var url = $"{ServerUrl}{path}";
        await Page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle,
            Timeout = 30_000
        });
    }

    /// <summary>
    /// Wait for an element to appear on the page.
    /// </summary>
    protected async Task<ILocator> WaitForSelectorAsync(string selector, float? timeoutMs = null)
    {
        var locator = Page.Locator(selector);
        await locator.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs ?? 10_000
        });
        return locator;
    }

    /// <summary>
    /// Wait for navigation to complete after an action.
    /// </summary>
    protected async Task WaitForNavigationAsync(Func<Task> action, string? urlPattern = null)
    {
        var waitTask = urlPattern != null
            ? Page.WaitForURLAsync(urlPattern, new PageWaitForURLOptions { Timeout = 15_000 })
            : Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 15_000 });

        await action();
        await waitTask;
    }

    internal static async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await InitLock.WaitAsync();
        try
        {
            if (_initialized) return;

            await SharedPlaywright.InitializeAsync();
            await SharedServer.StartAsync();
            _initialized = true;
        }
        finally
        {
            InitLock.Release();
        }
    }

    internal static async Task ShutdownAsync()
    {
        if (!_initialized) return;

        await SharedServer.DisposeAsync();
        await SharedPlaywright.DisposeAsync();
        _initialized = false;
    }

    private async Task CaptureScreenshotAsync()
    {
        try
        {
            var screenshotDir = Path.Combine(
                AppContext.BaseDirectory, "Screenshots");
            Directory.CreateDirectory(screenshotDir);

            var testName = "ui-test";
            var sanitized = $"{testName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            var path = Path.Combine(screenshotDir, $"{sanitized}.png");

            await Page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = path,
                FullPage = true
            });

            TestContext.Current?.OutputWriter?.WriteLine($"Screenshot saved: {path}");
        }
        catch (Exception ex)
        {
            TestContext.Current?.OutputWriter?.WriteLine($"Failed to capture screenshot: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_context != null)
        {
            await _context.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }
}
