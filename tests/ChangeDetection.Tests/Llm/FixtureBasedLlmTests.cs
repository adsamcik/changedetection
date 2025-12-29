using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using ChangeDetection.Tests.Llm.Fixtures;
using ChangeDetection.Tests.Llm.TestHelpers;
using NSubstitute;
using Shouldly;
using System.Text.Json;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Deterministic LLM tests using real captured responses from Ollama.
/// 
/// These tests use fixtures captured via CaptureOllamaTrafficTests and stored
/// in Llm/Fixtures/Responses/*.json. They run without a live LLM server.
/// 
/// To update fixtures, run the capture tests with Ollama:
///   ./test.ps1 -Filter "/*/*/*/*CaptureOllamaTrafficTests*"
/// </summary>
[Category("Unit")]
public class FixtureBasedLlmTests : TestBase
{
    #region Simple Response Tests

    [Test]
    public async Task SimpleGreeting_ReturnsExpectedResponse()
    {
        // Arrange - Load real captured response
        var response = LlmFixtureManager.GetFixtureResponse("simple-greeting");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Hello!");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Content.ShouldNotBeNullOrEmpty("Should have a response");
        // Accept any greeting-like response (LLM output varies)
        var content = result.Content!.ToLowerInvariant();
        var isGreeting = content.Contains("hi") || content.Contains("hello") || 
                         content.Contains("hey") || content.Contains("assist") ||
                         content.Contains("help");
        isGreeting.ShouldBeTrue($"Expected a greeting response, got: {result.Content}");
        result.ProviderUsed.ShouldNotBeNullOrEmpty();
        Log($"Response: {result.Content}");
    }

    #endregion

    #region Price Extraction Tests

    [Test]
    public async Task PriceExtraction_ExtractsCorrectValues()
    {
        // Arrange - Real captured price extraction response
        var response = LlmFixtureManager.GetFixtureResponse("price-extraction");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Extract price from HTML");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var content = result.Content!;
        content.ShouldContain("29.99");
        content.ShouldContain("49.99");
        // Accept either USD (ISO code) or $ (symbol) - LLMs may return either
        (content.Contains("USD") || content.Contains("$")).ShouldBeTrue("Currency should be USD or $");
        Log($"Price extraction: {content}");
    }

    [Test]
    public async Task PriceExtraction_JsonCanBeParsed()
    {
        // Arrange
        var response = LlmFixtureManager.GetFixtureResponse("price-extraction");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Extract price");

        // Assert - Strip markdown and parse JSON
        result.IsSuccess.ShouldBeTrue();
        var json = StripMarkdownCodeBlock(result.Content!);
        
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        root.GetProperty("currentPrice").GetDecimal().ShouldBe(29.99m);
        root.GetProperty("originalPrice").GetDecimal().ShouldBe(49.99m);
        // Accept either USD (ISO code) or $ (symbol) - LLMs may return either
        var currency = root.GetProperty("currency").GetString();
        (currency == "USD" || currency == "$").ShouldBeTrue($"Currency should be USD or $, was: {currency}");
    }

    #endregion

    #region Content Classification Tests

    [Test]
    public async Task ContentClassification_IdentifiesProductPage()
    {
        // Arrange - Real captured classification
        var response = LlmFixtureManager.GetFixtureResponse("content-classification");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Classify this page");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Content!.ToLowerInvariant().ShouldContain("product");
    }

    [Test]
    public async Task ContentClassification_IdentifiesEventListPage()
    {
        // Arrange - Real captured event list classification
        var response = LlmFixtureManager.GetFixtureResponse("event-list-classification");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Classify this page");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Content!.ToLowerInvariant().ShouldContain("event");
    }

    #endregion

    #region Intent Extraction Tests

    [Test]
    public async Task IntentExtraction_ExtractsUserIntent()
    {
        // Arrange - Real captured intent extraction
        var response = LlmFixtureManager.GetFixtureResponse("intent-extraction");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("What does the user want?");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var content = result.Content!.ToLowerInvariant();
        
        // Should contain key intent-related terms for tracking/monitoring events
        // The response may use various phrases that indicate monitoring intent
        var hasTrackingIntent = content.Contains("monitor") || 
                                content.Contains("track") || 
                                content.Contains("notification") ||
                                content.Contains("alert");
        hasTrackingIntent.ShouldBeTrue($"Expected 'monitor', 'track', 'notification', or 'alert' in: {content}");
        content.ShouldContain("event");
        
        Log($"Intent: {result.Content}");
    }

    #endregion

    #region Section Identification Tests

    [Test]
    public async Task SectionIdentification_ReturnsJsonArray()
    {
        // Arrange - Real captured section identification
        var response = LlmFixtureManager.GetFixtureResponse("section-identification");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Identify sections");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var json = StripMarkdownCodeBlock(result.Content!);
        
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().ShouldBeGreaterThan(0);
        Log($"Found {doc.RootElement.GetArrayLength()} sections");
    }

    [Test]
    public async Task SectionIdentification_ContainsEventSelectors()
    {
        // Arrange
        var response = LlmFixtureManager.GetFixtureResponse("section-identification");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Identify sections");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var json = StripMarkdownCodeBlock(result.Content!);
        
        using var doc = JsonDocument.Parse(json);
        var sections = doc.RootElement.EnumerateArray().ToList();
        
        // Extract all selectors from the response
        var selectors = sections
            .Where(s => s.TryGetProperty("selector", out _))
            .Select(s => s.GetProperty("selector").GetString()!)
            .ToList();
        
        // Should contain event-related selectors (either ".events" or ".event" is valid)
        var hasEventSelector = selectors.Any(s => s.Contains("event", StringComparison.OrdinalIgnoreCase));
        hasEventSelector.ShouldBeTrue($"Expected event selector in: {string.Join(", ", selectors)}");
        
        Log($"Found selectors: {string.Join(", ", selectors)}");
    }

    #endregion

    #region Selector Generation Tests

    [Test]
    public async Task SelectorGeneration_ReturnsSelectorWithConfidence()
    {
        // Arrange - Real captured selector generation
        var response = LlmFixtureManager.GetFixtureResponse("selector-generation");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Generate selector");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var json = StripMarkdownCodeBlock(result.Content!);
        
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        root.TryGetProperty("selector", out var selector).ShouldBeTrue();
        selector.GetString().ShouldNotBeNullOrEmpty();
        
        root.TryGetProperty("confidence", out var confidence).ShouldBeTrue();
        confidence.GetDouble().ShouldBeGreaterThan(0);
        
        Log($"Selector: {selector.GetString()}, Confidence: {confidence.GetDouble()}");
    }

    [Test]
    public async Task MultiSelectorGeneration_ReturnsMultipleSelectors()
    {
        // Arrange - Real captured multi-selector generation
        var response = LlmFixtureManager.GetFixtureResponse("multi-selector-generation");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Generate selectors");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        var json = StripMarkdownCodeBlock(result.Content!);
        
        using var doc = JsonDocument.Parse(json);
        var selectors = doc.RootElement;
        
        selectors.ValueKind.ShouldBe(JsonValueKind.Array);
        selectors.GetArrayLength().ShouldBeGreaterThanOrEqualTo(3); // name, price, stock
        
        // Verify each selector has required fields
        foreach (var s in selectors.EnumerateArray())
        {
            s.TryGetProperty("selector", out _).ShouldBeTrue();
            s.TryGetProperty("type", out _).ShouldBeTrue();
            s.TryGetProperty("description", out _).ShouldBeTrue();
        }
    }

    #endregion

    #region Change Analysis Tests

    [Test]
    public async Task ChangeAnalysis_SummarizesChanges()
    {
        // Arrange - Real captured change analysis
        var response = LlmFixtureManager.GetFixtureResponse("change-analysis");
        var mockHandler = new MockLlmHttpHandler().WithDefaultResponse(response);
        var (chain, _) = CreateLlmChain(mockHandler);

        // Act
        var result = await chain.ExecuteAsync("Summarize changes");

        // Assert
        result.IsSuccess.ShouldBeTrue();
        
        // Should mention price change
        (result.Content!.Contains("price") || result.Content!.Contains("Price")).ShouldBeTrue();
        // Should mention the values
        (result.Content.Contains("99.99") || result.Content.Contains("79.99")).ShouldBeTrue();
        
        Log($"Change summary: {result.Content}");
    }

    #endregion

    #region Multi-Response Flow Tests

    [Test]
    public async Task ContentAnalysisFlow_ProcessesMultipleResponses()
    {
        // Arrange - Queue responses for a typical content analysis flow
        var classificationResponse = LlmFixtureManager.GetFixtureResponse("event-list-classification");
        var intentResponse = LlmFixtureManager.GetFixtureResponse("intent-extraction");
        var sectionResponse = LlmFixtureManager.GetFixtureResponse("section-identification");
        
        var mockHandler = new MockLlmHttpHandler()
            .QueueResponse(classificationResponse)
            .QueueResponse(intentResponse)
            .QueueResponse(sectionResponse);

        var (chain, _) = CreateLlmChain(mockHandler);

        // Act - Simulate content analysis flow
        var classificationResult = await chain.ExecuteAsync("Classify");
        var intentResult = await chain.ExecuteAsync("Intent");
        var sectionsResult = await chain.ExecuteAsync("Sections");

        // Assert
        classificationResult.IsSuccess.ShouldBeTrue();
        classificationResult.Content!.ToLowerInvariant().ShouldContain("event");

        intentResult.IsSuccess.ShouldBeTrue();
        intentResult.Content.ShouldNotBeNullOrEmpty();

        sectionsResult.IsSuccess.ShouldBeTrue();
        sectionsResult.Content.ShouldNotBeNullOrEmpty();

        // Verify request capture
        mockHandler.CapturedRequests.Count.ShouldBe(3);
    }

    #endregion

    #region Available Fixtures Test

    [Test]
    public void AvailableFixtures_ContainsExpectedSet()
    {
        // Act
        var fixtures = LlmFixtureManager.GetAvailableFixtures().ToList();

        // Assert - Should have all our captured fixtures
        fixtures.ShouldContain("simple-greeting");
        fixtures.ShouldContain("price-extraction");
        fixtures.ShouldContain("content-classification");
        fixtures.ShouldContain("event-list-classification");
        fixtures.ShouldContain("intent-extraction");
        fixtures.ShouldContain("section-identification");
        fixtures.ShouldContain("selector-generation");
        fixtures.ShouldContain("multi-selector-generation");
        fixtures.ShouldContain("change-analysis");
        
        Log($"Available fixtures: {string.Join(", ", fixtures)}");
    }

    #endregion

    #region Helpers

    private static string StripMarkdownCodeBlock(string content)
    {
        // Remove ```json ... ``` wrapper if present
        // Also handle cases where LLM adds extra content after the first code block
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```"))
        {
            var endOfFirstLine = trimmed.IndexOf('\n');
            if (endOfFirstLine > 0)
                trimmed = trimmed[(endOfFirstLine + 1)..];
            
            // Find the closing ``` for the first code block only
            var closingBackticks = trimmed.IndexOf("```", StringComparison.Ordinal);
            if (closingBackticks >= 0)
                trimmed = trimmed[..closingBackticks];
        }
        return trimmed.Trim();
    }

    private (LlmProviderChain Chain, MockLlmHttpHandler Handler) CreateLlmChain(MockLlmHttpHandler mockHandler)
    {
        var providerRepo = new InMemoryRepository<LlmProviderConfig>();
        var usageRepo = new InMemoryRepository<LlmUsageRecord>();
        var logger = CreateLogger<LlmProviderChain>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var llmLogService = Substitute.For<ILlmLogService>();
        var httpClientFactory = new MockHttpClientFactory(mockHandler);

        // Configure a mock Ollama provider
        providerRepo.InsertAsync(new LlmProviderConfig
        {
            Id = Guid.NewGuid(),
            Name = "MockOllama",
            ProviderType = LlmProviderType.Ollama,
            Model = "ministral-3:3b",
            Endpoint = "http://localhost:11434",
            Priority = 1,
            IsEnabled = true,
            IsHealthy = true,
            TimeoutSeconds = 30
        }).Wait();

        var chain = new LlmProviderChain(
            providerRepo,
            usageRepo,
            logger,
            serviceProvider,
            llmLogService,
            httpClientFactory);

        return (chain, mockHandler);
    }

    #endregion
}
