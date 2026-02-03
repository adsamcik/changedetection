using ChangeDetection.Services.Content;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

[Category("Unit")]
public class ContentExtractorTests
{
    private readonly ContentExtractor _sut = new();

    [Test]
    public async Task ExtractText_WithSimpleHtml_ReturnsTextContent()
    {
        // Arrange
        var html = "<html><body><p>Hello World</p></body></html>";

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldBe("Hello World");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithScriptTags_RemovesScriptContent()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <p>Visible Text</p>
                <script>var x = 'hidden';</script>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldContain("Visible Text");
        result.ShouldNotContain("hidden");
        result.ShouldNotContain("script");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithStyleTags_RemovesStyleContent()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <style>.hidden { display: none; }</style>
                <p>Visible Text</p>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldContain("Visible Text");
        result.ShouldNotContain("display");
        result.ShouldNotContain("none");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithCssSelector_ExtractsTargetedContent()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div class="header">Header Content</div>
                <div class="content">Main Content</div>
                <div class="footer">Footer Content</div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html, cssSelector: ".content");

        // Assert
        result.ShouldBe("Main Content");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithXPathSelector_ExtractsTargetedContent()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div id="main">
                    <span>Target Text</span>
                </div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html, xpathSelector: "//div[@id='main']/span");

        // Assert
        result.ShouldBe("Target Text");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithInvalidSelector_ReturnsEmptyString()
    {
        // Arrange
        var html = "<html><body><p>Hello</p></body></html>";

        // Act
        var result = _sut.ExtractText(html, cssSelector: ".nonexistent");

        // Assert
        result.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_NormalizesWhitespace()
    {
        // Arrange
        var html = "<html><body><p>Hello    \n\n   World</p></body></html>";

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldBe("Hello World");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeHash_SameContent_ReturnsSameHash()
    {
        // Arrange
        var content = "Test content for hashing";

        // Act
        var hash1 = _sut.ComputeHash(content);
        var hash2 = _sut.ComputeHash(content);

        // Assert
        hash1.ShouldBe(hash2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeHash_DifferentContent_ReturnsDifferentHash()
    {
        // Arrange
        var content1 = "Content version 1";
        var content2 = "Content version 2";

        // Act
        var hash1 = _sut.ComputeHash(content1);
        var hash2 = _sut.ComputeHash(content2);

        // Assert
        hash1.ShouldNotBe(hash2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractTitle_WithTitleTag_ReturnsTitle()
    {
        // Arrange
        var html = "<html><head><title>Page Title</title></head><body></body></html>";

        // Act
        var result = _sut.ExtractTitle(html);

        // Assert
        result.ShouldBe("Page Title");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractTitle_WithoutTitleTag_ReturnsNull()
    {
        // Arrange
        var html = "<html><head></head><body></body></html>";

        // Act
        var result = _sut.ExtractTitle(html);

        // Assert
        result.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractHtml_WithCssSelector_ReturnsHtmlFragment()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div class="target"><span>Inner</span></div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractHtml(html, cssSelector: ".target");

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("<span>");
        result.ShouldContain("Inner");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CleanHtml_RemovesScriptsAndStyles()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <style>.x{}</style>
                <script>alert('x');</script>
                <p>Keep this</p>
            </body>
            </html>
            """;

        // Act
        var result = _sut.CleanHtml(html);

        // Assert
        result.ShouldContain("<p>Keep this</p>");
        result.ShouldNotContain("<script>");
        result.ShouldNotContain("<style>");
        await Task.CompletedTask;
    }
}
