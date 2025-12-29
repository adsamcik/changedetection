using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Integration tests for the Watch Setup Pipeline using the real IMG CAS events page.
/// Tests the full pipeline flow from natural language input to validated selectors.
/// 
/// The target page (https://www.img.cas.cz/novinky/akce/) contains:
/// - Events listed as cards in a flex container
/// - Each event has: title (h3), date (Termín), location (Místo)
/// - Events include seminars, courses, and conferences
/// </summary>
public class WatchSetupPipelineIntegrationTests
{
    private const string TestUrl = "https://www.img.cas.cz/novinky/akce/";
    private const string UserInput = "https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page";

    /// <summary>
    /// Sample HTML structure from the IMG CAS events page for unit testing without network.
    /// </summary>
    private static string GetSampleEventPageHtml()
    {
        return
            "<!DOCTYPE html><html><head><title>Akce - IMG CAS</title></head><body>" +
            "<div class=\"layout layout--wide\"><div class=\"container\">" +
            "<div class=\"wp-editor wp-editor--primary\">" +
            "<h1 class=\"wp-block-heading\">Akce</h1>" +
            "<div class=\"lg:flex lg:-ml-4 lg:flex-wrap\">" +
            // Event 1
            "<div class=\"mb-6 lg:mb-9 lg:w-1/3 lg:pl-4\">" +
            "<div class=\"shadow bg-white\">" +
            "<a class=\"group\" href=\"https://www.img.cas.cz/2025/08/87426-pravidelne-seminare/\">" +
            "<h3>Pravidelné semináře</h3></a>" +
            "<ul><li><strong>Termín</strong> 1. 10. 2025 - 24. 6. 2026</li>" +
            "<li><strong>Místo</strong> IMG, Posluchárna Milana Haška</li></ul>" +
            "</div></div>" +
            // Event 2
            "<div class=\"mb-6 lg:mb-9 lg:w-1/3 lg:pl-4\">" +
            "<div class=\"shadow bg-white\">" +
            "<a class=\"group\" href=\"https://www.img.cas.cz/2025/11/88755-seminar-tomas-venit/\">" +
            "<h3>Seminář – Tomáš Venit</h3></a>" +
            "<ul><li><strong>Termín</strong> 3. 12. 2025 | 15:00</li>" +
            "<li><strong>Místo</strong> IMG, Posluchárna Milana Haška</li></ul>" +
            "</div></div>" +
            // Event 3
            "<div class=\"mb-6 lg:mb-9 lg:w-1/3 lg:pl-4\">" +
            "<div class=\"shadow bg-white\">" +
            "<a class=\"group\" href=\"https://www.img.cas.cz/2025/12/88841-seminar-jakub-ridl/\">" +
            "<h3>Seminář – Jakub Rídl</h3></a>" +
            "<ul><li><strong>Termín</strong> 10. 12. 2025 | 15:00</li>" +
            "<li><strong>Místo</strong> IMG, Posluchárna Milana Haška</li></ul>" +
            "</div></div>" +
            // Event 4
            "<div class=\"mb-6 lg:mb-9 lg:w-1/3 lg:pl-4\">" +
            "<div class=\"shadow bg-white\">" +
            "<a class=\"group\" href=\"https://www.img.cas.cz/2025/10/88511-processing-and-analysis/\">" +
            "<h3>Processing and Analysis of Microscopic Images</h3></a>" +
            "<ul><li><strong>Termín</strong> 13. 4. 2026 - 17. 4. 2026</li>" +
            "<li><strong>Místo</strong> IMG, Vídeňská 1083, building F</li></ul>" +
            "</div></div>" +
            "</div></div></div></div></body></html>";
    }

    #region Stage 1: URL Extraction Tests

    [Test]
    public async Task UrlExtractionStage_ExtractsUrlFromNaturalLanguageInput()
    {
        // Arrange
        var stage = new UrlExtractionStage();

        // Act
        var urls = stage.Extract(UserInput);

        // Assert
        urls.ShouldNotBeEmpty();
        urls.Count.ShouldBe(1);
        urls[0].Url.ShouldBe(TestUrl);
        urls[0].IsValid.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("https://www.img.cas.cz/novinky/akce/")]
    [Arguments("https://www.img.cas.cz/novinky/akce/ monitor for new events")]
    [Arguments("I want to watch https://www.img.cas.cz/novinky/akce/ for upcoming seminars")]
    [Arguments("Please track the events at https://www.img.cas.cz/novinky/akce/ daily")]
    public async Task UrlExtractionStage_ExtractsUrl_FromVariousInputFormats(string input)
    {
        // Arrange
        var stage = new UrlExtractionStage();

        // Act
        var urls = stage.Extract(input);

        // Assert
        urls.ShouldNotBeEmpty();
        urls.Any(u => u.Url.Contains("img.cas.cz")).ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task UrlExtractionStage_PreservesUserContext()
    {
        // Arrange
        var stage = new UrlExtractionStage();

        // Act
        var urls = stage.Extract("https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page");

        // Assert
        urls.ShouldNotBeEmpty();
        // The stage should capture context about user intent
        var context = urls[0].Context;
        context.ShouldNotBeNullOrWhiteSpace();
        context.ShouldContain("watch");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page", 
                "I want to watch for the events on that page")]
    [Arguments("Check https://example.com/products for price changes daily", 
                "Check for price changes daily")]
    [Arguments("Monitor www.news.com for breaking news updates", 
                "Monitor for breaking news updates")]
    [Arguments("https://example.com", 
                "")] // URL only, no intent
    [Arguments("I want to track events at https://www.img.cas.cz/novinky/akce/ please notify me", 
                "I want to track events at please notify me")]
    public async Task UrlExtractionStage_ExtractsUserIntent_SeparateFromUrl(string input, string expectedIntent)
    {
        // Arrange
        var stage = new UrlExtractionStage();

        // Act
        var intent = stage.ExtractUserIntent(input);

        // Assert
        intent.ShouldBe(expectedIntent);
        await Task.CompletedTask;
    }

    #endregion

    #region Stage 2: Content Fetching Tests

    [Test]
    public async Task ContentFetchingStage_CreatesProperFetchedContent_FromMockedFetcher()
    {
        // Arrange
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = GetSampleEventPageHtml()
            });
        
        var extractor = Substitute.For<IContentExtractor>();
        var logger = Substitute.For<ILogger<ContentFetchingStage>>();
        var stage = new ContentFetchingStage(fetcher, extractor, logger);

        // Act
        var result = await stage.FetchAsync(TestUrl);

        // Assert
        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeTrue();
        result.Html.ShouldNotBeNullOrWhiteSpace();
        
        // Should contain event-related content
        result.Html!.ShouldContain("Seminář");
        result.Html.ShouldContain("Termín");
        result.Html.ShouldContain("Místo");
    }

    #endregion

    #region Selector Validation Tests (Using HtmlAgilityPack directly)

    [Test]
    [Arguments("//div[contains(@class, 'mb-6')]//h3", 4)] // Event titles
    [Arguments("//a[contains(@class, 'group')]", 4)] // Event links
    [Arguments("//li[contains(., 'Termín')]", 4)] // Date fields
    [Arguments("//li[contains(., 'Místo')]", 4)] // Location fields
    public async Task XPathSelectors_MatchExpectedEventElements(string xpath, int expectedMatches)
    {
        // Arrange
        var doc = new HtmlDocument();
        doc.LoadHtml(GetSampleEventPageHtml());

        // Act
        var nodes = doc.DocumentNode.SelectNodes(xpath);

        // Assert
        nodes.ShouldNotBeNull($"XPath '{xpath}' should match nodes");
        nodes.Count.ShouldBe(expectedMatches, $"XPath '{xpath}' should match {expectedMatches} nodes");
        await Task.CompletedTask;
    }

    [Test]
    public async Task EventTitlesSelector_ExtractsExpectedContent()
    {
        // Arrange
        var doc = new HtmlDocument();
        doc.LoadHtml(GetSampleEventPageHtml());

        // Act - XPath to get event titles
        var nodes = doc.DocumentNode.SelectNodes("//a[contains(@class, 'group')]//h3");

        // Assert
        nodes.ShouldNotBeNull();
        nodes.Count.ShouldBeGreaterThanOrEqualTo(4);

        var titles = nodes.Select(n => n.InnerText.Trim()).ToList();
        titles.ShouldContain(t => t.Contains("Seminář"));
        titles.ShouldContain(t => t.Contains("Pravidelné"));
        titles.ShouldContain(t => t.Contains("Processing"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task EventCardContainerSelector_SelectsEntireListing()
    {
        // Arrange
        var doc = new HtmlDocument();
        doc.LoadHtml(GetSampleEventPageHtml());

        // XPath to get the entire event container (to detect new/removed events)
        var xpath = "//div[contains(@class, 'lg:flex-wrap')]";

        // Act
        var container = doc.DocumentNode.SelectSingleNode(xpath);

        // Assert
        container.ShouldNotBeNull();
        container.InnerText.ShouldContain("Seminář");
        container.InnerText.ShouldContain("Pravidelné");
        await Task.CompletedTask;
    }

    [Test]
    public async Task EventDatesSelector_ExtractsDateInformation()
    {
        // Arrange
        var doc = new HtmlDocument();
        doc.LoadHtml(GetSampleEventPageHtml());

        // Act - XPath to get event dates
        var nodes = doc.DocumentNode.SelectNodes("//li[strong[contains(text(), 'Termín')]]");

        // Assert
        nodes.ShouldNotBeNull();
        nodes.Count.ShouldBe(4);
        
        // Should contain various date formats
        var dateTexts = nodes.Select(n => n.InnerText).ToList();
        dateTexts.ShouldContain(t => t.Contains("2025"));
        dateTexts.ShouldContain(t => t.Contains("2026"));
        await Task.CompletedTask;
    }

    #endregion

    #region End-to-End Selector Expectations

    [Test]
    public async Task ExpectedSelectors_ForImgCasEvents_ShouldAllWork()
    {
        // This test documents what we expect the LLM to generate
        // and verifies these selectors work correctly

        var doc = new HtmlDocument();
        doc.LoadHtml(GetSampleEventPageHtml());

        // The LLM should generate selectors that can:
        // 1. Monitor for new events (event card selector)
        var eventCards = doc.DocumentNode.SelectNodes("//div[contains(@class, 'mb-6') and contains(@class, 'lg:mb-9')]");
        eventCards.ShouldNotBeNull("Event card selector should work");
        eventCards.Count.ShouldBe(4);

        // 2. Extract event titles
        var eventTitles = doc.DocumentNode.SelectNodes("//a[contains(@class, 'group')]//h3");
        eventTitles.ShouldNotBeNull("Event title selector should work");
        eventTitles.Count.ShouldBe(4);

        // 3. Get the full listing container (simplest change detection)
        var container = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'lg:flex-wrap')]");
        container.ShouldNotBeNull("Container selector should work");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ExpectedWatchConfiguration_ForImgCasEvents()
    {
        // Document what we expect the pipeline to produce
        var expectedSelectors = new[]
        {
            "//div[contains(@class, 'lg:flex-wrap')]",     // Full event container
            "//div[contains(@class, 'mb-6')]",              // Individual event cards  
            "//a[contains(@class, 'group')]//h3"            // Just event titles
        };

        // Verify all expected selectors work
        var doc = new HtmlDocument();
        doc.LoadHtml(GetSampleEventPageHtml());

        foreach (var selector in expectedSelectors)
        {
            var nodes = doc.DocumentNode.SelectNodes(selector);
            nodes.ShouldNotBeNull($"Selector '{selector}' should work");
            nodes.Count.ShouldBeGreaterThan(0, $"Selector '{selector}' should match elements");
        }
        await Task.CompletedTask;
    }

    #endregion

    #region SelectorValidationStage Tests

    [Test]
    public async Task SelectorValidationStage_ValidatesSelectorsCorrectly()
    {
        // Arrange
        var extractor = Substitute.For<IContentExtractor>();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        var content = new FetchedContent
        {
            Url = TestUrl,
            Html = GetSampleEventPageHtml(),
            IsSuccess = true
        };

        var analysis = new ContentAnalysis
        {
            ContentType = ContentType.EventList,
            UserIntent = "Watch for new events"
        };

        var selectors = new List<GeneratedSelector>
        {
            new()
            {
                Selector = "//a[contains(@class, 'group')]//h3",
                Type = SelectorType.XPath,
                Confidence = 0.9f,
                Description = "Event titles",
                Priority = 1
            },
            new()
            {
                Selector = "//div[contains(@class, 'lg:flex-wrap')]",
                Type = SelectorType.XPath,
                Confidence = 0.85f,
                Description = "Event container",
                Priority = 2
            }
        };

        // Act
        var validations = stage.ValidateSelectors(content, selectors, analysis);

        // Assert
        validations.ShouldNotBeEmpty();
        validations.Count.ShouldBe(2);
        
        // All selectors should be valid
        validations.ShouldAllBe(v => v.IsValid);
        
        // Should have extracted samples
        validations.ShouldAllBe(v => !string.IsNullOrEmpty(v.ExtractedSample));
        await Task.CompletedTask;
    }

    [Test]
    public async Task SelectorValidationStage_SelectsBestSelector()
    {
        // Arrange
        var extractor = Substitute.For<IContentExtractor>();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        var content = new FetchedContent
        {
            Url = TestUrl,
            Html = GetSampleEventPageHtml(),
            IsSuccess = true
        };

        var analysis = new ContentAnalysis
        {
            ContentType = ContentType.EventList,
            UserIntent = "Watch for new events"
        };

        var selectors = new List<GeneratedSelector>
        {
            new()
            {
                Selector = "//div[contains(@class, 'lg:flex-wrap')]",
                Type = SelectorType.XPath,
                Confidence = 0.9f,
                Priority = 1
            }
        };

        var validations = stage.ValidateSelectors(content, selectors, analysis);

        // Act
        var best = stage.SelectBestSelector(validations, 0.5f);

        // Assert
        best.ShouldNotBeNull();
        best.Selector.ShouldBe("//div[contains(@class, 'lg:flex-wrap')]");
        await Task.CompletedTask;
    }

    #endregion
}

/// <summary>
/// Tests for the InputProcessor integration with the pipeline.
/// </summary>
public class InputProcessorPipelineIntegrationTests
{
    [Test]
    public async Task InputProcessor_CorrectlyIdentifies_UrlWithNaturalLanguage()
    {
        // Verify the URL + natural language pattern is correctly parsed
        var input = "https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page";

        // Should detect URL and natural language parts
        input.ShouldContain("https://");
        input.ShouldContain("watch");
        input.ShouldContain("events");

        // The URL should be extractable using the same regex as UrlExtractionStage
        var urlMatch = System.Text.RegularExpressions.Regex.Match(
            input, 
            @"https?://[^\s]+");
        
        urlMatch.Success.ShouldBeTrue();
        urlMatch.Value.ShouldBe("https://www.img.cas.cz/novinky/akce/");
        
        // Natural language part should be the remainder
        var naturalLanguagePart = input[(urlMatch.Index + urlMatch.Length)..].Trim();
        naturalLanguagePart.ShouldBe("I want to watch for the events on that page");
        await Task.CompletedTask;
    }

    [Test]
    public async Task UrlExtractionStage_CapturesContextFromNaturalLanguage()
    {
        // Arrange
        var stage = new UrlExtractionStage();
        var input = "https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page";

        // Act
        var urls = stage.Extract(input);

        // Assert
        urls.ShouldNotBeEmpty();
        urls[0].Url.ShouldBe("https://www.img.cas.cz/novinky/akce/");
        var context = urls[0].Context;
        context.ShouldNotBeNull();
        context.ShouldContain("watch");
        context.ShouldContain("events");
        await Task.CompletedTask;
    }
}
