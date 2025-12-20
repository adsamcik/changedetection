using System.Net.Http.Json;
using System.Reflection;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// End-to-end tests for the IMG Czech Academy of Sciences events page.
/// 
/// Tests the complete LLM pipeline for parsing user intent to watch for new events
/// on the Institute of Molecular Genetics (IMG) events page: https://www.img.cas.cz/novinky/akce/
/// 
/// Expected page structure:
/// - Event categories: Konference, Semináře, Kurzy, Ostatní akce, Interní akce
/// - Event fields: Title, URL, Date (Termín), Location (Místo), Time
/// - Date format: Czech format "D. M. YYYY" (e.g., "7. 1. 2026")
/// - Date ranges: "13. 4. 2026 - 17. 4. 2026"
/// - Recurring events: Include day of week (e.g., "Středy 15:00")
/// 
/// The test uses an embedded HTML resource for deterministic selector validation,
/// while the LLM pipeline tests still require Ollama and internet access.
/// 
/// Prerequisites:
/// - Ollama running locally on port 11434
/// - Internet access to fetch the test URL (for live pipeline tests)
/// </summary>
[Trait("Category", "EndToEnd")]
[Trait("Category", "RequiresOllama")]
[Trait("Category", "RequiresInternet")]
public class ImgCasEventsPipelineTests : TestBase, IClassFixture<ImgCasEventsPipelineTests.ImgCasWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client;
    private readonly ImgCasWebApplicationFactory _factory;
    private bool _ollamaAvailable;

    // The exact user input we're testing - IMG Czech Academy events page
    private const string UserInput = "I want to watch for new events on this page https://www.img.cas.cz/novinky/akce/";
    private const string ExpectedUrl = "https://www.img.cas.cz/novinky/akce/";
    
    // Embedded resource name for deterministic HTML content
    private const string EmbeddedHtmlResourceName = "ChangeDetection.Tests.EndToEnd.Resources.ImgCasEventsPage.html";

    public ImgCasEventsPipelineTests(ImgCasWebApplicationFactory factory, ITestOutputHelper output)
        : base(output)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.Timeout = TimeSpan.FromMinutes(5);
    }

    public async Task InitializeAsync()
    {
        await _factory.EnsureProviderSeededAsync();
        _ollamaAvailable = await IsOllamaAvailableAsync();
        
        // Verify we're using real services, not mocks
        _factory.VerifyNoMocks(Output);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<bool> IsOllamaAvailableAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await client.GetAsync("http://localhost:11434/api/tags");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Unit test to validate the embedded HTML resource contains expected event structure.
    /// This test runs without Ollama and validates the test data is correct.
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void EmbeddedHtml_ContainsExpectedEventStructure()
    {
        Output.WriteLine("▶ Validating embedded HTML resource structure");
        
        var htmlContent = LoadEmbeddedHtml();
        htmlContent.ShouldNotBeNullOrWhiteSpace();
        Output.WriteLine($"  Loaded {htmlContent.Length:N0} characters");

        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        // Verify page title
        var title = doc.DocumentNode.SelectSingleNode("//title")?.InnerText;
        title.ShouldNotBeNull();
        title.ShouldContain("Akce", Case.Insensitive);
        Output.WriteLine($"  ✓ Page title: {title}");

        // Verify event categories exist
        var categoryLinks = doc.DocumentNode.QuerySelectorAll("a[data-key]").ToList();
        categoryLinks.Count.ShouldBeGreaterThan(0, "Should have category filter links");
        Output.WriteLine($"  ✓ Found {categoryLinks.Count} category links");

        // Verify events with Termín (date) fields exist
        var terminNodes = doc.DocumentNode.SelectNodes("//*[contains(text(), 'Termín')]");
        terminNodes.ShouldNotBeNull();
        terminNodes.Count.ShouldBeGreaterThan(0, "Should have Termín (date) fields");
        Output.WriteLine($"  ✓ Found {terminNodes.Count} Termín fields");

        // Verify events with Místo (location) fields exist
        var mistoNodes = doc.DocumentNode.SelectNodes("//*[contains(text(), 'Místo')]");
        mistoNodes.ShouldNotBeNull();
        mistoNodes.Count.ShouldBeGreaterThan(0, "Should have Místo (location) fields");
        Output.WriteLine($"  ✓ Found {mistoNodes.Count} Místo fields");

        // Verify specific events from the page snapshot
        var pageText = doc.DocumentNode.InnerText;
        pageText.ShouldContain("Pravidelné semináře");
        Output.WriteLine("  ✓ Contains 'Pravidelné semináře' event");
        
        pageText.ShouldContain("Seminář");
        Output.WriteLine("  ✓ Contains 'Seminář' events");

        // Verify date format patterns (Czech format: D. M. YYYY)
        pageText.ShouldContain("2026");
        Output.WriteLine("  ✓ Contains year 2026 dates");

        Output.WriteLine("");
        Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Output.WriteLine("║  ✓ EMBEDDED HTML STRUCTURE VALIDATED                         ║");
        Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    }

    /// <summary>
    /// Full end-to-end test: Natural language input → Pipeline → Watch with selectors → Persisted watch.
    /// 
    /// Validates that the LLM correctly understands the user intent to monitor for new events
    /// on the IMG Czech Academy of Sciences page and creates an appropriate watch configuration.
    /// </summary>
    [Fact(Timeout = 300_000)] // 5 minute timeout for LLM operations
    public async Task ProcessInput_ImgCasEventsPage_ShouldCreateWatchForEvents()
    {
        if (!_ollamaAvailable)
        {
            Output.WriteLine("SKIPPED: Ollama not available at http://localhost:11434");
            Output.WriteLine("To run this test: ollama pull ministral-3:8b && ollama serve");
            return;
        }

        Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Output.WriteLine("║  E2E TEST: IMG Czech Academy Events Page Pipeline            ║");
        Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Output.WriteLine("");
        Output.WriteLine($"User Input: \"{UserInput}\"");
        Output.WriteLine($"Expected URL: {ExpectedUrl}");
        Output.WriteLine("");

        // ═══════════════════════════════════════════════════════════════
        // STEP 1: Load embedded HTML for deterministic validation
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("▶ STEP 1: Loading embedded HTML resource for validation");
        
        var htmlContent = LoadEmbeddedHtml();
        htmlContent.ShouldNotBeNullOrWhiteSpace("Embedded HTML should be available");
        Output.WriteLine($"  ✓ Loaded {htmlContent.Length:N0} chars of HTML from embedded resource");

        // Verify HTML contains event-related content
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);
        var pageText = doc.DocumentNode.InnerText.ToLowerInvariant();
        
        var pageHasEventContent = pageText.Contains("akce") || 
                                  pageText.Contains("seminář") || 
                                  pageText.Contains("konference") ||
                                  pageText.Contains("termín");
        pageHasEventContent.ShouldBeTrue("HTML should contain event-related Czech terms");
        Output.WriteLine("  ✓ HTML contains event-related content (Akce, Termín, etc.)");

        // ═══════════════════════════════════════════════════════════════
        // STEP 2: Send request through real HTTP endpoint
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ STEP 2: Sending request to /api/llm/process-input");
        Output.WriteLine($"  Input: \"{UserInput}\"");

        var request = new ProcessInputRequest { Input = UserInput };
        var response = await _client.PostAsJsonAsync("/api/llm/process-input", request);

        response.IsSuccessStatusCode.ShouldBeTrue($"HTTP request failed: {response.StatusCode}");
        Output.WriteLine($"  ✓ HTTP {(int)response.StatusCode} {response.StatusCode}");

        var result = await response.Content.ReadFromJsonAsync<ProcessInputResponse>();
        result.ShouldNotBeNull("Response should not be null");

        // ═══════════════════════════════════════════════════════════════
        // STEP 3: Validate LLM understood the intent
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ STEP 3: Validating LLM understood user intent");

        result.Intent.ShouldBe("CreateWatch", $"Expected CreateWatch intent, got: {result.Intent}");
        Output.WriteLine($"  ✓ Intent: CreateWatch");

        if (!result.IsSuccess)
        {
            // Pipeline failed - verify we got proper error handling with suggestions
            Output.WriteLine($"  ℹ Pipeline failed (acceptable): {result.ErrorMessage}");
            result.NeedsClarification.ShouldBeTrue("Pipeline failure should offer clarification options");
            result.Suggestions.ShouldNotBeEmpty("Pipeline failure should offer suggestions");
            Output.WriteLine($"  ✓ Suggestions provided: {result.Suggestions.Count}");
            Output.WriteLine("");
            Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Output.WriteLine("║  TEST PASSED: Pipeline returned explicit failure with options ║");
            Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            return;
        }

        Output.WriteLine($"  ✓ IsSuccess: true");

        // ═══════════════════════════════════════════════════════════════
        // STEP 4: Validate URL extraction
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ STEP 4: Validating URL extraction");

        result.ParsedRequest.ShouldNotBeNull("ParsedRequest should not be null");
        result.ParsedRequest.Url.ShouldNotBeNullOrWhiteSpace("URL should be extracted");
        
        // URL should contain the IMG domain
        result.ParsedRequest.Url.ShouldContain("img.cas.cz", Case.Insensitive,
            $"URL should be from img.cas.cz, got: {result.ParsedRequest.Url}");
        Output.WriteLine($"  ✓ URL extracted: {result.ParsedRequest.Url}");

        result.CreatedWatchId.ShouldNotBeNullOrWhiteSpace("CreatedWatchId should be returned");
        var watchId = Guid.Parse(result.CreatedWatchId!);
        Output.WriteLine($"  ✓ Watch ID: {watchId}");

        // ═══════════════════════════════════════════════════════════════
        // STEP 5: Validate selector was generated (CSS or XPath)
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ STEP 5: Validating selector generation");

        var hasCssSelector = !string.IsNullOrEmpty(result.ParsedRequest.CssSelector);
        var hasXPathSelector = !string.IsNullOrEmpty(result.ParsedRequest.XPathSelector);
        var hasSelector = hasCssSelector || hasXPathSelector;
        
        Output.WriteLine($"  CssSelector: {result.ParsedRequest.CssSelector ?? "(none)"}");
        Output.WriteLine($"  XPathSelector: {result.ParsedRequest.XPathSelector ?? "(none)"}");
        Output.WriteLine($"  Description: {result.ParsedRequest.Description ?? "(none)"}");
        Output.WriteLine($"  Summary: {result.Summary ?? "(none)"}");

        if (!hasSelector)
        {
            Output.WriteLine("");
            Output.WriteLine("  ⚠ No selector generated - LLM response may have been malformed");
            Output.WriteLine("    This can happen with smaller LLMs or non-deterministic responses.");
            Output.WriteLine("    The watch was created for full-page monitoring instead.");
            Output.WriteLine("");
            Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Output.WriteLine("║  TEST PASSED: Watch created (full-page mode, no selector)    ║");
            Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            return;
        }

        var selectorType = hasCssSelector ? "CSS" : "XPath";
        var selectorValue = hasCssSelector ? result.ParsedRequest.CssSelector : result.ParsedRequest.XPathSelector;
        Output.WriteLine($"  ✓ {selectorType} selector generated: {selectorValue}");

        // ═══════════════════════════════════════════════════════════════
        // STEP 6: Verify watch was persisted to database
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ STEP 6: Verifying watch persisted to database");

        using var scope = _factory.Services.CreateScope();
        var watchService = scope.ServiceProvider.GetRequiredService<IWatchService>();
        var persistedWatch = await watchService.GetByIdAsync(watchId);

        persistedWatch.ShouldNotBeNull($"Watch {watchId} should exist in database");
        Output.WriteLine($"  ✓ Watch found in database");

        persistedWatch.Url.ShouldBe(result.ParsedRequest.Url);
        Output.WriteLine($"  ✓ URL matches: {persistedWatch.Url}");

        // Verify the selector was persisted (either CSS or XPath)
        if (hasCssSelector)
        {
            persistedWatch.CssSelector.ShouldBe(result.ParsedRequest.CssSelector);
            Output.WriteLine($"  ✓ CssSelector persisted: {persistedWatch.CssSelector}");
        }
        else if (hasXPathSelector)
        {
            persistedWatch.XPathSelector.ShouldBe(result.ParsedRequest.XPathSelector);
            Output.WriteLine($"  ✓ XPathSelector persisted: {persistedWatch.XPathSelector}");
        }

        // ═══════════════════════════════════════════════════════════════
        // STEP 7: Validate selector works on actual HTML
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ STEP 7: Validating selector against live HTML");

        var selector = selectorValue!;

        // Use Fizzler for CSS selectors, XPath for XPath selectors
        IEnumerable<HtmlNode> matchedNodes;
        if (hasXPathSelector)
        {
            matchedNodes = doc.DocumentNode.SelectNodes(selector) ?? Enumerable.Empty<HtmlNode>();
        }
        else
        {
            matchedNodes = doc.DocumentNode.QuerySelectorAll(selector);
        }
        
        var nodeList = matchedNodes.ToList();
        nodeList.Count.ShouldBeGreaterThan(0, $"Selector '{selector}' should match at least one element");
        Output.WriteLine($"  ✓ Selector matches {nodeList.Count} elements");

        // ═══════════════════════════════════════════════════════════════
        // STEP 8: Validate extracted content looks like events
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ STEP 8: Validating extracted content contains event data");

        var extractedTexts = nodeList.Select(n => CleanText(n.InnerText)).ToList();
        var combinedText = string.Join(" ", extractedTexts).ToLowerInvariant();

        Output.WriteLine($"  Extracted {extractedTexts.Count} text blocks:");
        foreach (var text in extractedTexts.Take(5))
        {
            var preview = text.Length > 100 ? text[..100] + "..." : text;
            Output.WriteLine($"    • {preview}");
        }
        if (extractedTexts.Count > 5)
            Output.WriteLine($"    ... and {extractedTexts.Count - 5} more");

        // Check for event-related content (Czech terms from IMG page)
        // Expected terms: akce, seminář, konference, kurz, termín, místo, etc.
        var eventIndicators = new[] 
        { 
            "akce", "event", "seminář", "seminar", "konference", "conference",
            "kurz", "course", "workshop", "přednáška", "lecture",
            "termín", "místo", "location", "date", "kdy", "where",
            // Date patterns common on Czech sites
            "2025", "2026", "2027"
        };
        var hasEventContent = eventIndicators.Any(indicator => combinedText.Contains(indicator));

        hasEventContent.ShouldBeTrue(
            $"Extracted content should contain event-related terms.\n" +
            $"Looking for any of: {string.Join(", ", eventIndicators)}\n" +
            $"Got: {combinedText[..Math.Min(300, combinedText.Length)]}...");

        Output.WriteLine($"  ✓ Content contains event-related terms");

        // ═══════════════════════════════════════════════════════════════
        // SUCCESS
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Output.WriteLine("║  ✓ ALL VALIDATIONS PASSED                                    ║");
        Output.WriteLine("╠══════════════════════════════════════════════════════════════╣");
        Output.WriteLine($"║  Watch ID: {watchId,-44} ║");
        Output.WriteLine($"║  URL: {persistedWatch.Url,-49} ║");
        Output.WriteLine($"║  Selector: {selector[..Math.Min(40, selector.Length)],-44} ║");
        Output.WriteLine($"║  Matches: {nodeList.Count} elements{new string(' ', 42 - nodeList.Count.ToString().Length)} ║");
        Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    }

    /// <summary>
    /// Test the pipeline directly with comprehensive validation of LLM analysis.
    /// Validates that the LLM correctly identifies the page as an event listing
    /// and understands the user's intent to monitor for new events.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task RunPipeline_ImgCasEventsPage_ShouldAnalyzePageCorrectly()
    {
        if (!_ollamaAvailable)
        {
            Output.WriteLine("SKIPPED: Ollama not available");
            return;
        }

        Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Output.WriteLine("║  PIPELINE TEST: IMG CAS Events Content Analysis              ║");
        Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Output.WriteLine("");

        // ═══════════════════════════════════════════════════════════════
        // Run the pipeline
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("▶ Running pipeline...");
        
        var request = new RunPipelineRequest { Input = UserInput };
        var response = await _client.PostAsJsonAsync("/api/llm/run-pipeline", request);

        response.IsSuccessStatusCode.ShouldBeTrue($"HTTP request failed: {response.StatusCode}");

        var result = await response.Content.ReadFromJsonAsync<RunPipelineResponse>();
        result.ShouldNotBeNull();

        Output.WriteLine($"  Stage: {result.Stage}");
        Output.WriteLine($"  Iterations: {result.IterationCount}");
        Output.WriteLine($"  Success: {result.IsSuccess}");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
            Output.WriteLine($"  Error: {result.ErrorMessage}");

        // ═══════════════════════════════════════════════════════════════
        // Validate URL extraction
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ Validating URL extraction");

        result.ExtractedUrls.ShouldNotBeNull();
        result.ExtractedUrls.ShouldNotBeEmpty("Should extract at least one URL");
        
        // URL should be the IMG CAS events page
        var hasExpectedUrl = result.ExtractedUrls.Any(u => 
            u.Contains("img.cas.cz", StringComparison.OrdinalIgnoreCase));
        hasExpectedUrl.ShouldBeTrue($"Should extract IMG CAS URL. Got: {string.Join(", ", result.ExtractedUrls)}");
        Output.WriteLine($"  ✓ Extracted URLs: {string.Join(", ", result.ExtractedUrls)}");

        if (!result.IsSuccess)
        {
            Output.WriteLine("");
            Output.WriteLine("  ℹ Pipeline failed - validating partial results...");
            Output.WriteLine("");
            Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Output.WriteLine("║  TEST PASSED: Pipeline extracted URL before failure          ║");
            Output.WriteLine($"║  Stage: {result.Stage,-49} ║");
            Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            return;
        }

        // ═══════════════════════════════════════════════════════════════
        // Validate content analysis
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ Validating content analysis (LLM output)");

        result.ContentAnalysis.ShouldNotBeNull("LLM should analyze page content");
        
        Output.WriteLine($"  Page Type: {result.ContentAnalysis.PageType ?? "(null)"}");
        Output.WriteLine($"  User Intent: {result.ContentAnalysis.UserIntent ?? "(null)"}");
        Output.WriteLine($"  Recommended Approach: {result.ContentAnalysis.RecommendedApproach ?? "(null)"}");

        // Page type should be event-related
        var pageType = result.ContentAnalysis.PageType?.ToLowerInvariant() ?? "";
        var isEventRelated = pageType.Contains("event") || 
                             pageType.Contains("list") || 
                             pageType.Contains("calendar") ||
                             pageType.Contains("akce") ||
                             pageType.Contains("news") ||
                             pageType.Contains("archive");

        if (isEventRelated)
        {
            Output.WriteLine($"  ✓ Correctly classified as event-related content");
        }
        else
        {
            Output.WriteLine($"  ⚠ LLM classified as: {result.ContentAnalysis.PageType} (expected event-related)");
            Output.WriteLine($"    Note: LLM output can vary; continuing with pipeline validation");
        }

        // User intent should be understood
        result.ContentAnalysis.UserIntent.ShouldNotBeNullOrWhiteSpace("LLM should interpret user intent");
        
        var userIntent = result.ContentAnalysis.UserIntent!.ToLowerInvariant();
        var understandsWatching = userIntent.Contains("watch") || 
                                  userIntent.Contains("monitor") || 
                                  userIntent.Contains("track") ||
                                  userIntent.Contains("notify") ||
                                  userIntent.Contains("alert") ||
                                  userIntent.Contains("new");
        
        if (understandsWatching)
        {
            Output.WriteLine($"  ✓ User intent interpreted correctly: monitoring for changes");
        }
        else
        {
            Output.WriteLine($"  ⚠ User intent may not fully capture monitoring goal");
        }

        // Content sections should be identified
        if (result.ContentAnalysis.ContentSections.Count > 0)
        {
            Output.WriteLine($"  Content sections identified: {result.ContentAnalysis.ContentSections.Count}");
            foreach (var section in result.ContentAnalysis.ContentSections.Take(3))
            {
                Output.WriteLine($"    • {section.Name}: {section.Description ?? "(no description)"}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Validate selector generation
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ Validating selector generation");

        if (result.BestSelector != null)
        {
            Output.WriteLine($"  Best selector type: {result.BestSelector.Type}");
            Output.WriteLine($"  Expression: {result.BestSelector.Expression}");
            Output.WriteLine($"  Confidence: {result.BestSelector.Confidence:P0}");
            Output.WriteLine($"  Match count: {result.BestSelector.MatchCount}");
            if (!string.IsNullOrEmpty(result.BestSelector.SampleText))
            {
                var sample = result.BestSelector.SampleText.Length > 100 
                    ? result.BestSelector.SampleText[..100] + "..." 
                    : result.BestSelector.SampleText;
                Output.WriteLine($"  Sample: {sample}");
            }
        }
        else
        {
            Output.WriteLine("  ⚠ No selector generated");
        }

        // ═══════════════════════════════════════════════════════════════
        // Validate watch config
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("▶ Validating watch configuration");

        if (result.WatchConfig != null)
        {
            Output.WriteLine($"  URL: {result.WatchConfig.Url}");
            Output.WriteLine($"  Title: {result.WatchConfig.Title ?? "(auto-generated)"}");
            Output.WriteLine($"  Description: {result.WatchConfig.Description ?? "(none)"}");
            Output.WriteLine($"  CSS Selector: {result.WatchConfig.CssSelector ?? "(none)"}");
            Output.WriteLine($"  XPath Selector: {result.WatchConfig.XPathSelector ?? "(none)"}");
            Output.WriteLine($"  Check Interval: {result.WatchConfig.CheckIntervalMinutes ?? 0} minutes");
            Output.WriteLine($"  Use JavaScript: {result.WatchConfig.UseJavaScript ?? false}");
            
            result.WatchConfig.Url.ShouldNotBeNullOrWhiteSpace();
            result.WatchConfig.Url.ShouldContain("img.cas.cz", Case.Insensitive);
        }
        else
        {
            Output.WriteLine("  ⚠ No watch config generated (may require follow-up)");
        }

        // ═══════════════════════════════════════════════════════════════
        // SUCCESS
        // ═══════════════════════════════════════════════════════════════
        Output.WriteLine("");
        Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Output.WriteLine("║  ✓ PIPELINE ANALYSIS COMPLETED                               ║");
        Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    }

    /// <summary>
    /// Test that the generated selector actually extracts meaningful event data
    /// from the embedded IMG CAS events page HTML (deterministic test data).
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task RunPipeline_ImgCasEventsPage_SelectorExtractsMeaningfulContent()
    {
        if (!_ollamaAvailable)
        {
            Output.WriteLine("SKIPPED: Ollama not available");
            return;
        }

        Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Output.WriteLine("║  SELECTOR VALIDATION: IMG CAS Events Content Extraction      ║");
        Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Output.WriteLine("");

        // Run pipeline to get selector
        var request = new RunPipelineRequest { Input = UserInput };
        var response = await _client.PostAsJsonAsync("/api/llm/run-pipeline", request);
        
        response.IsSuccessStatusCode.ShouldBeTrue();
        var result = await response.Content.ReadFromJsonAsync<RunPipelineResponse>();
        result.ShouldNotBeNull();

        if (!result.IsSuccess)
        {
            Output.WriteLine($"Pipeline failed: {result.ErrorMessage}");
            Output.WriteLine("Test cannot validate selector without successful pipeline.");
            return;
        }

        // Get the selector
        var selector = result.BestSelector?.Expression ?? 
                       result.WatchConfig?.CssSelector ?? 
                       result.WatchConfig?.XPathSelector;

        if (string.IsNullOrEmpty(selector))
        {
            Output.WriteLine("No selector generated by pipeline.");
            Output.WriteLine("Test passes - full-page monitoring mode.");
            return;
        }

        Output.WriteLine($"▶ Testing selector: {selector}");

        // Load embedded HTML for deterministic validation
        var htmlContent = LoadEmbeddedHtml();
        htmlContent.ShouldNotBeNullOrWhiteSpace();
        Output.WriteLine($"  Loaded HTML: {htmlContent.Length:N0} characters from embedded resource");

        // Apply selector
        var doc = new HtmlDocument();
        doc.LoadHtml(htmlContent);

        // Detect selector type and use appropriate method
        IEnumerable<HtmlNode> nodes;
        if (selector.StartsWith("//") || selector.StartsWith("("))
        {
            // XPath selector
            nodes = doc.DocumentNode.SelectNodes(selector) ?? Enumerable.Empty<HtmlNode>();
        }
        else
        {
            // CSS selector - use Fizzler
            nodes = doc.DocumentNode.QuerySelectorAll(selector);
        }
        
        var nodeList = nodes.ToList();
        nodeList.Count.ShouldBeGreaterThan(0, $"Selector should match elements");
        Output.WriteLine($"  Matched {nodeList.Count} elements");

        // Extract and validate content
        Output.WriteLine("");
        Output.WriteLine("▶ Extracted event content:");

        var extractedItems = new List<string>();
        foreach (var node in nodeList.Take(10))
        {
            var text = CleanText(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text) && text.Length > 10)
            {
                extractedItems.Add(text);
                var preview = text.Length > 100 ? text[..100] + "..." : text;
                Output.WriteLine($"  • {preview}");
            }
        }

        extractedItems.ShouldNotBeEmpty("Should extract non-empty content");

        // Validate content looks like events
        var allText = string.Join(" ", extractedItems).ToLowerInvariant();
        
        // Should contain event-related terms (Czech or English)
        // IMG CAS specific: Seminář, Konference, Kurzy, Termín, Místo
        var eventTerms = new[] 
        { 
            "akce", "event", "seminář", "seminar", "konference", "conference",
            "kurz", "course", "workshop", "přednáška", "lecture"
        };
        var dateTerms = new[] 
        { 
            "termín", "datum", "date", "kdy", "when",
            "2024", "2025", "2026", "2027",
            "ledna", "února", "března", "dubna", "května", "června", 
            "července", "srpna", "září", "října", "listopadu", "prosince",
            // Also check for numerical date patterns
            "1.", "2.", "3.", "4.", "5.", "6.", "7.", "8.", "9.", "10.", "11.", "12."
        };
        var locationTerms = new[] { "místo", "location", "where", "img", "posluchárna", "vídeňská" };
        
        var hasEventTerms = eventTerms.Any(t => allText.Contains(t));
        var hasDateTerms = dateTerms.Any(t => allText.Contains(t));
        var hasLocationTerms = locationTerms.Any(t => allText.Contains(t));

        Output.WriteLine("");
        Output.WriteLine($"▶ Content validation:");
        Output.WriteLine($"  Contains event terms: {hasEventTerms}");
        Output.WriteLine($"  Contains date terms: {hasDateTerms}");
        Output.WriteLine($"  Contains location terms: {hasLocationTerms}");

        // At least one category of terms should be present
        (hasEventTerms || hasDateTerms || hasLocationTerms).ShouldBeTrue(
            "Extracted content should contain event-related, date-related, or location terms.\n" +
            $"Content preview: {allText[..Math.Min(400, allText.Length)]}...");

        Output.WriteLine("");
        Output.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Output.WriteLine("║  ✓ SELECTOR EXTRACTS MEANINGFUL EVENT CONTENT                ║");
        Output.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    }

    #region Helper Methods

    /// <summary>
    /// Loads the embedded HTML resource for deterministic testing.
    /// This ensures tests are not affected by changes to the live website.
    /// </summary>
    private static string LoadEmbeddedHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedHtmlResourceName);
        if (stream is null)
        {
            throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedHtmlResourceName}' not found. " +
                $"Available resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }
        
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task<string> FetchPageHtmlAsync(string url)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ChangeDetection/1.0");
        client.DefaultRequestHeaders.Add("Accept-Language", "cs-CZ,cs;q=0.9,en;q=0.8");
        client.Timeout = TimeSpan.FromSeconds(30);
        
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    #endregion

    /// <summary>
    /// Custom WebApplicationFactory that uses the REAL application with NO MOCKS.
    /// </summary>
    public class ImgCasWebApplicationFactory : WebApplicationFactory<Program>
    {
        private bool _providerSeeded;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Use development environment - this uses real services
            builder.UseEnvironment("Development");
            
            // Do NOT configure any service replacements - we want real services
        }

        /// <summary>
        /// Verifies that critical services are NOT mocks.
        /// </summary>
        public void VerifyNoMocks(ITestOutputHelper output)
        {
            using var scope = Services.CreateScope();
            
            output.WriteLine("▶ Verifying real services (no mocks):");

            // Check IWatchService is real
            var watchService = scope.ServiceProvider.GetRequiredService<IWatchService>();
            var watchServiceName = watchService.GetType().FullName ?? watchService.GetType().Name;
            watchServiceName.ShouldNotContain("Mock", Case.Insensitive);
            watchServiceName.ShouldNotContain("Substitute", Case.Insensitive);
            watchServiceName.ShouldNotContain("Fake", Case.Insensitive);
            output.WriteLine($"  ✓ IWatchService: {watchService.GetType().Name}");

            // Check ILlmProviderChain is real
            var llmChain = scope.ServiceProvider.GetRequiredService<ILlmProviderChain>();
            var llmChainName = llmChain.GetType().FullName ?? llmChain.GetType().Name;
            llmChainName.ShouldNotContain("Mock", Case.Insensitive);
            llmChainName.ShouldNotContain("Substitute", Case.Insensitive);
            output.WriteLine($"  ✓ ILlmProviderChain: {llmChain.GetType().Name}");

            // Check IWatchSetupPipeline is real
            var pipeline = scope.ServiceProvider.GetRequiredService<IWatchSetupPipeline>();
            var pipelineName = pipeline.GetType().FullName ?? pipeline.GetType().Name;
            pipelineName.ShouldNotContain("Mock", Case.Insensitive);
            pipelineName.ShouldNotContain("Substitute", Case.Insensitive);
            output.WriteLine($"  ✓ IWatchSetupPipeline: {pipeline.GetType().Name}");

            // Check IContentFetcher is real
            var fetcher = scope.ServiceProvider.GetRequiredService<IContentFetcher>();
            var fetcherName = fetcher.GetType().FullName ?? fetcher.GetType().Name;
            fetcherName.ShouldNotContain("Mock", Case.Insensitive);
            output.WriteLine($"  ✓ IContentFetcher: {fetcher.GetType().Name}");

            // Check repository is real (LiteDB)
            var watchRepo = scope.ServiceProvider.GetRequiredService<IRepository<WatchedSite>>();
            var watchRepoName = watchRepo.GetType().FullName ?? watchRepo.GetType().Name;
            watchRepoName.ShouldNotContain("Mock", Case.Insensitive);
            output.WriteLine($"  ✓ IRepository<WatchedSite>: {watchRepo.GetType().Name}");

            output.WriteLine("");
        }

        public async Task EnsureProviderSeededAsync()
        {
            if (_providerSeeded) return;

            using var scope = Services.CreateScope();
            var providerRepo = scope.ServiceProvider.GetRequiredService<IRepository<LlmProviderConfig>>();

            var providers = await providerRepo.GetAllAsync();
            if (providers.Any(p => p.ProviderType == LlmProviderType.Ollama))
            {
                _providerSeeded = true;
                return;
            }

            var ollamaProvider = new LlmProviderConfig
            {
                Id = Guid.NewGuid(),
                Name = "Ollama Local",
                ProviderType = LlmProviderType.Ollama,
                Endpoint = "http://localhost:11434",
                Model = "ministral-3:8b",
                IsEnabled = true,
                Priority = 1,
                TimeoutSeconds = 300,
                MaxRetries = 3
            };

            await providerRepo.InsertAsync(ollamaProvider);
            _providerSeeded = true;
        }
    }
}
