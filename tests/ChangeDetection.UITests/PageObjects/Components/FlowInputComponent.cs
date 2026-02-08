using Microsoft.Playwright;

namespace ChangeDetection.UITests.PageObjects.Components;

/// <summary>
/// Page object for the FlowInput component used in the setup flow.
/// Handles binary (Yes/No), multi-choice, and freetext input modes.
/// </summary>
public class FlowInputComponent(IPage page)
{
    private ILocator FlowInput => page.Locator(".flow-input");
    private ILocator Question => page.Locator(".flow-input-question");
    private ILocator BinaryButtons => page.Locator(".flow-input-binary .choice-button");
    private ILocator ChoiceCards => page.Locator(".flow-input-choices .choice-card");
    private ILocator ConfirmButton => page.Locator(".btn-confirm");
    private ILocator FreetextArea => page.Locator(".flow-input-freetext textarea");
    private ILocator SendButton => page.Locator(".btn-send");

    /// <summary>
    /// Returns the current question text, if visible.
    /// </summary>
    public async Task<string?> GetQuestionTextAsync()
    {
        if (!await Question.IsVisibleAsync()) return null;
        return await Question.InnerTextAsync();
    }

    /// <summary>
    /// Waits for any flow input to become visible.
    /// </summary>
    public async Task WaitForInputAsync(float? timeoutMs = null)
    {
        await FlowInput.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = timeoutMs ?? 30_000
        });
    }

    /// <summary>
    /// Clicks the primary (first) binary button (typically "Yes").
    /// </summary>
    public async Task ClickPrimaryAsync()
    {
        await BinaryButtons.First.ClickAsync();
    }

    /// <summary>
    /// Clicks the secondary (last) binary button (typically "No").
    /// </summary>
    public async Task ClickSecondaryAsync()
    {
        await BinaryButtons.Last.ClickAsync();
    }

    /// <summary>
    /// Selects a multi-choice option by its visible text, then confirms.
    /// </summary>
    public async Task SelectOptionAsync(string text)
    {
        var option = ChoiceCards.Filter(new LocatorFilterOptions { HasText = text });
        await option.ClickAsync();
        await ConfirmButton.ClickAsync();
    }

    /// <summary>
    /// Selects the recommended multi-choice option, then confirms.
    /// </summary>
    public async Task SelectRecommendedAsync()
    {
        var recommended = page.Locator(".choice-card.recommended");
        await recommended.ClickAsync();
        await ConfirmButton.ClickAsync();
    }

    /// <summary>
    /// Types freetext and submits.
    /// </summary>
    public async Task TypeAndSendAsync(string text)
    {
        await FreetextArea.FillAsync(text);
        await SendButton.ClickAsync();
    }

    /// <summary>
    /// Detects which input mode is currently active.
    /// </summary>
    public async Task<FlowInputMode> GetCurrentModeAsync()
    {
        if (await page.Locator(".flow-input-binary").IsVisibleAsync())
            return FlowInputMode.Binary;
        if (await page.Locator(".flow-input-choices").IsVisibleAsync())
            return FlowInputMode.MultiChoice;
        if (await page.Locator(".flow-input-freetext").IsVisibleAsync())
            return FlowInputMode.Freetext;
        return FlowInputMode.None;
    }
}

public enum FlowInputMode
{
    None,
    Binary,
    MultiChoice,
    Freetext
}
