using Microsoft.Playwright;

namespace ChangeDetection.UITests.PageObjects.Components;

/// <summary>
/// Page object for toast notification messages.
/// </summary>
public class ToastComponent(IPage page)
{
    private ILocator ToastContainer => page.Locator(".toast-container, .toast");

    /// <summary>
    /// Waits for a toast message to appear and returns its text content.
    /// </summary>
    public async Task<string> WaitForToastAsync(float? timeoutMs = null)
    {
        var toast = ToastContainer.First;
        await toast.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs ?? 10_000
        });
        return await toast.InnerTextAsync();
    }

    /// <summary>
    /// Checks if any toast is currently visible.
    /// </summary>
    public async Task<bool> IsVisibleAsync()
    {
        return await ToastContainer.First.IsVisibleAsync();
    }
}
