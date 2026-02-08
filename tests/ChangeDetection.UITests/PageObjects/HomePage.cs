using ChangeDetection.UITests.PageObjects.Components;
using Microsoft.Playwright;

namespace ChangeDetection.UITests.PageObjects;

/// <summary>
/// Page object for the Home/Dashboard page (/).
/// Provides access to the watch list, filters, health cards, and bulk operations.
/// </summary>
public class HomePage(IPage page)
{
    public SmartInputComponent SmartInput { get; } = new(page);
    public ToastComponent Toast { get; } = new(page);

    // Health cards
    private ILocator HealthCardTotal => page.Locator(".health-card-total");
    private ILocator HealthCardHealthy => page.Locator(".health-card-healthy");
    private ILocator HealthCardChecking => page.Locator(".health-card-checking");
    private ILocator HealthCardError => page.Locator(".health-card-error");
    private ILocator HealthCardPaused => page.Locator(".health-card-paused");

    // Watch list
    private ILocator WatchRows => page.Locator(".data-grid .grid-row");
    private ILocator DataGrid => page.Locator(".data-grid");

    // Search/filters
    private ILocator SearchInput => page.Locator("#home-search-input");
    private ILocator FilterChips => page.Locator(".filter-chip");

    // Bulk operations
    private ILocator BulkActionsBar => page.Locator(".bulk-actions-bar");

    /// <summary>
    /// Wait for the dashboard to fully load (command center visible).
    /// Falls back to checking for any main content if command-center isn't found.
    /// </summary>
    public async Task WaitForLoadAsync(float? timeoutMs = null)
    {
        var timeout = timeoutMs ?? 30_000;

        // First wait for the page to be at least partially loaded
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
            new PageWaitForLoadStateOptions { Timeout = timeout });

        // Wait for the command-center container (Blazor SSR should render this)
        try
        {
            await page.Locator(".command-center").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = timeout
            });
        }
        catch (TimeoutException)
        {
            // Fall back to checking for any body content (page at least loaded)
            await page.Locator("body").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 5_000
            });
        }
    }

    /// <summary>
    /// Gets the count displayed on a health card.
    /// </summary>
    public async Task<int> GetHealthCardCountAsync(string cardType)
    {
        var card = cardType.ToLower() switch
        {
            "total" => HealthCardTotal,
            "healthy" or "active" => HealthCardHealthy,
            "checking" => HealthCardChecking,
            "error" or "errors" => HealthCardError,
            "paused" => HealthCardPaused,
            _ => throw new ArgumentException($"Unknown card type: {cardType}")
        };

        var countText = await card.Locator(".health-card-count").InnerTextAsync();
        return int.TryParse(countText.Trim(), out var count) ? count : 0;
    }

    /// <summary>
    /// Clicks a health card to filter by that status.
    /// </summary>
    public async Task ClickHealthCardAsync(string cardType)
    {
        var card = cardType.ToLower() switch
        {
            "total" => HealthCardTotal,
            "healthy" or "active" => HealthCardHealthy,
            "checking" => HealthCardChecking,
            "error" or "errors" => HealthCardError,
            "paused" => HealthCardPaused,
            _ => throw new ArgumentException($"Unknown card type: {cardType}")
        };

        await card.ClickAsync();
    }

    /// <summary>
    /// Gets the number of watch rows currently visible.
    /// </summary>
    public async Task<int> GetWatchCountAsync()
    {
        return await WatchRows.CountAsync();
    }

    /// <summary>
    /// Types text in the search input to filter watches.
    /// </summary>
    public async Task SearchAsync(string text)
    {
        await SearchInput.FillAsync(text);
        // Allow debounce time for filtering
        await page.WaitForTimeoutAsync(500);
    }

    /// <summary>
    /// Clicks a watch row by its position (0-based index).
    /// </summary>
    public async Task ClickWatchRowAsync(int index)
    {
        await WatchRows.Nth(index).ClickAsync();
    }

    /// <summary>
    /// Gets the title text of a watch row by index.
    /// </summary>
    public async Task<string> GetWatchTitleAsync(int index)
    {
        return await WatchRows.Nth(index).Locator(".watch-title").InnerTextAsync();
    }

    /// <summary>
    /// Gets the URL text of a watch row by index.
    /// </summary>
    public async Task<string> GetWatchUrlAsync(int index)
    {
        return await WatchRows.Nth(index).Locator(".watch-url").InnerTextAsync();
    }

    /// <summary>
    /// Checks if the data grid is visible (watches loaded).
    /// </summary>
    public async Task<bool> IsDataGridVisibleAsync()
    {
        return await DataGrid.IsVisibleAsync();
    }

    /// <summary>
    /// Checks if the search input is visible.
    /// </summary>
    public async Task<bool> IsSearchVisibleAsync()
    {
        return await SearchInput.IsVisibleAsync();
    }

    /// <summary>
    /// Checks if the empty state is shown.
    /// </summary>
    public async Task<bool> IsEmptyStateAsync()
    {
        var emptyState = page.Locator(".empty-state, .no-watches");
        return await emptyState.IsVisibleAsync();
    }
}
