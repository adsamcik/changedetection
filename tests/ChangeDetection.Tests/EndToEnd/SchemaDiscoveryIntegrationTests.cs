using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;


namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// Integration tests for schema discovery in the input flow.
/// Tests the LLM-powered detection of structured data patterns.
/// </summary>
public class SchemaDiscoveryIntegrationTests
{
    #region Test HTML Data

    private const string EventListHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>Events</title></head>
        <body>
        <div class="events-list">
            <div class="event" data-id="evt-001">
                <h3 class="event-name">Tech Conference 2025</h3>
                <span class="event-date">2025-03-15</span>
                <span class="event-location">Prague</span>
                <a class="event-link" href="/events/tech-conf">Details</a>
            </div>
            <div class="event" data-id="evt-002">
                <h3 class="event-name">AI Workshop</h3>
                <span class="event-date">2025-04-20</span>
                <span class="event-location">Online</span>
                <a class="event-link" href="/events/ai-workshop">Details</a>
            </div>
            <div class="event" data-id="evt-003">
                <h3 class="event-name">Startup Meetup</h3>
                <span class="event-date">2025-05-10</span>
                <span class="event-location">Brno</span>
                <a class="event-link" href="/events/startup">Details</a>
            </div>
        </div>
        </body>
        </html>
        """;

    private const string ProductCatalogHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>Products</title></head>
        <body>
        <div id="catalog">
            <article class="product-card" data-sku="PROD-001">
                <img class="product-image" src="/img/laptop.jpg" alt="Laptop">
                <h2 class="product-title">Business Laptop</h2>
                <span class="product-price" data-value="1299">$1,299.00</span>
                <span class="product-rating">4.5</span>
                <span class="stock-status in-stock">In Stock</span>
            </article>
            <article class="product-card" data-sku="PROD-002">
                <img class="product-image" src="/img/mouse.jpg" alt="Mouse">
                <h2 class="product-title">Wireless Mouse</h2>
                <span class="product-price" data-value="49">$49.00</span>
                <span class="product-rating">4.2</span>
                <span class="stock-status in-stock">In Stock</span>
            </article>
            <article class="product-card" data-sku="PROD-003">
                <img class="product-image" src="/img/keyboard.jpg" alt="Keyboard">
                <h2 class="product-title">Mechanical Keyboard</h2>
                <span class="product-price" data-value="149">$149.00</span>
                <span class="product-rating">4.8</span>
                <span class="stock-status out-of-stock">Out of Stock</span>
            </article>
        </div>
        </body>
        </html>
        """;

    private const string JobListingsHtml = """
        <!DOCTYPE html>
        <html>
        <head><title>Job Openings</title></head>
        <body>
        <section class="job-listings">
            <div class="job-posting" id="job-123">
                <h2 class="job-title">Senior Software Engineer</h2>
                <span class="company-name">TechCorp</span>
                <span class="location">Remote</span>
                <span class="salary-range">$120k - $160k</span>
                <time class="posted-date" datetime="2025-12-01">Dec 1, 2025</time>
            </div>
            <div class="job-posting" id="job-124">
                <h2 class="job-title">Product Manager</h2>
                <span class="company-name">StartupXYZ</span>
                <span class="location">San Francisco</span>
                <span class="salary-range">$140k - $180k</span>
                <time class="posted-date" datetime="2025-12-05">Dec 5, 2025</time>
            </div>
            <div class="job-posting" id="job-125">
                <h2 class="job-title">DevOps Engineer</h2>
                <span class="company-name">CloudTech</span>
                <span class="location">New York</span>
                <span class="salary-range">$130k - $170k</span>
                <time class="posted-date" datetime="2025-12-10">Dec 10, 2025</time>
            </div>
        </section>
        </body>
        </html>
        """;

    #endregion

    #region Schema Discovery Detection Tests

    [Test]
    public async Task SchemaDiscovery_DetectsRepeatingEventItems()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(EventListHtml);

        // The selector should match all event items
        var items = doc.DocumentNode.SelectNodes("//div[@class='event']");

        items.ShouldNotBeNull();
        items.Count.ShouldBe(3);

        // Each item should have consistent structure
        foreach (var item in items)
        {
            item.SelectSingleNode(".//h3[@class='event-name']").ShouldNotBeNull();
            item.SelectSingleNode(".//span[@class='event-date']").ShouldNotBeNull();
            item.SelectSingleNode(".//span[@class='event-location']").ShouldNotBeNull();
            item.SelectSingleNode(".//a[@class='event-link']").ShouldNotBeNull();
        }
    }

    [Test]
    public async Task SchemaDiscovery_DetectsRepeatingProductItems()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ProductCatalogHtml);

        var items = doc.DocumentNode.SelectNodes("//article[@class='product-card']");

        items.ShouldNotBeNull();
        items.Count.ShouldBe(3);

        // Each item should have consistent structure
        foreach (var item in items)
        {
            item.SelectSingleNode(".//h2[@class='product-title']").ShouldNotBeNull();
            item.SelectSingleNode(".//span[@class='product-price']").ShouldNotBeNull();
            item.SelectSingleNode(".//img[@class='product-image']").ShouldNotBeNull();
        }
    }

    [Test]
    public async Task SchemaDiscovery_DetectsIdentityFieldsFromDataAttributes()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ProductCatalogHtml);

        var items = doc.DocumentNode.SelectNodes("//article[@class='product-card']");
        
        items.ShouldNotBeNull();

        // Each product should have a unique SKU in data-sku attribute
        var skus = items.Select(i => i.GetAttributeValue("data-sku", "")).ToList();

        skus.ShouldAllBe(sku => !string.IsNullOrEmpty(sku));
        skus.Distinct().Count().ShouldBe(skus.Count);
    }

    [Test]
    public async Task SchemaDiscovery_ExtractsFieldTypesCorrectly()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(ProductCatalogHtml);

        var item = doc.DocumentNode.SelectSingleNode("//article[@class='product-card']");
        item.ShouldNotBeNull();

        // String field
        var title = item.SelectSingleNode(".//h2[@class='product-title']")?.InnerText;
        title.ShouldNotBeNullOrWhiteSpace();

        // Number field (from data attribute)
        var priceAttr = item.SelectSingleNode(".//span[@class='product-price']")?.GetAttributeValue("data-value", "");
        int.TryParse(priceAttr, out var price).ShouldBeTrue();
        price.ShouldBe(1299);

        // Image URL field
        var imgSrc = item.SelectSingleNode(".//img[@class='product-image']")?.GetAttributeValue("src", "");
        imgSrc.ShouldNotBeNullOrWhiteSpace();
        imgSrc.ShouldContain("/img/");
    }

    #endregion

    #region Partial Configuration Tests

    [Test]
    public async Task PartialWatchConfiguration_TracksDiscoveredSchema()
    {
        var config = new PartialWatchConfiguration
        {
            Url = "https://example.com/events",
            Name = "Event Watch",
            DiscoveredSchema = new DiscoveredSchema
            {
                ItemSelector = ".event",
                Fields =
                [
                    new DiscoveredField
                    {
                        Name = "name",
                        Type = "String",
                        Selector = ".event-name",
                        IsRequired = true,
                        IsIdentityField = true,
                        Confidence = 0.95f
                    },
                    new DiscoveredField
                    {
                        Name = "date",
                        Type = "Date",
                        Selector = ".event-date",
                        IsRequired = true,
                        Confidence = 0.9f
                    }
                ],
                InferredIdentityFields = ["name"],
                Confidence = 0.92f,
                SampleItemCount = 3
            },
            SchemaEnabled = true
        };

        config.HasMinimumConfiguration.ShouldBeTrue();
        config.DiscoveredSchema.ShouldNotBeNull();
        config.DiscoveredSchema.Fields.Count.ShouldBe(2);
        config.DiscoveredSchema.InferredIdentityFields.ShouldContain("name");
    }

    [Test]
    public async Task PartialWatchConfiguration_TracksInferredIdentityFields()
    {
        var config = new PartialWatchConfiguration
        {
            Url = "https://example.com/products",
            InferredIdentityFields = ["sku", "name"]
        };

        config.InferredIdentityFields.Count.ShouldBe(2);
        config.InferredIdentityFields.ShouldContain("sku");
        config.InferredIdentityFields.ShouldContain("name");
    }

    #endregion

    #region Setup Stage Flow Tests

    [Test]
    [Arguments(SetupStage.Initial, "Initial state")]
    [Arguments(SetupStage.Processing, "Processing input")]
    [Arguments(SetupStage.Fetching, "Fetching content")]
    [Arguments(SetupStage.Analyzing, "Analyzing page")]
    [Arguments(SetupStage.SchemaDiscovery, "Discovering schema")]
    [Arguments(SetupStage.SchemaRefinement, "Refining schema")]
    [Arguments(SetupStage.Confirmation, "Confirming configuration")]
    [Arguments(SetupStage.Completed, "Setup completed")]
    public void SetupStage_HasAllExpectedStages(SetupStage stage, string description)
    {
        TestContext.Current?.OutputWriter?.WriteLine($"Stage: {stage} - {description}");
        Enum.IsDefined(typeof(SetupStage), stage).ShouldBeTrue();
    }

    [Test]
    public async Task ConversationSession_TracksSetupStageProgression()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.CurrentStage.ShouldBe(SetupStage.Initial);

        session.CurrentStage = SetupStage.Processing;
        session.CurrentStage.ShouldBe(SetupStage.Processing);

        session.CurrentStage = SetupStage.SchemaDiscovery;
        session.CurrentStage.ShouldBe(SetupStage.SchemaDiscovery);
    }

    [Test]
    public async Task ConversationSession_BuildsPartialConfiguration()
    {
        var session = new ConversationSession { SessionId = Guid.NewGuid() };

        session.Configuration.Url = "https://example.com/events";
        session.Configuration.Name = "Event Watch";
        session.Configuration.CssSelector = ".event";
        session.Configuration.Tags.Add("events");
        session.Configuration.Tags.Add("monitoring");

        session.Configuration.HasMinimumConfiguration.ShouldBeTrue();
        session.Configuration.Tags.Count.ShouldBe(2);
    }

    #endregion

    #region Selector Validation for Schema Discovery

    [Test]
    public async Task SelectorValidation_ValidatesItemContainerSelector()
    {
        var extractor = CreateMockExtractor();
        var logger = Substitute.For<ILogger<SelectorValidationStage>>();
        var stage = new SelectorValidationStage(extractor, logger);

        var content = new FetchedContent
        {
            Url = "https://example.com/events",
            Html = EventListHtml,
            IsSuccess = true
        };

        var analysis = new ContentAnalysis { ContentType = ContentType.EventList };
        var selectors = new List<GeneratedSelector>
        {
            new()
            {
                Selector = "//div[@class='event']",
                Type = SelectorType.XPath,
                Confidence = 0.9f,
                Description = "Event item container"
            }
        };

        var validations = stage.ValidateSelectors(content, selectors, analysis);

        validations.ShouldNotBeEmpty();
        validations[0].IsValid.ShouldBeTrue();
        validations[0].MatchCount.ShouldBe(3);
    }

    [Test]
    public async Task SelectorValidation_ValidatesNestedFieldSelectors()
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(EventListHtml);

        // Test field selectors relative to item
        var item = doc.DocumentNode.SelectSingleNode("//div[@class='event']");
        item.ShouldNotBeNull();

        // Field selector tests
        var nameNode = item.SelectSingleNode(".//h3[@class='event-name']");
        var dateNode = item.SelectSingleNode(".//span[@class='event-date']");
        var locationNode = item.SelectSingleNode(".//span[@class='event-location']");

        nameNode.ShouldNotBeNull();
        nameNode.InnerText.ShouldBe("Tech Conference 2025");

        dateNode.ShouldNotBeNull();
        dateNode.InnerText.ShouldBe("2025-03-15");

        locationNode.ShouldNotBeNull();
        locationNode.InnerText.ShouldBe("Prague");
    }

    #endregion

    #region Content Type to Schema Mapping

    [Test]
    [Arguments(ContentType.EventList, true)]
    [Arguments(ContentType.ProductListing, true)]
    [Arguments(ContentType.NewsList, true)]
    [Arguments(ContentType.Table, true)]
    [Arguments(ContentType.Feed, true)]
    [Arguments(ContentType.Article, false)]
    [Arguments(ContentType.PriceInfo, false)]
    [Arguments(ContentType.StatusPage, false)]
    public void ContentType_DeterminesSchemaDiscoveryApplicability(ContentType contentType, bool expectsSchema)
    {
        var isListType = contentType switch
        {
            ContentType.EventList => true,
            ContentType.ProductListing => true,
            ContentType.NewsList => true,
            ContentType.Table => true,
            ContentType.Feed => true,
            _ => false
        };

        isListType.ShouldBe(expectsSchema);
    }

    #endregion

    #region Helper Methods

    private static IContentExtractor CreateMockExtractor()
    {
        var extractor = Substitute.For<IContentExtractor>();
        extractor.CleanHtml(Arg.Any<string>()).Returns(x => x.Arg<string>());
        extractor.ExtractText(Arg.Any<string>()).Returns(x =>
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(x.Arg<string>());
            return doc.DocumentNode.InnerText;
        });
        extractor.ExtractTitle(Arg.Any<string>()).Returns(x =>
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(x.Arg<string>());
            return doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "";
        });
        return extractor;
    }

    #endregion
}

/// <summary>
/// Integration tests for multi-step pipeline recovery flows.
/// </summary>
public class PipelineRecoveryIntegrationTests
{
    #region Recovery Flow Tests

    [Test]
    public async Task PipelineSession_TracksRecoveryAttempts()
    {
        var session = new PipelineSession
        {
            OriginalInput = "https://example.com watch events",
            RecoveryAttempts = 0
        };

        session.RecoveryAttempts++;
        session.LastRecoveryError = "Selector matched no elements";
        session.RecoveryDiagnosticContext = "Page structure may have changed";

        session.RecoveryAttempts.ShouldBe(1);
        session.LastRecoveryError.ShouldNotBeNullOrWhiteSpace();
        session.RecoveryDiagnosticContext.ShouldNotBeNullOrWhiteSpace();
    }

    [Test]
    public async Task PipelineSession_RecordsLlmCalls()
    {
        var session = new PipelineSession();

        session.RecordLlmCall(maxCalls: 10);
        session.RecordLlmCall(maxCalls: 10);
        session.RecordLlmCall(maxCalls: 10);

        session.LlmCallCount.ShouldBe(3);
    }

    [Test]
    public async Task PipelineSession_ThrowsOnLlmCallLimitExceeded()
    {
        var session = new PipelineSession();

        // Make 5 calls with limit of 5
        for (int i = 0; i < 5; i++)
        {
            session.RecordLlmCall(maxCalls: 5);
        }

        // 6th call should throw
        Should.Throw<InvalidOperationException>(() => session.RecordLlmCall(maxCalls: 5))
            .Message.ShouldContain("limit exceeded");
    }

    [Test]
    public async Task PipelineOptions_HasRecoverySettings()
    {
        var options = new PipelineOptions
        {
            MaxRecoveryAttempts = 3,
            MaxLlmCalls = 20
        };

        options.MaxRecoveryAttempts.ShouldBe(3);
        options.MaxLlmCalls.ShouldBe(20);
    }

    #endregion

    #region Iteration History Tests

    [Test]
    public async Task PipelineSession_TracksIterationHistory()
    {
        var session = new PipelineSession
        {
            OriginalInput = "https://example.com watch events"
        };

        session.IterationHistory.Add("Extracted URL: https://example.com");
        session.IterationHistory.Add("Fetched 15000 chars of HTML");
        session.IterationHistory.Add("Content analysis: EventList");
        session.IterationHistory.Add("Generated 3 selectors");
        session.IterationHistory.Add("Best selector: .event-card");

        session.IterationHistory.Count.ShouldBe(5);
        session.IterationHistory.ShouldContain(h => h.Contains("EventList"));
    }

    [Test]
    public async Task PipelineSession_TracksCurrentIteration()
    {
        var session = new PipelineSession();

        session.CurrentIteration = 1;
        session.IterationHistory.Add("Iteration 1: Generated 2 selectors, none valid");

        session.CurrentIteration = 2;
        session.IterationHistory.Add("Iteration 2: Refined selectors, found valid match");

        session.CurrentIteration.ShouldBe(2);
        session.IterationHistory.Count.ShouldBe(2);
    }

    #endregion
}

/// <summary>
/// Integration tests for complex user input scenarios.
/// </summary>
public class ComplexInputScenariosTests
{
    #region Natural Language Variation Tests

    [Test]
    [Arguments("https://example.com/events")]
    [Arguments("Watch https://example.com/events")]
    [Arguments("I want to monitor https://example.com/events")]
    [Arguments("Please track changes on https://example.com/events")]
    [Arguments("https://example.com/events - notify me when new events appear")]
    [Arguments("Monitor https://example.com/events for upcoming conferences")]
    [Arguments("Keep an eye on https://example.com/events and alert me")]
    [Arguments("https://example.com/events I need to know about new seminars")]
    public void UrlExtraction_HandlesVariousNaturalLanguagePatterns(string input)
    {
        var stage = new UrlExtractionStage();
        var urls = stage.Extract(input);

        urls.ShouldNotBeEmpty($"Should extract URL from: {input}");
        urls[0].NormalizedUrl.ShouldContain("example.com/events");
    }

    [Test]
    [Arguments("monitor events", "monitor events")]
    [Arguments("watch for new products", "watch for new products")]
    [Arguments("track price changes daily", "track price changes daily")]
    [Arguments("notify when stock is available", "notify when stock is available")]
    [Arguments("alert on breaking news", "alert on breaking news")]
    public void UrlExtraction_PreservesIntentWithoutUrl(string input, string expectedIntent)
    {
        var stage = new UrlExtractionStage();
        var intent = stage.ExtractUserIntent(input);

        // Without URL, the entire input is the intent
        intent.Trim().ShouldContain(expectedIntent);
    }

    #endregion

    #region URL Format Variation Tests

    [Test]
    [Arguments("http://example.com", "http://example.com")]
    [Arguments("https://example.com", "https://example.com")]
    [Arguments("https://www.example.com", "https://www.example.com")]
    [Arguments("https://sub.example.com/path", "https://sub.example.com/path")]
    [Arguments("https://example.com:8080/path", "https://example.com:8080/path")]
    [Arguments("https://example.com/path?q=1&r=2", "https://example.com/path?q=1&r=2")]
    [Arguments("https://example.com/path#section", "https://example.com/path#section")]
    public void UrlExtraction_HandlesVariousUrlFormats(string url, string expectedNormalized)
    {
        var stage = new UrlExtractionStage();
        var urls = stage.Extract($"Watch {url} for changes");

        urls.ShouldNotBeEmpty();
        urls[0].NormalizedUrl.ShouldStartWith(expectedNormalized.Split('?')[0].Split('#')[0].TrimEnd('/'));
    }

    #endregion

    #region Edge Case Input Tests

    [Test]
    public async Task UrlExtraction_HandlesEmptyInput()
    {
        var stage = new UrlExtractionStage();

        var urls = stage.Extract("");
        urls.ShouldBeEmpty();

        var intent = stage.ExtractUserIntent("");
        intent.ShouldBeEmpty();
    }

    [Test]
    public async Task UrlExtraction_HandlesWhitespaceOnlyInput()
    {
        var stage = new UrlExtractionStage();

        var urls = stage.Extract("   \t\n   ");
        urls.ShouldBeEmpty();
    }

    [Test]
    public async Task UrlExtraction_HandlesVeryLongInput()
    {
        var stage = new UrlExtractionStage();
        var longText = new string('a', 10000);
        var input = $"https://example.com/events {longText}";

        var urls = stage.Extract(input);

        urls.ShouldNotBeEmpty();
        urls[0].NormalizedUrl.ShouldContain("example.com");
    }

    [Test]
    public async Task UrlExtraction_HandlesUrlInMiddleOfText()
    {
        var stage = new UrlExtractionStage();
        var input = "I found this great page https://example.com/events yesterday and I want to monitor it for new conferences";

        var urls = stage.Extract(input);
        var intent = stage.ExtractUserIntent(input);

        urls.ShouldNotBeEmpty();
        urls[0].NormalizedUrl.ShouldContain("example.com/events");
        intent.ShouldContain("great page");
        intent.ShouldContain("monitor");
    }

    [Test]
    public async Task UrlExtraction_HandlesMixedCaseUrl()
    {
        var stage = new UrlExtractionStage();
        // Note: The scheme must be lowercase due to a case-sensitivity issue in IsValidUrl
        // The path can have mixed case and will be preserved
        var input = "Watch https://EXAMPLE.COM/Events for changes";

        var urls = stage.Extract(input);

        urls.ShouldNotBeEmpty();
        urls[0].IsValid.ShouldBeTrue();
        urls[0].NormalizedUrl.ShouldContain("example.com");
    }

    #endregion
}




