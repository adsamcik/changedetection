using ChangeDetection.Services.Scraping;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Scraping;

[Category("Unit")]
public class LightweightFetchHeuristicsTests
{
    [Test]
    public async Task NeedsJavaScript_ReturnsTrue_ForShortAppShell()
    {
        var html = "<html><body><div id=\"app\"></div></body></html>";

        LightweightFetchHeuristics.NeedsJavaScript(html).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NeedsJavaScript_ReturnsFalse_ForNextDataBootstrap()
    {
        var html = "<html><body><script id=\"__NEXT_DATA__\">{\"props\":{}}</script></body></html>";

        LightweightFetchHeuristics.NeedsJavaScript(html).ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NeedsJavaScript_ReturnsFalse_ForSubstantialServerRenderedHtml()
    {
        var html = "<html><body>" + new string('x', 600) + "</body></html>";

        LightweightFetchHeuristics.NeedsJavaScript(html).ShouldBeFalse();
        await Task.CompletedTask;
    }
}
