using ChangeDetection.Core.Pipeline.Setup;
using Shouldly;

namespace ChangeDetection.Core.Tests.Pipeline.Setup;

[Category("Unit")]
public class PlatformDetectorTests
{
    private readonly IPlatformDetector _sut = new PlatformDetector();

    [Test]
    public async Task DetectFromUrl_ShouldRecognizeWorkday()
    {
        var detected = _sut.DetectFromUrl("https://acme.myworkdayjobs.com/en-US/Careers");

        detected.ShouldNotBeNull();
        detected.PlatformId.ShouldBe("workday");
        detected.Confidence.ShouldBeGreaterThan(0.9f);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DetectFromUrl_ShouldRecognizeWordPressApiPath()
    {
        var detected = _sut.DetectFromUrl("https://example.com/wp-json/wp/v2/posts");

        detected.ShouldNotBeNull();
        detected.PlatformId.ShouldBe("wordpress");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DetectFromContent_ShouldPreferHigherConfidenceSignal()
    {
        const string html = "<html><head><script>Shopify.theme = { name: 'Dawn' };</script></head></html>";

        var detected = _sut.DetectFromContent("https://store.example.com/products/widget", html);

        detected.ShouldNotBeNull();
        detected.PlatformId.ShouldBe("shopify");
        detected.Confidence.ShouldBeGreaterThan(0.9f);
        await Task.CompletedTask;
    }

    [Test]
    public async Task DetectFromUrl_ShouldNotTreatGenericProductsPathAsShopify()
    {
        var detected = _sut.DetectFromUrl("https://example.com/products/widget");

        detected.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task DetectFromContent_ShouldNotMatchGreenhouseFromGreenhouseGasText()
    {
        const string html = """
            <html>
            <head>
                <link rel="stylesheet" href="/styles/site.css">
            </head>
            <body>
                <p>This sustainability report tracks greenhouse gas emissions.</p>
            </body>
            </html>
            """;

        var detected = _sut.DetectFromContent("https://example.com/report", html);

        detected.ShouldBeNull();
        await Task.CompletedTask;
    }
}
