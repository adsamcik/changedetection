using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Pipeline;
using NSubstitute;
using Shouldly;
using System.Runtime.CompilerServices;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Deterministic pipeline tests using mocked LLM responses.
/// 
/// These tests verify the ContentAnalysisStage and SelectorGenerationStage
/// work correctly with predictable LLM responses, without requiring a live Ollama server.
/// 
/// Tests cover:
/// - Content type classification
/// - User intent extraction
/// - Section identification
/// - Selector generation
/// - Full pipeline flow
/// </summary>
[Category("Unit")]
public class FixtureBasedPipelineTests : TestBase
{
    #region Sample Content

    /// <summary>
    /// Sample event page HTML content for testing.
    /// </summary>
    private static FetchedContent GetSampleEventPageContent() => new()
    {
        Url = "https://www.img.cas.cz/novinky/akce/",
        IsSuccess = true,
        Title = "Akce - IMG CAS",
        Html = """
            <!DOCTYPE html>
            <html lang="cs">
            <head><title>Akce - IMG CAS</title></head>
            <body>
            <div class="layout layout--wide">
                <div class="container">
                    <div class="wp-editor wp-editor--primary">
                        <h1 class="wp-block-heading">Akce</h1>
                        <div class="lg:flex lg:-ml-4 lg:flex-wrap">
                            <div class="mb-6 lg:mb-9 lg:w-1/3 lg:pl-4">
                                <div class="shadow bg-white">
                                    <a class="group" href="https://example.com/event1">
                                        <h3>Pravidelné semináře</h3>
                                    </a>
                                    <ul>
                                        <li><strong>Termín</strong> 1. 10. 2025 - 24. 6. 2026 | Středy 15:00</li>
                                        <li><strong>Místo</strong> IMG, Posluchárna Milana Haška</li>
                                    </ul>
                                </div>
                            </div>
                            <div class="mb-6 lg:mb-9 lg:w-1/3 lg:pl-4">
                                <div class="shadow bg-white">
                                    <a class="group" href="https://example.com/event2">
                                        <h3>Seminář – Tomáš Venit</h3>
                                    </a>
                                    <ul>
                                        <li><strong>Termín</strong> 3. 12. 2025 | 15:00</li>
                                        <li><strong>Místo</strong> IMG, Posluchárna Milana Haška</li>
                                    </ul>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>
            </div>
            </body>
            </html>
            """,
        TextContent = """
            Akce
            Pravidelné semináře
            Termín 1. 10. 2025 - 24. 6. 2026 | Středy 15:00
            Místo IMG, Posluchárna Milana Haška
            Seminář – Tomáš Venit
            Termín 3. 12. 2025 | 15:00
            Místo IMG, Posluchárna Milana Haška
            """
    };

    #endregion

    #region Content Classification Tests

    [Test]
    public async Task ContentAnalysis_ClassifiesEventListPage()
    {
        // Arrange
        var content = GetSampleEventPageContent();
        var userIntent = "I want to watch for the events on that page";
        
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            "EventList",
            "Monitor new events and seminars on the page",
            "[]",
            """[{"name":"Event Cards","selector":".shadow.bg-white","isTarget":true,"description":"Event cards"}]"""
        ]);
        
        var stage = new ContentAnalysisStage(llmChain, CreateLogger<ContentAnalysisStage>());

        // Act
        var result = await stage.AnalyzeAsync(content, userIntent);

        // Assert
        result.ContentType.ShouldBe(ContentType.EventList);
        result.UserIntent.ShouldNotBeNullOrWhiteSpace();
        result.IdentifiedSections.ShouldNotBeEmpty();
        result.IdentifiedSections.Any(s => s.IsLikelyTarget).ShouldBeTrue();
        
        Log($"Content Type: {result.ContentType}");
        Log($"User Intent: {result.UserIntent}");
        Log($"Sections: {result.IdentifiedSections.Count}");
    }

    [Test]
    public async Task ContentAnalysis_ExtractsUserIntent()
    {
        // Arrange
        var content = GetSampleEventPageContent();
        var userIntent = "I want to watch for new events and seminars";
        
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            "EventList",
            "Monitor new events and seminars on the page",
            "[]",
            """[{"name":"Events","selector":".event","isTarget":true,"description":"Events section"}]"""
        ]);
        
        var stage = new ContentAnalysisStage(llmChain, CreateLogger<ContentAnalysisStage>());

        // Act
        var result = await stage.AnalyzeAsync(content, userIntent);

        // Assert
        result.UserIntent.ShouldNotBeNullOrWhiteSpace();
        var intentLower = result.UserIntent.ToLowerInvariant();
        
        // Should mention events, seminars, or monitoring
        var hasEventRelatedTerm = intentLower.Contains("event") || 
                                   intentLower.Contains("seminar") ||
                                   intentLower.Contains("monitor");
        hasEventRelatedTerm.ShouldBeTrue($"Expected event-related intent, got: {result.UserIntent}");
        
        Log($"Extracted intent: {result.UserIntent}");
    }

    [Test]
    public async Task ContentAnalysis_IdentifiesSections()
    {
        // Arrange
        var content = GetSampleEventPageContent();
        var userIntent = "Watch for new events";
        
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            "EventList",
            "Monitor new events",
            "[]",
            """[{"name":"Event Cards","selector":".shadow.bg-white","isTarget":true,"description":"Event cards"},{"name":"Event Titles","selector":"h3","isTarget":false,"description":"Titles"}]"""
        ]);
        
        var stage = new ContentAnalysisStage(llmChain, CreateLogger<ContentAnalysisStage>());

        // Act
        var result = await stage.AnalyzeAsync(content, userIntent);

        // Assert
        result.IdentifiedSections.ShouldNotBeEmpty();
        
        foreach (var section in result.IdentifiedSections)
        {
            section.Name.ShouldNotBeNullOrWhiteSpace();
            Log($"Section: {section.Name} - Selector: {section.SuggestedSelector} - Target: {section.IsLikelyTarget}");
        }
        
        // At least one section should be marked as target
        result.IdentifiedSections.Any(s => s.IsLikelyTarget).ShouldBeTrue();
    }

    #endregion

    #region Selector Generation Tests

    [Test]
    public async Task SelectorGeneration_CreatesEventSelectors()
    {
        // Arrange
        var content = GetSampleEventPageContent();
        var analysis = new ContentAnalysis
        {
            ContentType = ContentType.EventList,
            UserIntent = "Monitor new events and seminars",
            PageDescription = "Event listing page",
            Confidence = 0.8f,
            RecommendedApproach = MonitoringApproach.SpecificSelector,
            IdentifiedSections =
            [
                new PageSection
                {
                    Name = "Event Cards",
                    SuggestedSelector = ".shadow.bg-white",
                    IsLikelyTarget = true,
                    Description = "Individual event cards"
                }
            ]
        };
        
        var llmChain = CreateMockLlmChainWithResponse(
            """[{"selector":".shadow.bg-white","type":"CssSelector","description":"Event cards","reasoning":"Stable selector for events"}]""");
        
        var stage = new SelectorGenerationStage(llmChain, CreatePassThroughDomCompactor(), CreateLogger<SelectorGenerationStage>());

        // Act
        var selectors = await stage.GenerateSelectorsAsync(content, analysis);

        // Assert
        selectors.ShouldNotBeEmpty();
        
        foreach (var selector in selectors)
        {
            selector.Selector.ShouldNotBeNullOrWhiteSpace();
            Log($"[{selector.Type}] {selector.Selector} - {selector.Description}");
        }
        
        // Should have at least one CSS selector
        selectors.Any(s => s.Type == SelectorType.CssSelector).ShouldBeTrue();
    }

    [Test]
    public async Task SelectorGeneration_IncludesSelectorsFromAnalysis()
    {
        // Arrange - Analysis has a suggested selector
        var content = GetSampleEventPageContent();
        var analysis = new ContentAnalysis
        {
            ContentType = ContentType.EventList,
            UserIntent = "Monitor new events",
            PageDescription = "Event listing page",
            Confidence = 0.8f,
            RecommendedApproach = MonitoringApproach.SpecificSelector,
            IdentifiedSections =
            [
                new PageSection
                {
                    Name = "Event Cards",
                    SuggestedSelector = ".event-card-from-analysis",
                    IsLikelyTarget = true,
                    Description = "Event cards identified during analysis"
                }
            ]
        };
        
        var llmChain = CreateMockLlmChainWithResponse(
            """[{"selector":".llm-generated","type":"CssSelector","description":"LLM selector","reasoning":"Generated"}]""");
        
        var stage = new SelectorGenerationStage(llmChain, CreatePassThroughDomCompactor(), CreateLogger<SelectorGenerationStage>());

        // Act
        var selectors = await stage.GenerateSelectorsAsync(content, analysis);

        // Assert
        // Should include the selector from analysis
        selectors.Any(s => s.Selector == ".event-card-from-analysis").ShouldBeTrue(
            "Should include selector from content analysis");
        
        // Should also include LLM-generated selectors
        selectors.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    #endregion

    #region Full Pipeline Flow Tests

    [Test]
    public async Task FullPipeline_AnalyzesContentAndGeneratesSelectors()
    {
        // Arrange
        var content = GetSampleEventPageContent();
        var userIntent = "I want to watch for the events on that page";
        
        // Content analysis needs 4 responses (classification, intent, filter keywords, sections)
        var analysisChain = CreateMockLlmChainWithStreamingResponses([
            "EventList",
            "Monitor new events and seminars",
            "[]",
            """[{"name":"Event Cards","selector":".shadow.bg-white","isTarget":true,"description":"Event cards"}]"""
        ]);
        
        var analysisStage = new ContentAnalysisStage(analysisChain, CreateLogger<ContentAnalysisStage>());

        // Act - Step 1: Content Analysis
        Log("=== STEP 1: Content Analysis ===");
        var analysis = await analysisStage.AnalyzeAsync(content, userIntent);
        
        Log($"Content Type: {analysis.ContentType}");
        Log($"User Intent: {analysis.UserIntent}");
        Log($"Recommended Approach: {analysis.RecommendedApproach}");
        Log($"Confidence: {analysis.Confidence:P0}");
        
        // Arrange for selector generation
        var selectorChain = CreateMockLlmChainWithResponse(
            """[{"selector":".shadow.bg-white","type":"CssSelector","description":"Event cards","reasoning":"Stable selector"}]""");
        
        var selectorStage = new SelectorGenerationStage(selectorChain, CreatePassThroughDomCompactor(), CreateLogger<SelectorGenerationStage>());

        // Act - Step 2: Selector Generation
        Log("");
        Log("=== STEP 2: Selector Generation ===");
        var selectors = await selectorStage.GenerateSelectorsAsync(content, analysis);
        
        foreach (var selector in selectors)
        {
            Log($"[{selector.Type}] {selector.Selector}");
            Log($"  Description: {selector.Description}");
            Log($"  Confidence: {selector.Confidence:P0}");
        }

        // Assert
        analysis.ContentType.ShouldBe(ContentType.EventList);
        analysis.IdentifiedSections.ShouldNotBeEmpty();
        selectors.ShouldNotBeEmpty();
        
        // At least one selector should target events
        var hasEventSelector = selectors.Any(s =>
        {
            var selectorLower = s.Selector.ToLowerInvariant();
            var descLower = (s.Description ?? "").ToLowerInvariant();
            
            return selectorLower.Contains("shadow") ||
                   selectorLower.Contains("event") ||
                   selectorLower.Contains("h3") ||
                   descLower.Contains("event");
        });
        
        hasEventSelector.ShouldBeTrue("Should have at least one event-related selector");
    }

    [Test]
    public async Task ContentAnalysis_StreamingProgress_ReportsSteps()
    {
        // Arrange
        var content = GetSampleEventPageContent();
        var userIntent = "Watch for new events";
        
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            "EventList",
            "Monitor events",
            "[]",
            """[{"name":"Events","selector":".event","isTarget":true,"description":"Events"}]"""
        ]);
        
        var stage = new ContentAnalysisStage(llmChain, CreateLogger<ContentAnalysisStage>());

        // Act
        var progressSteps = new List<string>();
        ContentAnalysis? finalResult = null;
        
        await foreach (var progress in stage.AnalyzeStreamingAsync(content, userIntent))
        {
            progressSteps.Add($"{progress.Step}:{progress.Status}");
            if (progress.Result != null)
                finalResult = progress.Result;
        }

        // Assert
        progressSteps.ShouldContain("ContentClassification:Starting");
        progressSteps.ShouldContain("ContentClassification:Completed");
        progressSteps.ShouldContain("IntentExtraction:Starting");
        progressSteps.ShouldContain("IntentExtraction:Completed");
        progressSteps.ShouldContain("FilterKeywordExtraction:Starting");
        progressSteps.ShouldContain("FilterKeywordExtraction:Completed");
        progressSteps.ShouldContain("SectionIdentification:Starting");
        progressSteps.ShouldContain("SectionIdentification:Completed");
        progressSteps.ShouldContain("Complete:Completed");
        
        finalResult.ShouldNotBeNull();
        
        Log($"Progress steps: {string.Join(" -> ", progressSteps)}");
    }

    #endregion

    #region Error Handling Tests

    [Test]
    public async Task ContentAnalysis_HandlesMalformedJsonGracefully()
    {
        // Arrange - Return malformed JSON for sections
        var content = GetSampleEventPageContent();
        var userIntent = "Watch for events";
        
        var llmChain = CreateMockLlmChainWithStreamingResponses([
            "EventList",
            "Monitor new events",
            "[]",
            "Not valid JSON at all"
        ]);
        
        var stage = new ContentAnalysisStage(llmChain, CreateLogger<ContentAnalysisStage>());

        // Act
        var result = await stage.AnalyzeAsync(content, userIntent);

        // Assert - Should still return a result, just with empty sections
        result.ShouldNotBeNull();
        result.ContentType.ShouldBe(ContentType.EventList);
        result.IdentifiedSections.ShouldBeEmpty(); // Gracefully handled
        
        Log("Gracefully handled malformed JSON");
    }

    [Test]
    public async Task SelectorGeneration_HandlesEmptyLlmResponse()
    {
        // Arrange
        var content = GetSampleEventPageContent();
        var analysis = new ContentAnalysis
        {
            ContentType = ContentType.EventList,
            UserIntent = "Monitor events",
            PageDescription = "Events page",
            Confidence = 0.8f,
            RecommendedApproach = MonitoringApproach.SpecificSelector,
            IdentifiedSections = []
        };
        
        var llmChain = CreateMockLlmChainWithResponse("");
        
        var stage = new SelectorGenerationStage(llmChain, CreatePassThroughDomCompactor(), CreateLogger<SelectorGenerationStage>());

        // Act
        var selectors = await stage.GenerateSelectorsAsync(content, analysis);

        // Assert - Should return empty list, not throw
        selectors.ShouldBeEmpty();
        
        Log("Gracefully handled empty LLM response");
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Creates a mock ILlmProviderChain that returns the specified response for ExecuteAsync.
    /// </summary>
    private static ILlmProviderChain CreateMockLlmChainWithResponse(string response)
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        
        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LlmResponse
            {
                IsSuccess = true,
                Content = response,
                ProviderUsed = "MockProvider",
                Model = "mock-model"
            }));

        return llmChain;
    }

    /// <summary>
    /// Creates a mock ILlmProviderChain that returns streaming responses in sequence.
    /// ContentAnalysisStage makes 3 streaming calls: classification, intent, sections.
    /// </summary>
    private static ILlmProviderChain CreateMockLlmChainWithStreamingResponses(string[] responses)
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        var responseIndex = 0;

        // Mock ExecuteStreamingAsync to return responses in sequence
        llmChain.ExecuteStreamingAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var currentIndex = responseIndex++;
                var content = currentIndex < responses.Length ? responses[currentIndex] : "";
                return CreateAsyncEnumerable(content);
            });

        return llmChain;
    }

    /// <summary>
    /// Creates an async enumerable that yields LLM stream chunks for a single response.
    /// </summary>
    private static async IAsyncEnumerable<LlmStreamChunk> CreateAsyncEnumerable(
        string content,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield(); // Make it truly async
        
        yield return new LlmStreamChunk
        {
            Type = LlmStreamChunkType.Start,
            ProviderName = "MockProvider",
            Model = "mock-model"
        };

        // Return content in one chunk
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
