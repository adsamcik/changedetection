using Microsoft.Playwright;

namespace ChangeDetection.UITests.PageObjects.Components;

/// <summary>
/// Page object for the SmartInput component on the home page.
/// Handles typing text and submitting via Enter key.
/// </summary>
public class SmartInputComponent(IPage page)
{
    private ILocator Input => page.Locator(".add-watch-form input, .add-watch-form textarea, SmartInput input").First;

    public async Task TypeAndSubmitAsync(string text)
    {
        var input = Input;
        await input.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await input.FillAsync(text);
        await input.PressAsync("Enter");
    }

    public async Task TypeAsync(string text)
    {
        var input = Input;
        await input.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await input.FillAsync(text);
    }

    public async Task<bool> IsVisibleAsync()
    {
        return await Input.IsVisibleAsync();
    }
}
