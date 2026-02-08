using ChangeDetection.UITests.PageObjects.Components;
using Microsoft.Playwright;

namespace ChangeDetection.UITests.PageObjects;

/// <summary>
/// Page object for the WatchDetail page (/watches/{id}).
/// Provides access to watch configuration, actions, and change history.
/// </summary>
public class WatchDetailPage(IPage page)
{
    public ToastComponent Toast { get; } = new(page);

    // Title/header
    private ILocator TitleSection => page.Locator(".title-section");
    private ILocator WatchTitle => TitleSection.Locator("h1");
    private ILocator StatusIndicator => TitleSection.Locator(".status-indicator");

    // Action buttons
    private ILocator CheckNowButton => page.Locator("button:has-text('Check Now')");
    private ILocator ToggleEnabledButton => page.Locator("button:has-text('Pause'), button:has-text('Resume')");
    private ILocator EditLink => page.Locator("a:has-text('Edit')");

    // Configuration
    private ILocator InfoSection => page.Locator(".info-section");
    private ILocator InfoList => page.Locator(".info-list");

    // Change history
    private ILocator ChangesSection => page.Locator(".changes-section");
    private ILocator ChangeCount => ChangesSection.Locator(".stat-item strong").First;

    // Spinner for check in progress
    private ILocator CheckSpinner => CheckNowButton.Locator(".spinner-small");

    /// <summary>
    /// Waits for the watch detail page to load.
    /// </summary>
    public async Task WaitForLoadAsync(float? timeoutMs = null)
    {
        await TitleSection.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs ?? 15_000
        });
    }

    /// <summary>
    /// Gets the watch title displayed on the page.
    /// </summary>
    public async Task<string> GetTitleAsync()
    {
        return await WatchTitle.InnerTextAsync();
    }

    /// <summary>
    /// Gets the current status class (e.g., "updated", "pending", "error").
    /// </summary>
    public async Task<string> GetStatusClassAsync()
    {
        var className = await StatusIndicator.GetAttributeAsync("class") ?? "";
        // Extract status from "status-indicator active" → "active"
        return className.Replace("status-indicator", "").Trim();
    }

    /// <summary>
    /// Clicks the "Check Now" button.
    /// </summary>
    public async Task ClickCheckNowAsync()
    {
        await CheckNowButton.ClickAsync();
    }

    /// <summary>
    /// Waits for the check to complete (spinner disappears).
    /// </summary>
    public async Task WaitForCheckCompleteAsync(float? timeoutMs = null)
    {
        await CheckSpinner.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = timeoutMs ?? 30_000
        });
    }

    /// <summary>
    /// Clicks the enable/disable toggle button.
    /// </summary>
    public async Task ClickToggleEnabledAsync()
    {
        await ToggleEnabledButton.ClickAsync();
    }

    /// <summary>
    /// Gets the toggle button text ("Pause" or "Resume").
    /// </summary>
    public async Task<string> GetToggleTextAsync()
    {
        return await ToggleEnabledButton.InnerTextAsync();
    }

    /// <summary>
    /// Navigates to the edit page for this watch.
    /// </summary>
    public async Task ClickEditAsync()
    {
        await EditLink.ClickAsync();
    }

    /// <summary>
    /// Gets the total change count from the changes section.
    /// </summary>
    public async Task<int> GetChangeCountAsync()
    {
        if (!await ChangesSection.IsVisibleAsync()) return 0;

        var countText = await ChangeCount.InnerTextAsync();
        return int.TryParse(countText.Trim(), out var count) ? count : 0;
    }

    /// <summary>
    /// Gets the value of a configuration field from the info list.
    /// </summary>
    public async Task<string?> GetConfigValueAsync(string label)
    {
        var dt = InfoList.Locator($"dt:has-text('{label}')");
        if (!await dt.IsVisibleAsync()) return null;

        // The dd should be the next sibling
        var dd = dt.Locator("+ dd");
        return await dd.InnerTextAsync();
    }

    /// <summary>
    /// Gets the current page URL.
    /// </summary>
    public string GetCurrentUrl() => page.Url;
}
