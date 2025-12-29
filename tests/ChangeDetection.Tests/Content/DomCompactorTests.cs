using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for DomCompactor - verifies HTML is compacted while preserving
/// selector-relevant structure for CSS/XPath selector generation.
/// </summary>
[Category("Unit")]
public class DomCompactorTests : TestBase
{
    private DomCompactor CreateCompactor() => new(CreateLogger<DomCompactor>());

    #region Basic Compaction

    [Test]
    public void Compact_EmptyHtml_ReturnsEmptyResult()
    {
        // Arrange
        var compactor = CreateCompactor();

        // Act
        var result = compactor.Compact("");

        // Assert
        result.Html.ShouldBeEmpty();
        result.OriginalSize.ShouldBe(0);
        result.CompactedSize.ShouldBe(0);
    }

    [Test]
    public void Compact_RemovesScriptTags()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <div class="content">
                <script>alert('hello');</script>
                <p>Visible text</p>
            </div>
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldNotContain("script");
        result.Html.ShouldNotContain("alert");
        result.Html.ShouldContain("Visible text");
        Log($"Compacted: {result.Html}");
    }

    [Test]
    public void Compact_RemovesStyleTags()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <div>
                <style>.special { color: red; }</style>
                <p class="special">Text in paragraph</p>
            </div>
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldNotContain("<style");
        result.Html.ShouldNotContain(".special {");
        result.Html.ShouldContain("Text in paragraph");
    }

    [Test]
    public void Compact_RemovesHtmlComments()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <div>
                <!-- This is a comment -->
                <p>Content</p>
            </div>
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldNotContain("<!--");
        result.Html.ShouldNotContain("comment");
        result.Html.ShouldContain("Content");
    }

    #endregion

    #region Attribute Filtering

    [Test]
    public void Compact_PreservesIdAttribute()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """<div id="main-content" onclick="doSomething()">Text</div>""";

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("id=\"main-content\"");
        result.Html.ShouldNotContain("onclick");
    }

    [Test]
    public void Compact_PreservesDataAttributes()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """<div data-product-id="123" data-price="99.99">Product</div>""";

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("data-product-id=\"123\"");
        result.Html.ShouldContain("data-price=\"99.99\"");
        Log($"Compacted: {result.Html}");
    }

    [Test]
    public void Compact_PreservesHrefAndSrc()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <a href="/products">Link</a>
            <img src="/image.jpg" alt="Image">
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("href=\"/products\"");
        result.Html.ShouldContain("src=\"/image.jpg\"");
        result.Html.ShouldContain("alt=\"Image\"");
    }

    #endregion

    #region Utility Class Filtering

    [Test]
    public void Compact_FiltersTailwindClasses()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """<div class="product-card p-4 mx-auto text-gray-600 shadow-lg rounded-xl">Product</div>""";

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("product-card");
        result.Html.ShouldNotContain("p-4");
        result.Html.ShouldNotContain("mx-auto");
        result.Html.ShouldNotContain("text-gray-600");
        result.Html.ShouldNotContain("shadow-lg");
        Log($"Compacted: {result.Html}");
    }

    [Test]
    public void Compact_FiltersBootstrapClasses()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """<div class="product-list container col-md-6 d-flex justify-content-center">Items</div>""";

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("product-list");
        result.Html.ShouldNotContain("col-md-6");
        result.Html.ShouldNotContain("d-flex");
        result.Html.ShouldNotContain("justify-content-center");
    }

    [Test]
    public void Compact_PreservesMeaningfulClasses()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """<article class="event-card shadow-lg">Event</article>""";

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("event-card");
        result.Html.ShouldNotContain("shadow-lg");
    }

    [Test]
    public void Compact_LimitsClassCount()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """<div class="first-class second-class third-class fourth-class">Content</div>""";

        // Act
        var result = compactor.Compact(html, new DomCompactorOptions { MaxClassesPerElement = 2 });

        // Assert
        result.Html.ShouldContain("first-class");
        result.Html.ShouldContain("second-class");
        result.Html.ShouldNotContain("third-class");
        result.Html.ShouldNotContain("fourth-class");
    }

    #endregion

    #region Text Truncation

    [Test]
    public void Compact_TruncatesLongText()
    {
        // Arrange
        var compactor = CreateCompactor();
        var longText = "This is a very long piece of text that should be truncated because it exceeds the maximum length configured in the options.";
        var html = $"<p>{longText}</p>";

        // Act
        var result = compactor.Compact(html, new DomCompactorOptions { MaxTextLength = 30 });

        // Assert
        result.Html.ShouldContain("...");
        result.Html.Length.ShouldBeLessThan(html.Length);
        Log($"Compacted: {result.Html}");
    }

    [Test]
    public void Compact_PreservesShortText()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = "<p>Short text</p>";

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("Short text");
        result.Html.ShouldNotContain("...");
    }

    #endregion

    #region Wrapper Collapsing

    [Test]
    public void Compact_CollapsesEmptyWrapperDivs()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <div>
                <div>
                    <div>
                        <p class="content">Deep content</p>
                    </div>
                </div>
            </div>
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("Deep content");
        result.WrappersCollapsed.ShouldBeGreaterThan(0);
        Log($"Collapsed {result.WrappersCollapsed} wrappers");
        Log($"Compacted: {result.Html}");
    }

    [Test]
    public void Compact_DoesNotCollapseWrapperWithId()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <div id="important-wrapper">
                <p>Content</p>
            </div>
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("id=\"important-wrapper\"");
    }

    [Test]
    public void Compact_DoesNotCollapseWrapperWithClasses()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <div class="product-container">
                <p>Content</p>
            </div>
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("product-container");
    }

    #endregion

    #region Real-World Scenarios

    [Test]
    public void Compact_ProductCard_PreservesSelectorStructure()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <div class="container mx-auto px-4 py-8 bg-white shadow-lg rounded-xl">
                <div class="flex flex-col gap-4">
                    <article class="product-card p-4 border rounded" data-id="123">
                        <h2 class="text-xl font-bold text-gray-900 mb-2">Widget Pro Premium Edition with Extended Warranty</h2>
                        <p class="description text-sm text-gray-600 leading-relaxed">
                            This is an amazing product with incredible features that will change your life forever and make everything better...
                        </p>
                        <span class="price text-2xl font-semibold text-green-500">$99.99</span>
                    </article>
                </div>
            </div>
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        // Selector-relevant structure should be preserved
        result.Html.ShouldContain("product-card");
        result.Html.ShouldContain("data-id=\"123\"");
        result.Html.ShouldContain("price");
        result.Html.ShouldContain("$99.99");
        
        // Utility classes should be removed
        result.Html.ShouldNotContain("mx-auto");
        result.Html.ShouldNotContain("px-4");
        result.Html.ShouldNotContain("text-2xl");
        result.Html.ShouldNotContain("font-semibold");
        
        // Should be significantly smaller
        result.CompressionRatio.ShouldBeLessThan(0.7f);
        
        Log($"Original: {result.OriginalSize} chars");
        Log($"Compacted: {result.CompactedSize} chars ({result.CompressionRatio:P0})");
        Log($"Removed {result.ElementsRemoved} elements, collapsed {result.WrappersCollapsed} wrappers");
        Log($"Result:\n{result.Html}");
    }

    [Test]
    public void Compact_EventList_PreservesEventCards()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <main class="events-page container mx-auto">
                <div class="shadow bg-white rounded p-4 mb-4">
                    <h3 class="event-title text-lg font-bold">Annual Conference 2024</h3>
                    <time datetime="2024-03-15">March 15, 2024</time>
                    <p>Join us for the biggest event of the year...</p>
                </div>
                <div class="shadow bg-white rounded p-4 mb-4">
                    <h3 class="event-title text-lg font-bold">Workshop Series</h3>
                    <time datetime="2024-04-01">April 1, 2024</time>
                    <p>Learn from industry experts...</p>
                </div>
            </main>
            """;

        // Act
        var result = compactor.Compact(html);

        // Assert
        result.Html.ShouldContain("event-title");
        result.Html.ShouldContain("datetime");
        result.Html.ShouldContain("Annual Conference");
        result.Html.ShouldContain("Workshop Series");
        
        Log($"Compacted ({result.CompressionRatio:P0}):\n{result.Html}");
    }

    #endregion

    #region CompactToTokenBudget

    [Test]
    public void CompactToTokenBudget_FitsWithinBudget()
    {
        // Arrange
        var compactor = CreateCompactor();
        var html = """
            <div class="container mx-auto px-4">
                <div class="flex flex-col">
                    <article class="product" data-id="1">
                        <h2>Product One with a really long title that goes on and on</h2>
                        <p class="description">Long description that should be truncated to fit within the token budget</p>
                        <span class="price">$10</span>
                    </article>
                    <article class="product" data-id="2">
                        <h2>Product Two with another really long title</h2>
                        <p class="description">Another long description that adds to the total size</p>
                        <span class="price">$20</span>
                    </article>
                </div>
            </div>
            """;

        // Act - target 100 tokens (roughly 400 chars)
        var result = compactor.CompactToTokenBudget(html, targetTokens: 100);

        // Assert
        result.CompactedSize.ShouldBeLessThanOrEqualTo(500); // Allow some margin
        result.Html.ShouldContain("product");
        result.Html.ShouldContain("data-id");
        
        Log($"Target: 400 chars, Got: {result.CompactedSize} chars");
        Log($"Result:\n{result.Html}");
    }

    #endregion
}
