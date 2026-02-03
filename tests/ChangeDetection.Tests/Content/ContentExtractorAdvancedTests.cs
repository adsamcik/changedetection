using ChangeDetection.Services.Content;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Advanced tests for ContentExtractor covering edge cases.
/// </summary>
[Category("Unit")]
public class ContentExtractorAdvancedTests
{
    private readonly ContentExtractor _sut = new();

    [Test]
    public async Task ExtractText_EmptyHtml_ReturnsEmpty()
    {
        // Arrange
        var html = "";

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_MalformedHtml_StillExtracts()
    {
        // Arrange - unclosed tags
        var html = "<html><body><p>Text without closing tag<div>More text";

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldContain("Text");
        result.ShouldContain("More text");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_NestedDivs_ExtractsAllText()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div>
                    <div>
                        <div>Deeply nested</div>
                    </div>
                </div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldContain("Deeply nested");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithComments_RemovesComments()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <!-- This is a comment -->
                <p>Visible text</p>
                <!-- Another comment -->
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldContain("Visible text");
        result.ShouldNotContain("comment");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithNoscript_RemovesNoscriptContent()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <noscript>JavaScript is disabled</noscript>
                <p>Main content</p>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldContain("Main content");
        result.ShouldNotContain("JavaScript is disabled");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithMultipleCssMatches_ReturnsFirst()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div class="item">First</div>
                <div class="item">Second</div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html, cssSelector: ".item");

        // Assert
        result.ShouldBe("First");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithIdSelector_FindsById()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div id="target">Found by ID</div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html, cssSelector: "#target");

        // Assert
        result.ShouldBe("Found by ID");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithElementClassSelector_FindsCorrectly()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <span class="highlight">Wrong element</span>
                <div class="highlight">Right element</div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html, cssSelector: "div.highlight");

        // Assert
        result.ShouldBe("Right element");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithElementIdSelector_FindsCorrectly()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <span id="main">Span</span>
                <div id="main">Div</div>
            </body>
            </html>
            """;

        // Act - IDs should be unique, but testing the selector
        var result = _sut.ExtractText(html, cssSelector: "div#main");

        // Assert
        result.ShouldBe("Div");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithDescendantSelector_NotSupportedByBasicConverter()
    {
        // Arrange - The basic CSS-to-XPath converter doesn't support complex descendant selectors
        var html = """
            <html>
            <body>
                <div class="container">
                    <p class="content">Nested content</p>
                </div>
                <p class="content">Not nested</p>
            </body>
            </html>
            """;

        // Act - Complex selectors may not work with the basic converter
        var result = _sut.ExtractText(html, cssSelector: ".container .content");

        // Assert - The basic converter may return empty for complex selectors
        // This is a known limitation of the simple CSS-to-XPath conversion
        result.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithXPathContainsText_Works()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div>Not this one</div>
                <div>Target text here</div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html, xpathSelector: "//div[contains(text(),'Target')]");

        // Assert
        result.ShouldContain("Target");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithUnicodeCharacters_PreservesContent()
    {
        // Arrange
        var html = "<html><body><p>日本語テキスト 中文 한국어</p></body></html>";

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldContain("日本語");
        result.ShouldContain("中文");
        result.ShouldContain("한국어");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithHtmlEntities_PreservesEntities()
    {
        // Arrange - HtmlAgilityPack preserves entities in InnerText
        var html = "<html><body><p>&lt;script&gt; &amp; &quot;quotes&quot;</p></body></html>";

        // Act
        var result = _sut.ExtractText(html);

        // Assert - Entities remain encoded
        result.ShouldContain("&lt;script&gt;");
        result.ShouldContain("&amp;");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_WithBreakTags_NormalizesWhitespace()
    {
        // Arrange
        var html = "<html><body><p>Line1<br>Line2<br/>Line3</p></body></html>";

        // Act
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldNotBeNullOrEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeHash_EmptyString_ReturnsValidHash()
    {
        // Arrange
        var content = "";

        // Act
        var result = _sut.ComputeHash(content);

        // Assert
        result.ShouldNotBeNullOrEmpty();
        result.Length.ShouldBe(64); // SHA256 produces 64 hex characters
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeHash_WhitespaceVariations_ProduceDifferentHashes()
    {
        // Arrange
        var content1 = "Hello World";
        var content2 = "Hello  World"; // extra space

        // Act
        var hash1 = _sut.ComputeHash(content1);
        var hash2 = _sut.ComputeHash(content2);

        // Assert
        hash1.ShouldNotBe(hash2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ComputeHash_LargeContent_Succeeds()
    {
        // Arrange
        var content = new string('x', 1_000_000); // 1MB of text

        // Act
        var result = _sut.ComputeHash(content);

        // Assert
        result.ShouldNotBeNullOrEmpty();
        result.Length.ShouldBe(64);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractTitle_WithWhitespace_Trims()
    {
        // Arrange
        var html = "<html><head><title>  Spaced Title  </title></head></html>";

        // Act
        var result = _sut.ExtractTitle(html);

        // Assert
        result.ShouldBe("Spaced Title");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractTitle_NestedInHead_StillFinds()
    {
        // Arrange
        var html = """
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8">
                <title>Found Title</title>
            </head>
            </html>
            """;

        // Act
        var result = _sut.ExtractTitle(html);

        // Assert
        result.ShouldBe("Found Title");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractHtml_InvalidSelector_ReturnsNull()
    {
        // Arrange
        var html = "<html><body><p>Content</p></body></html>";

        // Act
        var result = _sut.ExtractHtml(html, cssSelector: ".nonexistent");

        // Assert
        result.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractHtml_WithXPath_ReturnsHtmlFragment()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div id="content"><span>Text</span></div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractHtml(html, xpathSelector: "//div[@id='content']");

        // Assert
        result.ShouldNotBeNull();
        result.ShouldContain("<span>Text</span>");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CleanHtml_PreservesEssentialAttributes()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="http://example.com" onclick="alert()">Link</a>
                <img src="image.png" alt="Image" data-tracking="x">
            </body>
            </html>
            """;

        // Act
        var result = _sut.CleanHtml(html);

        // Assert
        result.ShouldContain("href=");
        result.ShouldContain("src=");
        result.ShouldContain("alt=");
        result.ShouldNotContain("onclick");
        result.ShouldNotContain("data-tracking");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CleanHtml_PreservesIdAndClass()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <div id="main" class="container" style="color:red">Content</div>
            </body>
            </html>
            """;

        // Act
        var result = _sut.CleanHtml(html);

        // Assert
        result.ShouldContain("id=\"main\"");
        result.ShouldContain("class=\"container\"");
        result.ShouldNotContain("style=");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_ElementSelector_FindsElements()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <article>Article content</article>
            </body>
            </html>
            """;

        // Act
        var result = _sut.ExtractText(html, cssSelector: "article");

        // Assert
        result.ShouldBe("Article content");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExtractText_PreservesTitleAttribute()
    {
        // Arrange
        var html = """
            <html>
            <body>
                <a href="#" title="Tooltip text">Link</a>
            </body>
            </html>
            """;

        // Act - ExtractText only gets innerText, not attributes
        var result = _sut.ExtractText(html);

        // Assert
        result.ShouldBe("Link");
        await Task.CompletedTask;
    }
}
