using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Tests.Llm.Cache;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// Pipeline baseline assessment across diverse real-world websites.
/// 
/// Tests the full watch-setup pipeline against 4 URL categories:
///   1. Amazon DE — E-commerce product (price, EUR, tracking params)
///   2. Wowhead — Gaming wiki/database (dynamic, JS-heavy)
///   3. Czech Wikipedia — Non-English wiki (encoded URL, Czech language)
///   4. Veeam Careers — Job listings (list extraction, filtering)
///
/// Each URL is tested with 3-5 synthetic user prompts representing
/// realistic monitoring goals. All results are logged for grading.
///
/// Uses CachingWebApplicationFactory for LLM + content caching.
/// Run with: ./test.ps1 -Filter "*PipelineBaseline*" -IncludeLlm -IncludeInternet -TailLines 0
/// </summary>
[Category("EndToEnd")]
[Category("LlmCached")]
[Category("RequiresInternet")]
public class PipelineBaselineE2ETests : TestBase, IAsyncDisposable
{
    private HttpClient _client = null!;
    private BaselineWebApplicationFactory _factory = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _factory = new BaselineWebApplicationFactory();
        _client = _factory.CreateClient();
        _client.Timeout = TimeSpan.FromMinutes(5);
        await _factory.EnsureProviderSeededAsync();

        Log($"=== LLM Cache Mode: {_factory.LlmCacheMode} ===");
        Log($"=== Content Cache Mode: {_factory.ContentCacheMode} ===");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    // Amazon DE — E-commerce product page
    // ═══════════════════════════════════════════════════════════════

    private const string AmazonUrl = "https://www.amazon.de/-/en/JBL-Bluetooth-Headphones-Intelligent-Cancelling-black/dp/B0DDTVL8V2/?_encoding=UTF8&pd_rd_w=EkXhr&content-id=amzn1.sym.79d1f343-1e12-4a57-8f6f-6e5712b5effc&pf_rd_p=79d1f343-1e12-4a57-8f6f-6e5712b5effc&pf_rd_r=4DSJ5XYMAPTY4G5D655M&pd_rd_wg=iESS1&pd_rd_r=a4f3844f-cfcc-44ce-ba61-60e4b328fca6";

    [Test]
    public async Task Amazon_WatchForPriceDrops()
    {
        await RunPipelineAndLog(
            $"Watch this JBL headphone page for price drops {AmazonUrl}",
            "amazon-price-drops");
    }

    [Test]
    public async Task Amazon_TrackPriceBelowThreshold()
    {
        await RunPipelineAndLog(
            $"Let me know when the JBL Tune 760NC goes below €80 {AmazonUrl}",
            "amazon-price-threshold");
    }

    [Test]
    public async Task Amazon_TrackAvailability()
    {
        await RunPipelineAndLog(
            $"Track availability of {AmazonUrl} — I want to know when it's back in stock",
            "amazon-availability");
    }

    [Test]
    public async Task Amazon_MonitorAnyChanges()
    {
        await RunPipelineAndLog(
            $"Monitor this product for any changes {AmazonUrl}",
            "amazon-any-changes");
    }

    // ═══════════════════════════════════════════════════════════════
    // Wowhead — Gaming wiki/database
    // ═══════════════════════════════════════════════════════════════

    private const string WowheadUrl = "https://www.wowhead.com/";

    [Test]
    public async Task Wowhead_WatchForNewNews()
    {
        await RunPipelineAndLog(
            $"Watch {WowheadUrl} for new front page news articles",
            "wowhead-news");
    }

    [Test]
    public async Task Wowhead_TrackLatestGuides()
    {
        await RunPipelineAndLog(
            $"Track the latest guides posted on {WowheadUrl}",
            "wowhead-guides");
    }

    [Test]
    public async Task Wowhead_PatchNotes()
    {
        await RunPipelineAndLog(
            $"I want to know when new patch notes appear on {WowheadUrl}",
            "wowhead-patch-notes");
    }

    // ═══════════════════════════════════════════════════════════════
    // Czech Wikipedia — Non-English, encoded URL
    // ═══════════════════════════════════════════════════════════════

    private const string CzechWikiUrl = "https://cs.wikipedia.org/wiki/Hlavn%C3%AD_strana";

    [Test]
    public async Task CzechWiki_MonitorFeaturedArticle()
    {
        await RunPipelineAndLog(
            $"Monitor the featured article on Czech Wikipedia's main page {CzechWikiUrl}",
            "czwiki-featured");
    }

    [Test]
    public async Task CzechWiki_WatchSelectedArticleSection()
    {
        await RunPipelineAndLog(
            $"Watch {CzechWikiUrl} for changes to the selected article section",
            "czwiki-selected-section");
    }

    [Test]
    public async Task CzechWiki_TrackAnyNewContent()
    {
        await RunPipelineAndLog(
            $"Track {CzechWikiUrl} for any new content",
            "czwiki-any-content");
    }

    // ═══════════════════════════════════════════════════════════════
    // Veeam Careers — Job listings (list page)
    // ═══════════════════════════════════════════════════════════════

    private const string VeeamUrl = "https://careers.veeam.com/";

    [Test]
    public async Task Veeam_NewJobPostings()
    {
        await RunPipelineAndLog(
            $"Show me new job postings at {VeeamUrl}",
            "veeam-new-jobs");
    }

    [Test]
    public async Task Veeam_EngineeringPositions()
    {
        await RunPipelineAndLog(
            $"Watch {VeeamUrl} for engineering positions",
            "veeam-engineering");
    }

    [Test]
    public async Task Veeam_RemoteJobs()
    {
        await RunPipelineAndLog(
            $"Track this page for new remote job listings {VeeamUrl}",
            "veeam-remote");
    }

    [Test]
    public async Task Veeam_PragueOpenings()
    {
        await RunPipelineAndLog(
            $"Monitor {VeeamUrl} for any new openings in Prague",
            "veeam-prague");
    }

    // ═══════════════════════════════════════════════════════════════
    // Shared pipeline runner and logger
    // ═══════════════════════════════════════════════════════════════

    private async Task RunPipelineAndLog(string userInput, string runId)
    {
        Log("");
        Log("╔══════════════════════════════════════════════════════════════╗");
        Log($"║  PIPELINE RUN: {runId,-46}║");
        Log("╚══════════════════════════════════════════════════════════════╝");
        Log($"  Input: \"{userInput}\"");
        Log("");

        using var scope = _factory.Services.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IWatchSetupPipeline>();

        var options = new PipelineOptions
        {
            MaxIterations = 3,
            MinConfidence = 0.6f
        };

        var progressEvents = new List<PipelineProgress>();
        PipelineResult? finalResult = null;

        try
        {
            await foreach (var progress in pipeline.ProcessStreamingAsync(userInput, options))
            {
                progressEvents.Add(progress);

                var marker = progress.Type switch
                {
                    ProgressType.Starting => "▶ START",
                    ProgressType.InProgress => "  ⋯",
                    ProgressType.Thinking => "  💭",
                    ProgressType.StageCompleted => "  ✓ DONE",
                    ProgressType.Completed => "★ COMPLETE",
                    ProgressType.Failed => "✗ FAIL",
                    ProgressType.NeedsInput => "? INPUT",
                    ProgressType.Recovery => "↻ RECOVER",
                    _ => "  ⋯"
                };

                Log($"  [{marker}] [{progress.Stage}] {progress.Summary}");

                if (!string.IsNullOrEmpty(progress.Details))
                {
                    var details = progress.Details.Length > 500
                        ? progress.Details[..500] + "..."
                        : progress.Details;
                    Log($"           Details: {details}");
                }

                if (progress.Result != null)
                    finalResult = progress.Result;
            }
        }
        catch (Exception ex)
        {
            Log($"  [✗ EXCEPTION] {ex.GetType().Name}: {ex.Message}");
            Log($"  Stack: {ex.StackTrace?[..Math.Min(ex.StackTrace?.Length ?? 0, 500)]}");
        }

        Log("");
        Log($"  Total events: {progressEvents.Count}");

        // ═══════ Session Analysis ═══════
        var session = progressEvents.LastOrDefault(p => p.Session != null)?.Session;
        LogSessionAnalysis(session, runId);

        // ═══════ Final Result ═══════
        LogFinalResult(finalResult, runId);

        // ═══════ Minimal assertions (observational) ═══════
        progressEvents.Count.ShouldBeGreaterThan(0, $"[{runId}] Pipeline should produce at least one event");
    }

    private void LogSessionAnalysis(PipelineSession? session, string runId)
    {
        Log("");
        Log($"  === SESSION ANALYSIS ({runId}) ===");

        if (session == null)
        {
            Log("    WARNING: No session data");
            return;
        }

        // URL Extraction
        Log($"    URL Extraction:");
        Log($"      Extracted URLs: {session.ExtractedUrls.Count}");
        foreach (var url in session.ExtractedUrls)
            Log($"        - {url.Url} (normalized: {url.NormalizedUrl}, valid: {url.IsValid})");
        if (session.SelectedUrl != null)
            Log($"      Selected: {session.SelectedUrl.NormalizedUrl}");

        // Content Fetching
        Log($"    Content Fetching:");
        if (session.FetchedContent != null)
        {
            var fc = session.FetchedContent;
            Log($"      Success: {fc.IsSuccess}");
            Log($"      Title: {fc.Title ?? "(none)"}");
            Log($"      HTML length: {fc.Html?.Length ?? 0}");
            Log($"      Text length: {fc.TextContent?.Length ?? 0}");
            Log($"      Duration: {fc.FetchDurationMs}ms");
            Log($"      JavaScript: {fc.UsedJavaScript}");
            if (fc.ErrorMessage != null)
                Log($"      Error: {fc.ErrorMessage}");
        }
        else
        {
            Log("      No content fetched");
        }

        // Content Analysis
        Log($"    Content Analysis:");
        if (session.ContentAnalysis != null)
        {
            var ca = session.ContentAnalysis;
            Log($"      ContentType: {ca.ContentType}");
            Log($"      UserIntent: {ca.UserIntent}");
            Log($"      PageDescription: {ca.PageDescription}");
            Log($"      RecommendedApproach: {ca.RecommendedApproach}");
            Log($"      Confidence: {ca.Confidence:F2}");
            Log($"      Sections: {ca.IdentifiedSections.Count}");
            foreach (var sec in ca.IdentifiedSections)
            {
                Log($"        - {sec.Name} (target={sec.IsLikelyTarget})");
                Log($"          Selector: {sec.SuggestedSelector ?? "(none)"}");
                Log($"          Description: {sec.Description ?? "(none)"}");
            }
        }
        else
        {
            Log("      No analysis");
        }

        // Schema Discovery
        Log($"    Schema Discovery:");
        if (session.DiscoveredSchema != null)
        {
            var schema = session.DiscoveredSchema;
            Log($"      Fields: {schema.Fields.Count}, Confidence: {schema.Confidence:F2}");
            Log($"      ContentType: {schema.ContentType ?? "(none)"}");
            Log($"      ItemSelector: {schema.ItemSelector}");
            Log($"      SampleItemCount: {schema.SampleItemCount}");
            Log($"      IdentityFields: [{string.Join(", ", schema.InferredIdentityFields)}]");
            foreach (var field in schema.Fields)
            {
                Log($"        - {field.Name} (Type={field.Type})");
                Log($"          Selector: {field.Selector}");
                Log($"          Required={field.IsRequired} Identity={field.IsIdentityField}");
                Log($"          TrackHistory={field.TrackHistory} CurrencyCode={field.CurrencyCode ?? "null"}");
                if (field.SampleValues.Count > 0)
                    Log($"          Samples: [{string.Join(", ", field.SampleValues.Take(3))}]");
            }
        }
        else
        {
            Log("      No schema discovered");
        }

        // Selector Generation & Validation
        Log($"    Selectors:");
        Log($"      Generated: {session.GeneratedSelectors.Count}");
        foreach (var sel in session.GeneratedSelectors)
            Log($"        - [{sel.Type}] {sel.Selector} (conf={sel.Confidence:F2}, pri={sel.Priority}) {sel.Description}");
        Log($"      Validations: {session.ValidationResults.Count}");
        foreach (var val in session.ValidationResults)
            Log($"        - {val.Selector.Selector}: valid={val.IsValid}, matches={val.MatchCount}, quality={val.MatchQuality:F2} | {val.ValidationMessage}");
        if (session.BestSelector != null)
            Log($"      Best: {session.BestSelector.Selector} ({session.BestSelector.Type})");

        // Pipeline stats
        Log($"    Pipeline Stats:");
        Log($"      Iterations: {session.CurrentIteration}");
        Log($"      LLM Calls: {session.LlmCallCount}");
        Log($"      Recovery Attempts: {session.RecoveryAttempts}");
        Log($"    History:");
        foreach (var log in session.IterationHistory)
            Log($"      {log}");
    }

    private void LogFinalResult(PipelineResult? result, string runId)
    {
        Log("");
        Log($"  === FINAL RESULT ({runId}) ===");

        if (result == null)
        {
            Log("    WARNING: No final result");
            return;
        }

        Log($"    Success: {result.IsSuccess}");
        Log($"    Stage: {result.CurrentStage}");
        Log($"    NeedsInput: {result.NeedsUserInput}");
        Log($"    Error: {result.ErrorMessage ?? "(none)"}");
        Log($"    Summary: {result.Summary ?? "(none)"}");

        if (result.FinalConfiguration != null)
        {
            var config = result.FinalConfiguration;
            Log($"    Watch Config:");
            Log($"      URL: {config.Url}");
            Log($"      Name: {config.Name}");
            Log($"      CSS: {config.CssSelector ?? "(none)"}");
            Log($"      XPath: {config.XPathSelector ?? "(none)"}");
            Log($"      JavaScript: {config.UseJavaScript}");
            Log($"      CheckInterval: {config.CheckInterval}");
            Log($"      SchemaEnabled: {config.SchemaEnabled}");
            Log($"      Confidence: {config.Confidence:F2}");
            Log($"      Description: {config.Description}");

            if (config.Schema != null)
            {
                Log($"      Schema ({config.Schema.Fields.Count} fields):");
                foreach (var field in config.Schema.Fields)
                {
                    Log($"        - {field.Name} ({field.Type}) TrackHistory={field.TrackHistory} Currency={field.CurrencyCode ?? "none"}");
                    if (field.AlertThresholds.Count > 0)
                        Log($"          Alerts: {string.Join(", ", field.AlertThresholds.Select(a => $"{a.ConditionType} {a.Value}"))}");
                }
            }
        }

        if (result.UserPrompts.Count > 0)
        {
            Log($"    User Prompts:");
            foreach (var p in result.UserPrompts)
                Log($"      - {p}");
        }

        if (result.SuggestedOptions.Count > 0)
        {
            Log($"    Suggested Options:");
            foreach (var o in result.SuggestedOptions)
                Log($"      - {o.Label} ({o.Value}) conf={o.Confidence:F2} rec={o.IsRecommended}");
        }

        Log("");
        Log($"  === END {runId} ===");
        Log("");
    }

    // ═══════════════════════════════════════════════════════════════
    // Adversarial Tests — Edge cases and stress tests
    // ═══════════════════════════════════════════════════════════════

    [Test]
    public async Task Adversarial_UrlOnly_NoIntent()
    {
        // Just a URL with no explanation — pipeline must infer intent
        await RunPipelineAndLog(AmazonUrl, "adv-url-only");
    }

    [Test]
    public async Task Adversarial_VagueIntent_NoUrl()
    {
        // Intent without any URL — should ask for URL or fail gracefully
        await RunPipelineAndLog(
            "I want to track prices on some website",
            "adv-vague-no-url");
    }

    [Test]
    public async Task Adversarial_MultipleUrls()
    {
        // Two URLs in one prompt — should pick the most relevant one
        await RunPipelineAndLog(
            $"Compare prices between {AmazonUrl} and https://www.amazon.de/dp/B0CX4YQ3TZ",
            "adv-multi-url");
    }

    [Test]
    public async Task Adversarial_NonEnglishPrompt()
    {
        // Czech language prompt with Czech Wikipedia URL
        await RunPipelineAndLog(
            $"Sleduj změny na hlavní stránce {CzechWikiUrl}",
            "adv-czech-prompt");
    }

    [Test]
    public async Task Adversarial_VeryLongPrompt()
    {
        // Excessively detailed prompt
        await RunPipelineAndLog(
            $"I'm a World of Warcraft player and I want to stay up to date with all the latest patch notes, " +
            $"hotfixes, class changes, talent tree modifications, dungeon and raid tuning adjustments, " +
            $"PvP balance updates, and any other game-changing modifications that Blizzard publishes on " +
            $"their news page. I specifically care about warrior and paladin changes but want to see everything. " +
            $"Please monitor {WowheadUrl} for me and let me know when something new appears.",
            "adv-long-prompt");
    }

    [Test]
    public async Task Adversarial_TypoInUrl()
    {
        // URL with typo — should handle gracefully
        await RunPipelineAndLog(
            "Watch for new jobs at https://careers.veam.com/",
            "adv-typo-url");
    }

    // ═══════════════════════════════════════════════════════════════
    // Test Factory
    // ═══════════════════════════════════════════════════════════════

    private class BaselineWebApplicationFactory : CachingWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Development");
        }
    }
}
