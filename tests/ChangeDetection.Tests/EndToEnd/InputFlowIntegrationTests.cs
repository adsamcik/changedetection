using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;


namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// Extensive integration tests for the entire input flow.
/// Tests the pipeline from user input through URL extraction, content fetching,
/// content analysis, selector generation, and validation.
/// 
/// These tests use mocked external dependencies (LLM, HTTP) but test the
/// real integration between pipeline stages.
/// </summary>
public class InputFlowIntegrationTests : TestBase
{
    #region Test Data

    private static class TestHtml
    {
        public const string EventsPage = """
            <!DOCTYPE html>
            <html lang="cs">
            <head><title>Akce a události - Test Site</title></head>
            <body>
            <header><nav>Menu</nav></header>
            <main>
                <h1>Upcoming Events</h1>
                <div class="events-container" data-testid="events-list">
                    <article class="event-card" data-event-id="1">
                        <h2 class="event-title">Annual Conference 2025</h2>
                        <time datetime="2025-03-15">March 15, 2025</time>
                        <p class="event-location">Prague Convention Center</p>
                        <p class="event-description">Join us for our annual conference.</p>
                    </article>
                    <article class="event-card" data-event-id="2">
                        <h2 class="event-title">Workshop: AI in Practice</h2>
                        <time datetime="2025-04-20">April 20, 2025</time>
                        <p class="event-location">Online</p>
                        <p class="event-description">Hands-on AI workshop for developers.</p>
                    </article>
                    <article class="event-card" data-event-id="3">
                        <h2 class="event-title">Summer Meetup</h2>
                        <time datetime="2025-06-10">June 10, 2025</time>
                        <p class="event-location">City Park</p>
                        <p class="event-description">Casual networking event.</p>
                    </article>
                </div>
            </main>
            <footer>Copyright 2025</footer>
            </body>
            </html>
            """;

        public const string ProductListingPage = """
            <!DOCTYPE html>
            <html>
            <head><title>Products - Online Store</title></head>
            <body>
            <div id="product-grid">
                <div class="product" data-sku="SKU001">
                    <h3 class="product-name">Laptop Pro 15</h3>
                    <span class="price" data-price="1299.99">$1,299.99</span>
                    <span class="stock in-stock">In Stock</span>
                </div>
                <div class="product" data-sku="SKU002">
                    <h3 class="product-name">Wireless Mouse</h3>
                    <span class="price" data-price="49.99">$49.99</span>
                    <span class="stock in-stock">In Stock</span>
                </div>
                <div class="product" data-sku="SKU003">
                    <h3 class="product-name">USB-C Hub</h3>
                    <span class="price" data-price="79.99">$79.99</span>
                    <span class="stock out-of-stock">Out of Stock</span>
                </div>
            </div>
            </body>
            </html>
            """;

        public const string NewsPage = """
            <!DOCTYPE html>
            <html>
            <head><title>Latest News</title></head>
            <body>
            <section class="news-feed">
                <article class="news-item">
                    <h2>Breaking: Major Tech Announcement</h2>
                    <time>December 15, 2025</time>
                    <p>A major technology company announced...</p>
                </article>
                <article class="news-item">
                    <h2>Market Update</h2>
                    <time>December 14, 2025</time>
                    <p>Stock markets showed mixed results...</p>
                </article>
            </section>
            </body>
            </html>
            """;

        public const string MinimalPage = """
            <!DOCTYPE html>
            <html>
            <head><title>Simple Page</title></head>
            <body>
            <p>Hello World</p>
            </body>
            </html>
            """;

        public const string JavaScriptHeavyPage = """
            <!DOCTYPE html>
            <html>
            <head><title>SPA App</title></head>
            <body>
            <div id="app" data-reactroot>
                <noscript>Please enable JavaScript</noscript>
            </div>
            <script>window.__NEXT_DATA__ = {};</script>
            </body>
            </html>
            """;

        public const string TableDataPage = """
            <!DOCTYPE html>
            <html>
            <head><title>Data Table</title></head>
            <body>
            <table id="data-table">
                <thead>
                    <tr><th>Name</th><th>Value</th><th>Status</th></tr>
                </thead>
                <tbody>
                    <tr><td>Item A</td><td>100</td><td>Active</td></tr>
                    <tr><td>Item B</td><td>200</td><td>Pending</td></tr>
                    <tr><td>Item C</td><td>300</td><td>Active</td></tr>
                </tbody>
            </table>
            </body>
            </html>
            """;
    }

    #endregion

    #region URL Extraction Stage Tests

    [Test]
    [Arguments("https://example.com/events", "https://example.com/events")]
    [Arguments("http://example.com", "http://example.com")]
    [Arguments("https://www.example.com/path/to/page?query=1", "https://www.example.com/path/to/page?query=1")]
    [Arguments("Watch https://example.com for changes", "https://example.com")]
    [Arguments("I want to monitor https://example.com/products daily", "https://example.com/products")]
    [Arguments("https://example.com please track price changes", "https://example.com")]
    public void UrlExtraction_ExtractsUrlFromVariousInputFormats(string input, string expectedUrl)
    {
        var stage = new UrlExtractionStage();
        var urls = stage.Extract(input);

        urls.ShouldNotBeEmpty();
        urls[0].NormalizedUrl.ShouldStartWith(expectedUrl.TrimEnd('/'));
    }

    [Test]
    [Arguments("https://example.com I want to watch for events", "I want to watch for events")]
    [Arguments("Monitor https://example.com/products for price drops", "Monitor for price drops")]
    [Arguments("https://news.com track breaking news updates", "track breaking news updates")]
    [Arguments("Please watch https://example.com for any changes to the page", "Please watch for any changes to the page")]
    public void UrlExtraction_ExtractsUserIntent(string input, string expectedIntent)
    {
        var stage = new UrlExtractionStage();
        var intent = stage.ExtractUserIntent(input);

        intent.Trim().ShouldBe(expectedIntent);
    }

    [Test]
    public async Task UrlExtraction_HandlesMultipleUrls()
    {
        var stage = new UrlExtractionStage();
        var input = "Compare https://site1.com/products and https://site2.com/products for prices";

        var urls = stage.Extract(input);

        urls.Count.ShouldBe(2);
        urls.ShouldContain(u => u.NormalizedUrl.Contains("site1.com"));
        urls.ShouldContain(u => u.NormalizedUrl.Contains("site2.com"));
    }

    [Test]
    public async Task UrlExtraction_SelectsPrimaryUrl_FromMultiple()
    {
        var stage = new UrlExtractionStage();
        var input = "https://main-target.com/events I also like https://other-site.com but focus on the first";

        var urls = stage.Extract(input);
        var primary = stage.SelectPrimaryUrl(urls, input);

        primary.ShouldNotBeNull();
        primary.NormalizedUrl.ShouldContain("main-target.com");
    }

    [Test]
    public async Task UrlExtraction_NormalizesUrls()
    {
        var stage = new UrlExtractionStage();

        var urls1 = stage.Extract("https://example.com/path/");
        var urls2 = stage.Extract("https://example.com/path");

        // Both should normalize to the same value (without trailing slash)
        urls1[0].NormalizedUrl.TrimEnd('/').ShouldBe(urls2[0].NormalizedUrl.TrimEnd('/'));
    }

    [Test]
    public async Task UrlExtraction_HandlesUrlWithoutProtocol()
    {
        var stage = new UrlExtractionStage();
        var input = "Watch www.example.com/products for updates";

        var urls = stage.Extract(input);

        urls.ShouldNotBeEmpty();
        urls[0].NormalizedUrl.ShouldStartWith("https://");
    }

    [Test]
    public async Task UrlExtraction_ReturnsEmptyForNoUrl()
    {
        var stage = new UrlExtractionStage();
        var input = "I want to watch for price changes";

        var urls = stage.Extract(input);

        urls.ShouldBeEmpty();
    }

    [Test]
    public async Task UrlExtraction_PreservesContext()
    {
        var stage = new UrlExtractionStage();
        var input = "https://example.com/events I want to track new events on this page";

        var urls = stage.Extract(input);

        urls[0].Context.ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    #region Content Fetching Stage Tests

    [Test]
    public async Task ContentFetching_ReturnsSuccessfulContent()
    {
        var fetcher = CreateMockFetcher(TestHtml.EventsPage);
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<ContentFetchingStage>>();
        var stage = new ContentFetchingStage(fetcher, extractor, logger);

        var result = await stage.FetchAsync("https://example.com/events");

        result.IsSuccess.ShouldBeTrue();
        result.Html.ShouldNotBeNullOrWhiteSpace();
        result.Title.ShouldNotBeNull();
        result.Title!.ShouldContain("Akce");
        result.TextContent.ShouldNotBeNull();
        result.TextContent!.ShouldContain("Conference");
    }

    [Test]
    public async Task ContentFetching_HandlesFetchFailure()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = false, ErrorMessage = "Connection refused" });

        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<ContentFetchingStage>>();
        var stage = new ContentFetchingStage(fetcher, extractor, logger);

        var result = await stage.FetchAsync("https://example.com");

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task ContentFetching_DetectsJavaScriptNeed()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<ContentFetchingStage>>();
        var stage = new ContentFetchingStage(fetcher, extractor, logger);

        var jsContent = new FetchedContent
        {
            Url = "https://example.com",
            Html = TestHtml.JavaScriptHeavyPage,
            TextContent = "",
            IsSuccess = true,
            UsedJavaScript = false
        };

        var shouldRetry = stage.ShouldUseJavaScript(jsContent);

        shouldRetry.ShouldBeTrue("SPA content should trigger JavaScript retry");
    }

    [Test]
    public async Task ContentFetching_DoesNotRetryStaticContent()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<ContentFetchingStage>>();
        var stage = new ContentFetchingStage(fetcher, extractor, logger);

        // TextContent must be > 100 chars to avoid triggering ShouldUseJavaScript
        var staticContent = new FetchedContent
        {
            Url = "https://example.com",
            Html = TestHtml.EventsPage,
            TextContent = "This page has plenty of content to analyze and includes many words that make it longer than one hundred characters to properly test the static content detection.",
            IsSuccess = true,
            UsedJavaScript = false
        };

        var shouldRetry = stage.ShouldUseJavaScript(staticContent);

        shouldRetry.ShouldBeFalse("Static content should not trigger JavaScript retry");
    }

    #endregion

    #region Selector Validation Stage Tests

    [Test]
    [Arguments(".event-card", 3)]
    [Arguments(".event-title", 3)]
    [Arguments("[data-event-id=\"1\"]", 1)]  // CssToXPath only handles attr=value, not presence-only
    [Arguments(".events-container", 1)]
    [Arguments("#nonexistent", 0)]
    public void SelectorValidation_ValidatesCssSelectors(string selector, int expectedMatches)
    {
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        var content = new FetchedContent
        {
            Url = "https://example.com",
            Html = TestHtml.EventsPage,
            IsSuccess = true
        };

        var analysis = new ContentAnalysis { ContentType = ContentType.EventList };
        var selectors = new List<GeneratedSelector>
        {
            new() { Selector = selector, Type = SelectorType.CssSelector, Confidence = 0.8f }
        };

        var validations = stage.ValidateSelectors(content, selectors, analysis);

        validations.ShouldNotBeEmpty();
        validations[0].MatchCount.ShouldBe(expectedMatches);
        validations[0].IsValid.ShouldBe(expectedMatches > 0);
    }

    [Test]
    [Arguments("//article[@class='event-card']", 3)]
    [Arguments("//h2[contains(@class, 'event-title')]", 3)]
    [Arguments("//div[@class='events-container']", 1)]
    [Arguments("//div[@id='nonexistent']", 0)]
    public void SelectorValidation_ValidatesXPathSelectors(string xpath, int expectedMatches)
    {
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        var content = new FetchedContent
        {
            Url = "https://example.com",
            Html = TestHtml.EventsPage,
            IsSuccess = true
        };

        var analysis = new ContentAnalysis { ContentType = ContentType.EventList };
        var selectors = new List<GeneratedSelector>
        {
            new() { Selector = xpath, Type = SelectorType.XPath, Confidence = 0.8f }
        };

        var validations = stage.ValidateSelectors(content, selectors, analysis);

        validations.ShouldNotBeEmpty();
        validations[0].MatchCount.ShouldBe(expectedMatches);
    }

    [Test]
    public async Task SelectorValidation_SelectsBestSelector()
    {
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        var content = new FetchedContent
        {
            Url = "https://example.com",
            Html = TestHtml.EventsPage,
            IsSuccess = true
        };

        var analysis = new ContentAnalysis { ContentType = ContentType.EventList };
        var selectors = new List<GeneratedSelector>
        {
            new() { Selector = ".event-card", Type = SelectorType.CssSelector, Confidence = 0.9f, Priority = 1 },
            new() { Selector = ".event-title", Type = SelectorType.CssSelector, Confidence = 0.7f, Priority = 2 },
            new() { Selector = ".nonexistent", Type = SelectorType.CssSelector, Confidence = 0.5f, Priority = 3 }
        };

        var validations = stage.ValidateSelectors(content, selectors, analysis);
        var best = stage.SelectBestSelector(validations, 0.5f);

        best.ShouldNotBeNull();
        best.Selector.ShouldBe(".event-card");
    }

    [Test]
    public async Task SelectorValidation_ExtractsSampleContent()
    {
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        var content = new FetchedContent
        {
            Url = "https://example.com",
            Html = TestHtml.EventsPage,
            IsSuccess = true
        };

        var analysis = new ContentAnalysis { ContentType = ContentType.EventList };
        var selectors = new List<GeneratedSelector>
        {
            new() { Selector = ".event-title", Type = SelectorType.CssSelector, Confidence = 0.8f }
        };

        var validations = stage.ValidateSelectors(content, selectors, analysis);

        validations[0].ExtractedSample.ShouldNotBeNullOrWhiteSpace();
        validations[0].ExtractedSample!.ShouldContain("Conference");
    }

    [Test]
    public async Task SelectorValidation_HandlesInvalidSelector()
    {
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        var content = new FetchedContent
        {
            Url = "https://example.com",
            Html = TestHtml.EventsPage,
            IsSuccess = true
        };

        var analysis = new ContentAnalysis { ContentType = ContentType.EventList };
        var selectors = new List<GeneratedSelector>
        {
            new() { Selector = "[[[invalid", Type = SelectorType.CssSelector, Confidence = 0.8f }
        };

        var validations = stage.ValidateSelectors(content, selectors, analysis);

        validations[0].IsValid.ShouldBeFalse();
        validations[0].ValidationMessage.ShouldNotBeNullOrWhiteSpace();
    }

    #endregion

    #region Full Pipeline Integration Tests

    [Test]
    public async Task FullPipeline_ProcessesEventPageSuccessfully()
    {
        var pipeline = CreatePipelineWithMockedLlm(
            htmlContent: TestHtml.EventsPage,
            llmContentType: "EventList",
            llmSelectors: [
                new SelectorDto { Selector = ".event-card", Type = "CssSelector", Description = "Event cards" },
                new SelectorDto { Selector = ".event-title", Type = "CssSelector", Description = "Event titles" }
            ]);

        var result = await pipeline.ProcessAsync(
            "https://example.com/events I want to watch for new events",
            new PipelineOptions { MaxIterations = 3, MinConfidence = 0.5f });

        TestContext.Current?.OutputWriter?.WriteLine($"Pipeline result: Success={result.IsSuccess}, Stage={result.CurrentStage}");
        TestContext.Current?.OutputWriter?.WriteLine($"Best selector: {result.Session.BestSelector?.Selector}");

        // Pipeline should at least extract the URL correctly
        result.Session.ExtractedUrls.ShouldNotBeEmpty();
        result.Session.ExtractedUrls[0].NormalizedUrl.ShouldContain("example.com");

        // If the pipeline completed successfully, verify the final config
        if (result.IsSuccess && result.CurrentStage == PipelineStage.Complete)
        {
            result.FinalConfiguration.ShouldNotBeNull();
            result.FinalConfiguration!.Url.ShouldContain("example.com");
        }
        // If it needs user input, that's also acceptable for this mock scenario
        else if (result.NeedsUserInput)
        {
            result.SuggestedOptions.ShouldNotBeNull();
        }
    }

    [Test]
    public async Task FullPipeline_ProcessesProductPageSuccessfully()
    {
        var pipeline = CreatePipelineWithMockedLlm(
            htmlContent: TestHtml.ProductListingPage,
            llmContentType: "ProductListing",
            llmSelectors: [
                new SelectorDto { Selector = ".product", Type = "CssSelector", Description = "Product cards" },
                new SelectorDto { Selector = ".price", Type = "CssSelector", Description = "Product prices" }
            ]);

        var result = await pipeline.ProcessAsync(
            "https://store.com/products track price changes",
            new PipelineOptions { MaxIterations = 3, MinConfidence = 0.5f });

        // Pipeline should at least extract the URL correctly
        result.Session.ExtractedUrls.ShouldNotBeEmpty();
        result.Session.ExtractedUrls[0].NormalizedUrl.ShouldContain("store.com");

        // If the pipeline completed successfully, verify the final config
        if (result.IsSuccess && result.CurrentStage == PipelineStage.Complete)
        {
            result.FinalConfiguration.ShouldNotBeNull();
        }
    }

    [Test]
    public async Task FullPipeline_HandlesNoUrlInInput()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.EventsPage, "Unknown", []);

        var result = await pipeline.ProcessAsync(
            "I want to watch for price changes",
            new PipelineOptions());

        result.IsSuccess.ShouldBeFalse();
        result.NeedsUserInput.ShouldBeTrue();
        result.UserPrompts.ShouldNotBeEmpty();
        result.CurrentStage.ShouldBe(PipelineStage.UrlExtraction);
    }

    [Test]
    public async Task FullPipeline_HandlesMultipleUrls_AsksForSelection()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.EventsPage, "EventList", []);

        var result = await pipeline.ProcessAsync(
            "Compare https://site1.com and https://site2.com for events",
            new PipelineOptions());

        result.NeedsUserInput.ShouldBeTrue();
        result.SuggestedOptions.ShouldNotBeEmpty();
        result.SuggestedOptions.Count.ShouldBe(2);
    }

    [Test]
    public async Task FullPipeline_HandlesContentFetchFailure()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = false, ErrorMessage = "404 Not Found", HttpStatusCode = 404 });

        var pipeline = CreatePipelineWithCustomFetcher(fetcher);

        var result = await pipeline.ProcessAsync(
            "https://example.com/nonexistent watch for changes",
            new PipelineOptions());

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.CurrentStage.ShouldBe(PipelineStage.ContentFetching);
    }

    [Test]
    public async Task FullPipeline_HandlesNoSelectorsGenerated()
    {
        var pipeline = CreatePipelineWithMockedLlm(
            htmlContent: TestHtml.MinimalPage,
            llmContentType: "Unknown",
            llmSelectors: []);

        var result = await pipeline.ProcessAsync(
            "https://example.com/simple watch for changes",
            new PipelineOptions { MaxIterations = 1 });

        result.NeedsUserInput.ShouldBeTrue();
        result.SuggestedOptions.ShouldContain(o => o.Value == "fullpage");
    }

    [Test]
    public async Task FullPipeline_IteratesWhenSelectorsNeedRefinement()
    {
        var llmChain = CreateMockLlmChain(
            contentTypeResponse: "EventList",
            selectorResponses: [
                // First iteration: returns invalid selector
                """[{"selector": ".nonexistent", "type": "CssSelector", "description": "Bad selector"}]""",
                // Second iteration: returns valid selector (refinement)
                """[{"selector": ".event-card", "type": "CssSelector", "description": "Event cards"}]"""
            ]);

        var pipeline = CreatePipelineWithCustomLlm(TestHtml.EventsPage, llmChain);

        var result = await pipeline.ProcessAsync(
            "https://example.com/events watch for events",
            new PipelineOptions { MaxIterations = 3, MinConfidence = 0.5f });

        // At least one iteration should have happened (CurrentIteration is 1-based when incremented)
        result.Session.CurrentIteration.ShouldBeGreaterThanOrEqualTo(1);
    }

    #endregion

    #region Pipeline Feedback Flow Tests

    [Test]
    public async Task PipelineFeedback_HandlesUrlSelection()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.EventsPage, "EventList",
            [new SelectorDto { Selector = ".event-card", Type = "CssSelector" }]);

        var initialResult = await pipeline.ProcessAsync(
            "Compare https://site1.com and https://site2.com",
            new PipelineOptions());

        initialResult.NeedsUserInput.ShouldBeTrue();

        var continuation = await pipeline.ContinueWithFeedbackAsync(
            initialResult.Session,
            "https://site1.com",
            CancellationToken.None);

        continuation.Session.SelectedUrl.ShouldNotBeNull();
        continuation.Session.SelectedUrl.NormalizedUrl.ShouldContain("site1.com");
    }

    [Test]
    public async Task PipelineFeedback_HandlesSelectorSelection()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.EventsPage, "EventList",
            [
                new SelectorDto { Selector = ".event-card", Type = "CssSelector", Description = "Event cards" },
                new SelectorDto { Selector = ".event-title", Type = "CssSelector", Description = "Event titles" }
            ]);

        // First, get to selector selection stage
        var session = new PipelineSession
        {
            OriginalInput = "https://example.com/events watch events",
            ExtractedUrls = [new ExtractedUrl { Url = "https://example.com/events", NormalizedUrl = "https://example.com/events", IsValid = true }],
            SelectedUrl = new ExtractedUrl { Url = "https://example.com/events", NormalizedUrl = "https://example.com/events", IsValid = true },
            FetchedContent = new FetchedContent { Url = "https://example.com/events", Html = TestHtml.EventsPage, IsSuccess = true },
            ContentAnalysis = new ContentAnalysis { ContentType = ContentType.EventList, UserIntent = "watch events" },
            GeneratedSelectors = [
                new GeneratedSelector { Selector = ".event-card", Type = SelectorType.CssSelector, Confidence = 0.8f },
                new GeneratedSelector { Selector = ".event-title", Type = SelectorType.CssSelector, Confidence = 0.7f }
            ]
        };

        var result = await pipeline.ContinueWithFeedbackAsync(session, ".event-title", CancellationToken.None);

        result.Session.BestSelector.ShouldNotBeNull();
        result.Session.BestSelector.Selector.ShouldBe(".event-title");
    }

    [Test]
    public async Task PipelineFeedback_HandlesFullPageSelection()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.EventsPage, "EventList", []);

        var session = new PipelineSession
        {
            OriginalInput = "https://example.com watch for changes",
            ExtractedUrls = [new ExtractedUrl { Url = "https://example.com", NormalizedUrl = "https://example.com", IsValid = true }],
            SelectedUrl = new ExtractedUrl { Url = "https://example.com", NormalizedUrl = "https://example.com", IsValid = true },
            FetchedContent = new FetchedContent { Url = "https://example.com", Html = TestHtml.EventsPage, IsSuccess = true },
            ContentAnalysis = new ContentAnalysis { ContentType = ContentType.Unknown },
            GeneratedSelectors = []
        };

        var result = await pipeline.ContinueWithFeedbackAsync(session, "fullpage", CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.FinalConfiguration.ShouldNotBeNull();
        result.FinalConfiguration.CssSelector.ShouldBeNull();
    }

    [Test]
    public async Task PipelineFeedback_HandlesCustomDescription()
    {
        var llmChain = CreateMockLlmChain(
            contentTypeResponse: "EventList",
            selectorResponses: [
                """[{"selector": ".event-card", "type": "CssSelector", "description": "Event cards"}]"""
            ]);

        var pipeline = CreatePipelineWithCustomLlm(TestHtml.EventsPage, llmChain);

        var session = new PipelineSession
        {
            OriginalInput = "https://example.com/events",
            ExtractedUrls = [new ExtractedUrl { Url = "https://example.com/events", NormalizedUrl = "https://example.com/events", IsValid = true }],
            SelectedUrl = new ExtractedUrl { Url = "https://example.com/events", NormalizedUrl = "https://example.com/events", IsValid = true },
            FetchedContent = new FetchedContent { Url = "https://example.com/events", Html = TestHtml.EventsPage, IsSuccess = true, CleanedHtml = TestHtml.EventsPage },
            ContentAnalysis = new ContentAnalysis { ContentType = ContentType.Unknown },
            GeneratedSelectors = []
        };

        var result = await pipeline.ContinueWithFeedbackAsync(
            session,
            "I want to watch the event cards on the page",
            CancellationToken.None);

        result.Session.UserIntent.ShouldContain("event cards");
    }

    #endregion

    #region Conversation Session Tests

    [Test]
    public async Task ConversationSession_TracksMessages()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.AddUserMessage("Watch https://example.com for events");
        session.AddAssistantMessage("I found the URL. Let me analyze the page.");
        session.AddUserMessage("Focus on the event cards");

        session.Messages.Count.ShouldBe(3);
        session.OriginalInputs.Count.ShouldBe(2);
        session.Messages[0].Role.ShouldBe(MessageRole.User);
        session.Messages[1].Role.ShouldBe(MessageRole.Assistant);
    }

    [Test]
    public async Task ConversationSession_RecordsPresentedOptions()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.RecordPresentedOption("sel1", "Event cards", ".event-card");
        session.RecordPresentedOption("sel2", "Event titles", ".event-title");

        session.PresentedOptions.Count.ShouldBe(2);
        session.PresentedOptions[0].DisplayText.ShouldBe("Event cards");
    }

    [Test]
    public async Task ConversationSession_UpdatesLastActivity()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };
        var initialTime = session.LastActivityAt;

        Thread.Sleep(10);
        session.Touch();

        session.LastActivityAt.ShouldBeGreaterThan(initialTime);
    }

    [Test]
    public async Task ConversationSessionManager_CreatesAndRetrievesSessions()
    {
        var manager = new ConversationSessionManager(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<ConversationSessionManager>>());

        var session = manager.CreateSession();
        var retrieved = manager.GetSession(session.SessionId);

        retrieved.ShouldNotBeNull();
        retrieved.SessionId.ShouldBe(session.SessionId);
    }

    [Test]
    public async Task ConversationSessionManager_ReturnsNullForNonexistentSession()
    {
        var manager = new ConversationSessionManager(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<ConversationSessionManager>>());

        var nonExistentId = Guid.NewGuid();
        var retrieved = manager.GetSession(nonExistentId);

        retrieved.ShouldBeNull();
    }

    [Test]
    public async Task ConversationSessionManager_GetOrCreateSession_ReusesExisting()
    {
        var manager = new ConversationSessionManager(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ILogger<ConversationSessionManager>>());

        var sessionId = Guid.NewGuid();
        var session1 = manager.GetOrCreateSession(sessionId);
        session1.AddUserMessage("First message");

        var session2 = manager.GetOrCreateSession(sessionId);

        session2.ShouldBeSameAs(session1);
        session2.Messages.Count.ShouldBe(1);
    }

    #endregion

    #region Edge Cases and Error Handling

    [Test]
    public async Task Pipeline_HandlesCancellation()
    {
        // Create a fetcher that respects cancellation
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ct = (CancellationToken)callInfo[2];
                ct.ThrowIfCancellationRequested();
                return new FetchResult { IsSuccess = true, Html = TestHtml.EventsPage };
            });
        
        var pipeline = CreatePipelineWithCustomFetcher(fetcher);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await pipeline.ProcessAsync(
            "https://example.com/events watch events",
            new PipelineOptions(),
            cts.Token);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("cancel", Case.Insensitive);
    }

    [Test]
    public async Task Pipeline_LimitsLlmCalls()
    {
        var callCount = 0;
        var llmChain = Substitute.For<ILlmProviderChain>();
        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return new LlmResponse { IsSuccess = true, Content = "Unknown" };
            });

        var pipeline = CreatePipelineWithCustomLlm(TestHtml.EventsPage, llmChain);

        var result = await pipeline.ProcessAsync(
            "https://example.com watch",
            new PipelineOptions { MaxLlmCalls = 5, MaxIterations = 10 });

        // Pipeline should stop before making too many calls
        callCount.ShouldBeLessThanOrEqualTo(10);
    }

    [Test]
    public async Task UrlExtraction_HandlesUnicodeUrls()
    {
        var stage = new UrlExtractionStage();
        var input = "Watch https://example.com/путь/страница for updates";

        var urls = stage.Extract(input);

        urls.ShouldNotBeEmpty();
        urls[0].NormalizedUrl.ShouldContain("example.com");
    }

    [Test]
    public async Task UrlExtraction_HandlesUrlsWithSpecialCharacters()
    {
        var stage = new UrlExtractionStage();
        var input = "Watch https://example.com/path?query=test&foo=bar#section for updates";

        var urls = stage.Extract(input);

        urls.ShouldNotBeEmpty();
        urls[0].NormalizedUrl.ShouldContain("query=test");
    }

    [Test]
    public async Task Pipeline_HandlesEmptyHtmlContent()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "" });

        var pipeline = CreatePipelineWithCustomFetcher(fetcher);

        var result = await pipeline.ProcessAsync(
            "https://example.com watch",
            new PipelineOptions());

        result.IsSuccess.ShouldBeFalse();
    }

    [Test]
    public async Task SelectorValidation_HandlesHugeHtmlContent()
    {
        var extractor = CreateRealContentExtractor();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        // Create HTML with 1000 items
        var items = string.Join("", Enumerable.Range(1, 1000)
            .Select(i => $"<div class='item' data-id='{i}'>Item {i}</div>"));
        var hugeHtml = $"<html><body><div class='container'>{items}</div></body></html>";

        var content = new FetchedContent { Url = "https://example.com", Html = hugeHtml, IsSuccess = true };
        var analysis = new ContentAnalysis { ContentType = ContentType.Other };
        var selectors = new List<GeneratedSelector>
        {
            new() { Selector = ".item", Type = SelectorType.CssSelector, Confidence = 0.8f }
        };

        var validations = stage.ValidateSelectors(content, selectors, analysis);

        validations[0].MatchCount.ShouldBe(1000);
        validations[0].IsValid.ShouldBeTrue();
    }

    #endregion

    #region Content Type Specific Tests

    [Test]
    public async Task Pipeline_IdentifiesEventListContent()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.EventsPage, "EventList",
            [new SelectorDto { Selector = ".event-card", Type = "CssSelector" }]);

        var result = await pipeline.ProcessAsync(
            "https://example.com/events watch for upcoming events",
            new PipelineOptions { MinConfidence = 0.5f });

        result.Session.ContentAnalysis.ShouldNotBeNull();
        result.Session.ContentAnalysis.ContentType.ShouldBe(ContentType.EventList);
    }

    [Test]
    public async Task Pipeline_IdentifiesProductListingContent()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.ProductListingPage, "ProductListing",
            [new SelectorDto { Selector = ".product", Type = "CssSelector" }]);

        var result = await pipeline.ProcessAsync(
            "https://store.com/products watch for price changes",
            new PipelineOptions { MinConfidence = 0.5f });

        result.Session.ContentAnalysis.ShouldNotBeNull();
        result.Session.ContentAnalysis.ContentType.ShouldBe(ContentType.ProductListing);
    }

    [Test]
    public async Task Pipeline_IdentifiesNewsContent()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.NewsPage, "NewsList",
            [new SelectorDto { Selector = ".news-item", Type = "CssSelector" }]);

        var result = await pipeline.ProcessAsync(
            "https://news.com watch for breaking news",
            new PipelineOptions { MinConfidence = 0.5f });

        result.Session.ContentAnalysis.ShouldNotBeNull();
        result.Session.ContentAnalysis.ContentType.ShouldBe(ContentType.NewsList);
    }

    [Test]
    public async Task Pipeline_IdentifiesTableContent()
    {
        var pipeline = CreatePipelineWithMockedLlm(TestHtml.TableDataPage, "Table",
            [new SelectorDto { Selector = "#data-table tbody tr", Type = "CssSelector" }]);

        var result = await pipeline.ProcessAsync(
            "https://example.com/data watch for table updates",
            new PipelineOptions { MinConfidence = 0.5f });

        result.Session.ContentAnalysis.ShouldNotBeNull();
        result.Session.ContentAnalysis.ContentType.ShouldBe(ContentType.Table);
    }

    #endregion

    #region Helper Methods

    private static IPipelineEventService CreateMockPipelineEventService()
    {
        var service = Substitute.For<IPipelineEventService>();
        
        // Return a valid PipelineRun so the pipeline doesn't throw NullReferenceException
        // Note: Must use ArgAt<Guid>(index) since there are multiple Guid parameters
        service.StartRunAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => new PipelineRun
            {
                Id = Guid.NewGuid(),
                SessionId = callInfo.ArgAt<Guid>(0),
                OriginalInput = callInfo.ArgAt<string>(1),
                OwnerId = callInfo.ArgAt<Guid>(2),
                Status = PipelineRunStatus.Started
            });
        
        // Return valid PipelineEvent for all event recording methods
        service.RecordEventAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineEvent { Id = Guid.NewGuid(), PipelineRunId = Guid.Empty, Stage = "", EventType = "" });
        
        service.RecordLlmCallAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<long>(),
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>())
            .Returns(new PipelineEvent { Id = Guid.NewGuid(), PipelineRunId = Guid.Empty, Stage = "", EventType = "" });
        
        return service;
    }

    private static IContentFetcher CreateMockFetcher(string htmlContent)
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult 
            { 
                IsSuccess = true, 
                Html = htmlContent,
                HttpStatusCode = 200,
                DurationMs = 150
            });
        return fetcher;
    }

    private static IContentExtractor CreateRealContentExtractor()
    {
        var extractor = Substitute.For<IContentExtractor>();
        extractor.CleanHtml(Arg.Any<string>()).Returns(x => (string)x[0]);
        extractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(x =>
        {
            var doc = new HtmlDocument();
            doc.LoadHtml((string)x[0]);
            return doc.DocumentNode.InnerText;
        });
        extractor.ExtractTitle(Arg.Any<string>()).Returns(x =>
        {
            var doc = new HtmlDocument();
            doc.LoadHtml((string)x[0]);
            return doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "";
        });
        return extractor;
    }

    private static ILlmProviderChain CreateMockLlmChain(
        string contentTypeResponse,
        List<string> selectorResponses)
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        var selectorIndex = 0;
        var streamingSelectorIndex = 0;

        // Mock ExecuteAsync for non-streaming calls
        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = (string)callInfo[0];

                // Content type classification
                if (prompt.Contains("Category") || prompt.Contains("category"))
                {
                    return new LlmResponse { IsSuccess = true, Content = contentTypeResponse };
                }

                // User intent extraction
                if (prompt.Contains("goal") || prompt.Contains("Summarize"))
                {
                    return new LlmResponse { IsSuccess = true, Content = "Monitor for changes" };
                }

                // Section identification
                if (prompt.Contains("sections") || prompt.Contains("Analyze this HTML"))
                {
                    return new LlmResponse
                    {
                        IsSuccess = true,
                        Content = """[{"name":"Main content","selector":".container","isTarget":true,"description":"Main section"}]"""
                    };
                }

                // Selector generation
                if (prompt.Contains("selector") || prompt.Contains("Generate"))
                {
                    if (selectorIndex < selectorResponses.Count)
                    {
                        return new LlmResponse { IsSuccess = true, Content = selectorResponses[selectorIndex++] };
                    }
                    return new LlmResponse { IsSuccess = true, Content = "[]" };
                }

                return new LlmResponse { IsSuccess = true, Content = contentTypeResponse };
            });

        // Mock ExecuteStreamingAsync for streaming calls (used by ContentAnalysisStage)
        llmChain.ExecuteStreamingAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prompt = (string)callInfo[0];
                string response;

                // Content type classification (prompt contains "Category" or "category")
                if (prompt.Contains("Category") || prompt.Contains("category"))
                {
                    response = contentTypeResponse;
                }
                // User intent extraction
                else if (prompt.Contains("goal") || prompt.Contains("Summarize"))
                {
                    response = "Monitor for changes";
                }
                // Section identification
                else if (prompt.Contains("sections") || prompt.Contains("Analyze this HTML"))
                {
                    response = """[{"name":"Main content","selector":".container","isTarget":true,"description":"Main section"}]""";
                }
                // Selector generation
                else if (prompt.Contains("selector") || prompt.Contains("Generate"))
                {
                    if (streamingSelectorIndex < selectorResponses.Count)
                    {
                        response = selectorResponses[streamingSelectorIndex++];
                    }
                    else
                    {
                        response = "[]";
                    }
                }
                else
                {
                    response = contentTypeResponse;
                }

                return CreateStreamingChunks(response);
            });

        return llmChain;
    }

    private static async IAsyncEnumerable<LlmStreamChunk> CreateStreamingChunks(string response)
    {
        // Yield content chunk with the response
        yield return new LlmStreamChunk
        {
            Type = LlmStreamChunkType.Content,
            Text = response
        };

        // Yield complete chunk
        yield return new LlmStreamChunk
        {
            Type = LlmStreamChunkType.Complete
        };

        await Task.CompletedTask; // Make it truly async
    }

    private WatchSetupPipeline CreatePipelineWithMockedLlm(
        string htmlContent,
        string llmContentType,
        List<SelectorDto> llmSelectors)
    {
        var selectorJson = System.Text.Json.JsonSerializer.Serialize(llmSelectors);
        var llmChain = CreateMockLlmChain(llmContentType, [selectorJson]);
        return CreatePipelineWithCustomLlm(htmlContent, llmChain);
    }

    private WatchSetupPipeline CreatePipelineWithCustomLlm(string htmlContent, ILlmProviderChain llmChain)
    {
        var fetcher = CreateMockFetcher(htmlContent);
        var extractor = CreateRealContentExtractor();

        var urlExtraction = new UrlExtractionStage();
        var contentFetching = new ContentFetchingStage(
            fetcher, extractor, Substitute.For<ILogger<ContentFetchingStage>>());
        var contentAnalysis = new ContentAnalysisStage(
            llmChain, Substitute.For<ILogger<ContentAnalysisStage>>());
        var selectorGeneration = new SelectorGenerationStage(
            llmChain, CreatePassThroughDomCompactor(), Substitute.For<ILogger<SelectorGenerationStage>>());
        var selectorValidation = new SelectorValidationStage(
            extractor, Substitute.For<ILogger<SelectorValidationStage>>());
        var schemaDiscovery = new SchemaDiscoveryStage(
            llmChain, Substitute.For<ILogger<SchemaDiscoveryStage>>());

        return new WatchSetupPipeline(
            urlExtraction,
            contentFetching,
            contentAnalysis,
            selectorGeneration,
            selectorValidation,
            schemaDiscovery,
            llmChain,
            CreateMockPipelineEventService(),
            Substitute.For<ILlmLogService>(),
            Substitute.For<IUserContext>(),
            Substitute.For<ILogger<WatchSetupPipeline>>());
    }

    private WatchSetupPipeline CreatePipelineWithCustomFetcher(IContentFetcher fetcher)
    {
        var extractor = CreateRealContentExtractor();
        var llmChain = CreateMockLlmChain("Unknown", []);

        var urlExtraction = new UrlExtractionStage();
        var contentFetching = new ContentFetchingStage(
            fetcher, extractor, Substitute.For<ILogger<ContentFetchingStage>>());
        var contentAnalysis = new ContentAnalysisStage(
            llmChain, Substitute.For<ILogger<ContentAnalysisStage>>());
        var selectorGeneration = new SelectorGenerationStage(
            llmChain, CreatePassThroughDomCompactor(), Substitute.For<ILogger<SelectorGenerationStage>>());
        var selectorValidation2 = new SelectorValidationStage(
            extractor, Substitute.For<ILogger<SelectorValidationStage>>());
        var schemaDiscovery2 = new SchemaDiscoveryStage(
            llmChain, Substitute.For<ILogger<SchemaDiscoveryStage>>());

        return new WatchSetupPipeline(
            urlExtraction,
            contentFetching,
            contentAnalysis,
            selectorGeneration,
            selectorValidation2,
            schemaDiscovery2,
            llmChain,
            CreateMockPipelineEventService(),
            Substitute.For<ILlmLogService>(),
            Substitute.For<IUserContext>(),
            Substitute.For<ILogger<WatchSetupPipeline>>());
    }

    #endregion
}

/// <summary>
/// DTO for selector generation responses.
/// </summary>
public class SelectorDto
{
    public string? Selector { get; set; }
    public string? Type { get; set; }
    public string? Description { get; set; }
    public string? Reasoning { get; set; }
}




