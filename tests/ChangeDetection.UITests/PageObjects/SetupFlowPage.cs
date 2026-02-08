using ChangeDetection.UITests.PageObjects.Components;
using Microsoft.Playwright;

namespace ChangeDetection.UITests.PageObjects;

/// <summary>
/// Page object for the SetupFlow page (/setup).
/// Handles the interactive LLM-powered watch creation flow including
/// SignalR streaming, state timeline, and user input interactions.
/// </summary>
public class SetupFlowPage(IPage page)
{
    public FlowInputComponent FlowInput { get; } = new(page);
    public ToastComponent Toast { get; } = new(page);

    // State timeline
    private ILocator Timeline => page.Locator(".state-timeline");
    private ILocator TimelineEntries => page.Locator(".timeline-entry");
    private ILocator ThinkingEntries => page.Locator(".timeline-entry.thinking");
    private ILocator CompletedEntries => page.Locator(".timeline-entry.completed");
    private ILocator CurrentEntry => page.Locator(".timeline-entry.current");

    // Status overlays
    private ILocator SuccessOverlay => page.Locator(".flow-success");
    private ILocator ErrorOverlay => page.Locator(".flow-error");
    private ILocator ConnectingIndicator => page.Locator(".flow-connecting");

    // Success actions
    private ILocator ViewWatchButton => SuccessOverlay.Locator(".btn-primary");
    private ILocator CreateAnotherButton => SuccessOverlay.Locator(".btn-secondary");

    // Error actions
    private ILocator RetryButton => ErrorOverlay.Locator(".btn-retry");
    private ILocator BackButton => ErrorOverlay.Locator(".btn-back");

    /// <summary>
    /// Waits for the setup flow to initialize (timeline visible).
    /// </summary>
    public async Task WaitForLoadAsync(float? timeoutMs = null)
    {
        // Wait for connecting indicator to disappear and timeline to appear
        await ConnectingIndicator.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Hidden,
            Timeout = timeoutMs ?? 15_000
        });
    }

    /// <summary>
    /// Gets the number of timeline entries currently displayed.
    /// </summary>
    public async Task<int> GetTimelineEntryCountAsync()
    {
        return await TimelineEntries.CountAsync();
    }

    /// <summary>
    /// Waits for the timeline to have at least the specified number of entries.
    /// Useful for waiting for SignalR streaming to deliver updates.
    /// </summary>
    public async Task WaitForTimelineEntriesAsync(int minCount, float? timeoutMs = null)
    {
        var timeout = timeoutMs ?? 30_000;
        await page.WaitForFunctionAsync(
            $"() => document.querySelectorAll('.timeline-entry').length >= {minCount}",
            null,
            new PageWaitForFunctionOptions { Timeout = timeout });
    }

    /// <summary>
    /// Waits for a question to appear in the flow (input becomes visible).
    /// </summary>
    public async Task WaitForQuestionAsync(float? timeoutMs = null)
    {
        await FlowInput.WaitForInputAsync(timeoutMs);
    }

    /// <summary>
    /// Checks if the thinking indicator is currently visible.
    /// </summary>
    public async Task<bool> IsThinkingAsync()
    {
        return await ThinkingEntries.Last.IsVisibleAsync();
    }

    /// <summary>
    /// Gets the summary text of a timeline entry by index.
    /// </summary>
    public async Task<string> GetTimelineSummaryAsync(int index)
    {
        return await TimelineEntries.Nth(index).Locator(".timeline-summary").InnerTextAsync();
    }

    /// <summary>
    /// Waits for the success overlay to appear (watch created successfully).
    /// </summary>
    public async Task WaitForSuccessAsync(float? timeoutMs = null)
    {
        await SuccessOverlay.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs ?? 60_000
        });
    }

    /// <summary>
    /// Clicks "View Watch" on the success overlay.
    /// </summary>
    public async Task ClickViewWatchAsync()
    {
        await ViewWatchButton.ClickAsync();
    }

    /// <summary>
    /// Clicks "Create Another" on the success overlay.
    /// </summary>
    public async Task ClickCreateAnotherAsync()
    {
        await CreateAnotherButton.ClickAsync();
    }

    /// <summary>
    /// Checks if the error overlay is visible.
    /// </summary>
    public async Task<bool> IsErrorVisibleAsync()
    {
        return await ErrorOverlay.IsVisibleAsync();
    }

    /// <summary>
    /// Clicks retry on the error overlay.
    /// </summary>
    public async Task ClickRetryAsync()
    {
        await RetryButton.ClickAsync();
    }

    /// <summary>
    /// Gets the current page URL (useful for checking navigation after success).
    /// </summary>
    public string GetCurrentUrl() => page.Url;
}
