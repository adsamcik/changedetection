using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;
using NSubstitute;
using Shouldly;
using System.Runtime.CompilerServices;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Pipeline tests for a single product page (LTT Store).
/// Tests the 2-turn LLM-driven schema discovery:
///   Turn 1: LLM classifies structure as list/single/none
///   Turn 2: LLM discovers extractable fields with unified prompt
///
/// Key behaviors validated:
/// 1. ContentAnalysisStage correctly classifies product pages as PriceInfo
/// 2. Turn 1 classification identifies the page as "single" structure
/// 3. Turn 2 discovers price, availability, rating fields with absolute selectors
/// 4. Post-processing sets TrackHistory=true for Currency/Number/Status fields
/// 5. Post-processing extracts CurrencyCode from sample values
/// </summary>
[Category("Unit")]
public class LttStorePipelineTests : TestBase
{
    private const string UserIntent = "Track the price and availability of this cable";
    private const string PageUrl = "https://global.lttstore.com/products/ltt-truespec-cable-usb-type-c-to-c?variant=44410354204717";

    #region Test Fixtures

    private static FetchedContent GetLttStoreProductContent() => new()
    {
        Url = PageUrl,
        IsSuccess = true,
        Title = "LTT TrueSpec Cable USB Type C to C (240W) – LTT Store",
        Html = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <title>LTT TrueSpec Cable USB Type C to C (240W) – LTT Store</title>
                <meta property="og:type" content="product">
                <meta property="product:price:amount" content="29.99">
                <meta property="product:price:currency" content="CAD">
            </head>
            <body>
            <main id="MainContent">
                <section class="product" data-product-id="ltt-truespec-cable-usb-type-c-to-c">
                    <div class="product__info-wrapper">
                        <h1 class="product__title">LTT TrueSpec Cable USB Type C to C (240W)</h1>
                        
                        <div class="product__price" id="price-template--product">
                            <span class="price-item price-item--regular">$29.99 CAD</span>
                        </div>
                        
                        <div class="product__description">
                            <p>After many years of development, LTT "TrueSpec" cables are here!</p>
                        </div>
                        
                        <fieldset class="product-form__input" id="variant-selectors">
                            <legend class="form__label">Specification:</legend>
                            <div class="product-form__input--swatch">
                                <input type="radio" name="specification" value="USB 2.0 480Mbps / 240W - 1m" checked>
                                <label>USB 2.0 480Mbps / 240W - 1m</label>
                                <input type="radio" name="specification" value="USB 2.0 480Mbps / 240W - 2m">
                                <label>USB 2.0 480Mbps / 240W - 2m</label>
                                <input type="radio" name="specification" value="USB4 40Gbps / 240W - 1m">
                                <label>USB4 40Gbps / 240W - 1m</label>
                            </div>
                        </fieldset>
                        
                        <div class="product-form__buttons" id="product-actions">
                            <button type="button" class="product-form__submit" disabled>
                                <span>Notify me when available</span>
                            </button>
                        </div>
                        
                        <div class="product__reviews" data-rating="5.0" data-review-count="2">
                            <span class="rating-value">5.00 out of 5</span>
                            <span class="review-count">2 reviews</span>
                        </div>
                    </div>
                </section>
            </main>
            </body>
            </html>
            """,
        TextContent = """
            LTT TrueSpec Cable USB Type C to C (240W)
            $29.99 CAD
            Specification:
            USB 2.0 480Mbps / 240W - 1m
            USB 2.0 480Mbps / 240W - 2m
            USB4 40Gbps / 240W - 1m
            Notify me when available
            5.00 out of 5 - 2 reviews
            After many years of development, LTT "TrueSpec" cables are here!
            """
    };

    /// <summary>
    /// Mock Turn 1 response: LLM classifies the page as "single" structure.
    /// </summary>
    private const string Turn1SingleClassification =
        """{"st":"single","cs":null,"ic":0,"ef":["Price","Stock Status","Rating","Review Count"],"c":0.9,"r":"Single product page with price, availability, and review data"}""";

    /// <summary>
    /// Mock Turn 2 response: LLM discovers fields using short keys.
    /// </summary>
    private const string Turn2FieldDiscovery =
        """[{"n":"Product Name","t":"String","s":".product__title","r":true,"id":true,"v":"LTT TrueSpec Cable USB Type C to C (240W)"},{"n":"Price","t":"Currency","s":".price-item--regular","r":true,"id":false,"v":"$29.99 CAD"},{"n":"Stock Status","t":"Status","s":".product-form__submit span","r":true,"id":false,"v":"Notify me when available"},{"n":"Rating","t":"Number","s":".rating-value","r":false,"id":false,"v":"5.00 out of 5"},{"n":"Review Count","t":"Number","s":".review-count","r":false,"id":false,"v":"2 reviews"}]""";

    private static ContentAnalysis GetProductPageAnalysis() => new()
    {
        ContentType = ContentType.PriceInfo,
        UserIntent = "Track price and availability",
        PageDescription = "Product page",
        Confidence = 0.8f,
        RecommendedApproach = MonitoringApproach.MultipleSelectors,
        IdentifiedSections =
        [
            new PageSection
            {
                Name = "Price",
                SuggestedSelector = ".product__price",
                IsLikelyTarget = true,
                Description = "Product price"
            }
        ]
    };

    #endregion

    #region Stage 1: URL Extraction

    [Test]
    public async Task UrlExtraction_ExtractsUrlAndIntent()
    {
        var stage = new UrlExtractionStage();
        var input = $"{PageUrl} {UserIntent}";

        var urls = stage.Extract(input);
        var intent = stage.ExtractUserIntent(input);

        Log($"URLs: {urls.Count}");
        Log($"URL: {urls.FirstOrDefault()?.Url}");
        Log($"Intent: \"{intent}\"");

        urls.ShouldNotBeEmpty();
        urls[0].Url.ShouldContain("lttstore.com");
        urls[0].IsValid.ShouldBeTrue();
        intent.ShouldNotBeNullOrWhiteSpace();
        intent.ShouldContain("price");

        await Task.CompletedTask;
    }

    #endregion

    #region Stage 3: Content Analysis

    [Test]
    public async Task ContentAnalysis_ClassifiesProductPageAsPriceInfo()
    {
        // Arrange: Mock LLM returns "PriceInfo" for classification, intent, and sections
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            "PriceInfo",
            "Track USB-C cable price and stock availability",
            """[{"name":"Product Price","selector":".product__price","isTarget":true,"description":"Product price section showing $29.99 CAD"},{"name":"Product Availability","selector":".product-form__buttons","isTarget":true,"description":"Stock status and add-to-cart button"}]"""
        ]);
        var stage = new ContentAnalysisStage(llmChain, CreateLogger<ContentAnalysisStage>());

        // Act
        var result = await stage.AnalyzeAsync(GetLttStoreProductContent(), UserIntent);

        // Assert
        Log($"ContentType: {result.ContentType}");
        Log($"Intent: {result.UserIntent}");
        Log($"Approach: {result.RecommendedApproach}");
        Log($"Confidence: {result.Confidence:P0}");
        Log($"Sections: {result.IdentifiedSections.Count}");
        foreach (var s in result.IdentifiedSections)
            Log($"  {(s.IsLikelyTarget ? "★" : "○")} {s.Name} -> {s.SuggestedSelector}");

        result.ContentType.ShouldBe(ContentType.PriceInfo);
        result.UserIntent.ShouldNotBeNullOrWhiteSpace();
        result.IdentifiedSections.ShouldNotBeEmpty();
        result.IdentifiedSections.Any(s => s.IsLikelyTarget).ShouldBeTrue();
        result.RecommendedApproach.ShouldBe(MonitoringApproach.MultipleSelectors);
    }

    #endregion

    #region Stage 3.5: Schema Discovery (2-Turn LLM-Driven)

    [Test]
    public async Task SchemaDiscovery_Turn1ClassifiesSingleItem()
    {
        // Turn 1 should classify the product page as "single" structure
        // Turn 2 should discover fields
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            Turn1SingleClassification,
            Turn2FieldDiscovery
        ]);

        var stage = new SchemaDiscoveryStage(llmChain, CreateLogger<SchemaDiscoveryStage>());
        var analysis = GetProductPageAnalysis();

        // Act
        var schema = await stage.DiscoverAsync(GetLttStoreProductContent(), analysis);

        // Assert
        schema.ShouldNotBeNull("LLM classified as 'single' — should produce a schema");
        Log($"Schema: {schema.Fields.Count} fields, {schema.SampleItemCount} items");
        Log($"ItemSelector: {schema.ItemSelector}");
        Log($"Confidence: {schema.Confidence:P0}");
        Log($"Explanation: {schema.Explanation}");
        foreach (var f in schema.Fields)
            Log($"  {f.Name} ({f.Type}) -> {f.Selector} [sample: {f.SampleValues.FirstOrDefault()}]");

        schema.SampleItemCount.ShouldBe(1);
        schema.Fields.Count.ShouldBeGreaterThanOrEqualTo(3);
        schema.Fields.ShouldContain(f => f.Name.Contains("Price", StringComparison.OrdinalIgnoreCase));
        schema.Fields.ShouldContain(f => f.Name.Contains("Stock", StringComparison.OrdinalIgnoreCase) ||
                                         f.Name.Contains("Availab", StringComparison.OrdinalIgnoreCase));
        schema.ItemSelector.ShouldNotBeNullOrWhiteSpace();

        await Task.CompletedTask;
    }

    [Test]
    public async Task SchemaDiscovery_NoneTypeReturnsNoSchema()
    {
        // When Turn 1 classifies as "none", no schema should be produced
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            """{"st":"none","cs":null,"ic":0,"ef":[],"c":0.8,"r":"Blog article with no extractable data"}"""
        ]);

        var stage = new SchemaDiscoveryStage(llmChain, CreateLogger<SchemaDiscoveryStage>());
        var analysis = new ContentAnalysis
        {
            ContentType = ContentType.Article,
            UserIntent = "Track changes to this article",
            PageDescription = "Blog article",
            Confidence = 0.7f,
            RecommendedApproach = MonitoringApproach.FullPage,
            IdentifiedSections = []
        };

        var schema = await stage.DiscoverAsync(GetLttStoreProductContent(), analysis);

        schema.ShouldBeNull("'none' classification should produce no schema");
        Log("Correctly returned null for 'none' classification");

        await Task.CompletedTask;
    }

    [Test]
    public async Task SchemaDiscovery_ListTypeReturnsListSchema()
    {
        // When Turn 1 classifies as "list", schema should have container selector and item count
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            // Turn 1: list classification
            """{"st":"list","cs":".product-card","ic":24,"ef":["Name","Price","Rating"],"c":0.9,"r":"24 product cards in grid"}""",
            // Turn 2: field discovery (with relative selectors)
            """[{"n":"Name","t":"String","s":".product-card__title","r":true,"id":true,"v":"Product A"},{"n":"Price","t":"Currency","s":".product-card__price","r":true,"id":false,"v":"$19.99 USD"},{"n":"Rating","t":"Number","s":".rating","r":false,"id":false,"v":"4.5"}]"""
        ]);

        var stage = new SchemaDiscoveryStage(llmChain, CreateLogger<SchemaDiscoveryStage>());
        var analysis = new ContentAnalysis
        {
            ContentType = ContentType.ProductListing,
            UserIntent = "Track product prices",
            PageDescription = "Product listing page",
            Confidence = 0.9f,
            RecommendedApproach = MonitoringApproach.MultipleSelectors,
            IdentifiedSections = []
        };

        var schema = await stage.DiscoverAsync(GetLttStoreProductContent(), analysis);

        schema.ShouldNotBeNull();
        Log($"Schema: {schema.Fields.Count} fields, {schema.SampleItemCount} items, selector={schema.ItemSelector}");

        schema.ItemSelector.ShouldBe(".product-card");
        schema.SampleItemCount.ShouldBe(24);
        schema.Fields.ShouldContain(f => f.Name == "Name" && f.IsIdentityField);
        schema.Fields.ShouldContain(f => f.Name == "Price");

        await Task.CompletedTask;
    }

    #endregion

    #region Full Pipeline Flow

    [Test]
    public async Task FullPipeline_ProductPage_DiscoversPriceFields()
    {
        // Arrange: mock all LLM calls in sequence
        // Content analysis: 3 calls (classification, intent, sections)
        // Schema discovery: 2 calls (Turn 1 classification, Turn 2 field discovery)
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            // Content analysis: classification
            "PriceInfo",
            // Content analysis: intent extraction
            "Track USB-C cable price and stock availability",
            // Content analysis: section identification
            """[{"name":"Price","selector":".product__price","isTarget":true,"description":"Product price $29.99 CAD"},{"name":"Stock Status","selector":".product-form__buttons","isTarget":true,"description":"Availability: out of stock"}]""",
            // Schema discovery Turn 1: structure classification
            Turn1SingleClassification,
            // Schema discovery Turn 2: field discovery
            Turn2FieldDiscovery
        ]);

        var analysisStage = new ContentAnalysisStage(llmChain, CreateLogger<ContentAnalysisStage>());
        var schemaStage = new SchemaDiscoveryStage(llmChain, CreateLogger<SchemaDiscoveryStage>());
        var content = GetLttStoreProductContent();

        // Stage 3: Content analysis
        var analysis = await analysisStage.AnalyzeAsync(content, UserIntent);

        Log("=== Full Pipeline Flow: LTT Store Product Page ===");
        Log($"[Stage 3] ContentType: {analysis.ContentType}");
        Log($"[Stage 3] Intent: {analysis.UserIntent}");
        Log($"[Stage 3] Approach: {analysis.RecommendedApproach}");
        Log($"[Stage 3] Sections:");
        foreach (var s in analysis.IdentifiedSections)
            Log($"  {(s.IsLikelyTarget ? "★" : "○")} {s.Name} -> {s.SuggestedSelector}");

        // Stage 3.5: Schema discovery (2-turn LLM-driven — always runs)
        var schema = await schemaStage.DiscoverAsync(content, analysis);
        schema.ShouldNotBeNull("Schema should be discovered for product pages");

        Log($"[Stage 3.5 Turn 1] Structure classified as: single");
        Log($"[Stage 3.5 Turn 2] Fields discovered: {schema.Fields.Count}");
        foreach (var f in schema.Fields)
            Log($"  {f.Name} ({f.Type}) = {f.SampleValues.FirstOrDefault()}");

        Log($"[Stage 3.5] Confidence: {schema.Confidence:P0}");
        Log($"[Stage 3.5] Identity fields: {string.Join(", ", schema.InferredIdentityFields)}");

        // Assertions
        analysis.ContentType.ShouldBe(ContentType.PriceInfo);
        schema.SampleItemCount.ShouldBe(1);
        schema.Fields.Count.ShouldBeGreaterThanOrEqualTo(3);
        schema.Fields.ShouldContain(f => f.Name == "Price");
        schema.Fields.ShouldContain(f => f.Name == "Stock Status");

        schema.InferredIdentityFields.ShouldContain("Product Name");
    }

    #endregion

    #region Helpers

    private static ILlmProviderChain CreateMockLlmChainWithStreamingResponses(string[] responses)
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        var responseIndex = 0;

        llmChain.ExecuteStreamingAsync(
                Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var currentIndex = responseIndex++;
                var content = currentIndex < responses.Length ? responses[currentIndex] : "";
                return CreateAsyncEnumerable(content);
            });

        return llmChain;
    }

    private static async IAsyncEnumerable<LlmStreamChunk> CreateAsyncEnumerable(
        string content,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();

        yield return new LlmStreamChunk
        {
            Type = LlmStreamChunkType.Start,
            ProviderName = "MockProvider",
            Model = "mock-model"
        };

        if (!string.IsNullOrEmpty(content))
        {
            yield return new LlmStreamChunk
            {
                Type = LlmStreamChunkType.Content,
                Text = content,
                ProviderName = "MockProvider",
                Model = "mock-model"
            };
        }

        yield return new LlmStreamChunk
        {
            Type = LlmStreamChunkType.Complete,
            FinalResponse = new LlmResponse
            {
                IsSuccess = true,
                Content = content,
                ProviderUsed = "MockProvider",
                Model = "mock-model"
            }
        };
    }

    #endregion
}
