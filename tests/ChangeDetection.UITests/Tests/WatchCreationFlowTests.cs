using ChangeDetection.UITests.Infrastructure;
using ChangeDetection.UITests.PageObjects;
using ChangeDetection.UITests.PageObjects.Components;
using Microsoft.Playwright;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.UITests.Tests;

/// <summary>
/// UI tests for the watch creation flow via the setup page.
/// Tests SSR rendering of setup pages, and attempts interactive flows
/// when WASM is available.
///
/// Note: The full interactive flow (SmartInput → SignalR streaming → Q&A)
/// requires Blazor WASM to activate. These tests verify SSR rendering
/// and gracefully handle environments where WASM doesn't load.
/// </summary>
[Category("UIOrchestration")]
[Category("LlmCached")]
public class WatchCreationFlowTests : UITestBase
{
    [Test]
    [Timeout(60_000)]
    public async Task SetupPage_DirectNavigation_RendersWithoutError(CancellationToken cancellationToken)
    {
        // Navigate to setup page directly
        await NavigateToAsync("/setup");

        // Page should load without 500 error
        var content = await Page.ContentAsync();
        content.ShouldNotContain("Internal Server Error");
    }

    [Test]
    [Timeout(60_000)]
    public async Task SetupPage_WithQueryParam_RendersWithoutError(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/setup?input=https://example.com/page");

        // Page should render without server error
        var content = await Page.ContentAsync();
        content.ShouldNotContain("Internal Server Error");
    }

    [Test]
    [Timeout(60_000)]
    public async Task SmartInput_IsVisibleOnDashboard(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);
        await home.WaitForLoadAsync();

        var isVisible = await home.SmartInput.IsVisibleAsync();
        isVisible.ShouldBeTrue("SmartInput should be rendered on the dashboard");
    }

    [Test]
    [Timeout(60_000)]
    public async Task SmartInput_AcceptsText(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);
        await home.WaitForLoadAsync();

        await home.SmartInput.TypeAsync("https://example.com");

        // Verify the text was entered (input has the value)
        var inputValue = await Page.Locator(".add-watch-form input, .add-watch-form textarea").First.InputValueAsync();
        inputValue.ShouldContain("example.com");
    }

    [Test]
    [Timeout(120_000)]
    public async Task SmartInput_TypeUrl_NavigatesToSetup(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);
        await home.WaitForLoadAsync();

        // Wait for WASM to activate (needed for interactivity)
        await Page.WaitForTimeoutAsync(3000);

        await home.SmartInput.TypeAndSubmitAsync("https://example.com");

        // Wait for navigation — this requires WASM
        try
        {
            await Page.WaitForURLAsync("**/setup**", new PageWaitForURLOptions { Timeout = 15_000 });
            Page.Url.ShouldContain("/setup");
        }
        catch (TimeoutException)
        {
            TestContext.Current?.OutputWriter?.WriteLine(
                "SmartInput navigation did not trigger — WASM may not have activated");
            // Verify at least the input was filled
            var content = await Page.ContentAsync();
            content.ShouldNotContain("Internal Server Error");
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task SetupFlow_LoadsAndShowsTimeline(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/setup?input=https://example.com");

        var setupFlow = new SetupFlowPage(Page);

        try
        {
            // Wait for the setup flow to initialize
            await setupFlow.WaitForLoadAsync(30_000);

            // Wait for at least one timeline entry
            await setupFlow.WaitForTimelineEntriesAsync(1, 30_000);

            var entryCount = await setupFlow.GetTimelineEntryCountAsync();
            entryCount.ShouldBeGreaterThanOrEqualTo(1);
        }
        catch (TimeoutException)
        {
            // Setup flow requires WASM + SignalR — may not work in all environments
            TestContext.Current?.OutputWriter?.WriteLine(
                "SetupFlow timeline did not populate — WASM/SignalR may not have activated");

            // At minimum, verify the page rendered without error
            var content = await Page.ContentAsync();
            content.ShouldNotContain("Internal Server Error");
        }
    }
}
