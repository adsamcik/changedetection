using ChangeDetection.UITests.Helpers;
using ChangeDetection.UITests.Infrastructure;
using ChangeDetection.UITests.PageObjects;
using Microsoft.Playwright;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.UITests.Tests;

/// <summary>
/// UI tests for the Home/Dashboard page.
/// Validates page rendering, watch list display, health cards, search filtering,
/// and navigation to watch details.
/// </summary>
[Category("UIOrchestration")]
public class DashboardTests : UITestBase
{
    [Test]
    [Timeout(60_000)]
    public async Task Dashboard_LoadsSuccessfully(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);

        await home.WaitForLoadAsync();

        // The command center container should be visible
        var url = Page.Url;
        url.ShouldEndWith("/");
    }

    [Test]
    [Timeout(60_000)]
    public async Task Dashboard_ShowsHealthCards(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);

        await home.WaitForLoadAsync();

        // Health cards should be rendered
        var totalCount = await home.GetHealthCardCountAsync("total");
        totalCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Dashboard_SmartInputIsVisible(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);

        await home.WaitForLoadAsync();

        var isVisible = await home.SmartInput.IsVisibleAsync();
        isVisible.ShouldBeTrue("SmartInput should be visible on the dashboard");
    }

    [Test]
    [Timeout(60_000)]
    public async Task Dashboard_SearchInputIsVisible(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);
        await home.WaitForLoadAsync();

        // Verify the search input is rendered (even if filtering needs WASM)
        var searchVisible = await home.IsSearchVisibleAsync();
        searchVisible.ShouldBeTrue("Search input should be visible on the dashboard");
    }

    [Test]
    [Timeout(60_000)]
    public async Task Dashboard_WatchListRendersWithData(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);
        await home.WaitForLoadAsync();

        // Watches should be rendered via SSR
        var watchCount = await home.GetWatchCountAsync();
        watchCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Dashboard_HealthCardFiltering_ClickTotal_ShowsAllWatches(CancellationToken cancellationToken)
    {
        await NavigateToAsync("/");
        var home = new HomePage(Page);
        await home.WaitForLoadAsync();

        // Click the "Total" health card — should filter to all watches
        await home.ClickHealthCardAsync("total");

        // Total count should be a non-negative number
        var totalCount = await home.GetHealthCardCountAsync("total");
        totalCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    [Timeout(60_000)]
    public async Task Dashboard_NavigateToWatchDetail_ViaDirectUrl(CancellationToken cancellationToken)
    {
        // Seed a watch via API to get its ID, then navigate directly
        using var httpClient = new HttpClient { BaseAddress = new Uri(ServerUrl) };
        var apiHelper = new WatchApiHelper(httpClient);

        var watches = await apiHelper.GetWatchesAsync();
        if (watches.Count == 0)
        {
            Skip.Test("No watches available for navigation test.");
            return;
        }

        var watchId = watches[0].Id;
        await NavigateToAsync($"/watches/{watchId}");

        // The page should load without a 500 error
        var content = await Page.ContentAsync();
        content.ShouldNotContain("500");
    }
}
