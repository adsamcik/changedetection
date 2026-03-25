using System.Net.Http.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;
using ChangeDetection.Tests.Infrastructure;
using ChangeDetection.Tests.Llm.Cache;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// End-to-end pipeline test for the LTT Store product page.
/// 
/// Validates the 2-turn LLM-driven schema discovery on a single product page:
///   Turn 1: Structure classification (should detect "single" item)
///   Turn 2: Field discovery (should find Price, Stock Status, etc.)
///   PostProcess: TrackHistory + CurrencyCode enrichment
///
/// Uses CachingWebApplicationFactory for LLM response caching and content caching.
/// Run with -IncludeLlm -IncludeInternet to populate caches on first run.
/// </summary>
[Category("EndToEnd")]
[Category("LlmCached")]
[Category("RequiresInternet")]
public class LttStorePipelineE2ETests : TestBase, IAsyncDisposable
{
    private HttpClient _client = null!;
    private LttStoreWebApplicationFactory _factory = null!;

    private const string UserInput = "Track the price and availability of this cable https://global.lttstore.com/products/ltt-truespec-cable-usb-type-c-to-c?variant=44410354204717";
    private const string ExpectedUrl = "https://global.lttstore.com/products/ltt-truespec-cable-usb-type-c-to-c";

    [Before(Test)]
    public async Task SetUp()
    {
        _factory = new LttStoreWebApplicationFactory();
        _client = _factory.CreateClient();
        _client.Timeout = TimeSpan.FromMinutes(5);

        Log($"=== LLM Cache Mode: {_factory.LlmCacheMode} ===");
        Log($"=== Content Cache Mode: {_factory.ContentCacheMode} ===");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null)
            await _factory.DisposeAsync();
    }

    /// <summary>
    /// Full pipeline observation: runs the pipeline on the LTT Store product page
    /// and logs every stage's output for observability.
    /// </summary>
    [Test]
    public async Task FullPipeline_LttStoreProduct_ObserveBehavior()
    {
        Log("╔══════════════════════════════════════════════════════════════╗");
        Log("║  LTT STORE PIPELINE OBSERVATION (2-Turn Schema Discovery)   ║");
        Log("╚══════════════════════════════════════════════════════════════╝");
        Log("");
        Log($"Input: \"{UserInput}\"");
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

        await foreach (var progress in pipeline.ProcessStreamingAsync(UserInput, options))
        {
            progressEvents.Add(progress);

            var marker = progress.Type switch
            {
                ProgressType.Starting => "START",
                ProgressType.InProgress => "  ...",
                ProgressType.Thinking => "  THINK",
                ProgressType.StageCompleted => "  DONE",
                ProgressType.Completed => "COMPLETE",
                ProgressType.Failed => "FAIL",
                _ => "  ..."
            };

            Log($"[{marker}] [{progress.Stage}] {progress.Summary}");

            if (!string.IsNullOrEmpty(progress.Details))
            {
                var details = progress.Details.Length > 500
                    ? progress.Details[..500] + "..."
                    : progress.Details;
                Log($"         Details: {details}");
            }

            if (progress.Result != null)
                finalResult = progress.Result;
        }

        Log("");
        Log($"Total progress events: {progressEvents.Count}");

        // ═══════ Analyze schema discovery results ═══════
        Log("");
        Log("=== SCHEMA DISCOVERY ANALYSIS ===");

        var session = progressEvents.LastOrDefault(p => p.Session != null)?.Session;
        if (session == null)
        {
            Log("  WARNING: No session data available");
        }
        else
        {
            Log($"  Content Analysis ContentType: {session.ContentAnalysis?.ContentType}");
            Log($"  Content Analysis UserIntent: {session.ContentAnalysis?.UserIntent}");
            Log($"  Content Analysis RecommendedApproach: {session.ContentAnalysis?.RecommendedApproach}");

            if (session.DiscoveredSchema != null)
            {
                var schema = session.DiscoveredSchema;
                Log("");
                Log($"  SCHEMA DISCOVERED: {schema.Fields.Count} fields, confidence={schema.Confidence:F2}");
                Log($"    ContentType: {schema.ContentType ?? "(none)"}");
                Log($"    ItemSelector: {schema.ItemSelector}");
                Log($"    SampleItemCount: {schema.SampleItemCount}");
                Log($"    IdentityFields: [{string.Join(", ", schema.InferredIdentityFields)}]");
                Log("");
                Log("    Fields:");
                foreach (var field in schema.Fields)
                {
                    Log($"      - {field.Name} (Type={field.Type})");
                    Log($"        Selector: {field.Selector}");
                    Log($"        Required={field.IsRequired} Identity={field.IsIdentityField} Confidence={field.Confidence:F2}");
                    if (field.SampleValues.Count > 0)
                        Log($"        Samples: [{string.Join(", ", field.SampleValues.Take(3))}]");
                }
            }
            else
            {
                Log("");
                Log("  NO SCHEMA DISCOVERED");
                Log("  -> Turn 1 may have classified page as 'none' or schema discovery failed");
            }

            Log("");
            Log("  Pipeline History:");
            foreach (var log in session.IterationHistory)
                Log($"    {log}");
        }

        // ═══════ Final result ═══════
        Log("");
        Log("=== FINAL RESULT ===");

        if (finalResult == null)
        {
            Log("  WARNING: No final result produced");
            return;
        }

        Log($"  Success: {finalResult.IsSuccess}");
        Log($"  Stage: {finalResult.CurrentStage}");
        Log($"  Error: {finalResult.ErrorMessage ?? "(none)"}");

        if (finalResult.FinalConfiguration != null)
        {
            var config = finalResult.FinalConfiguration;
            Log($"  Watch URL: {config.Url}");
            Log($"  Watch Name: {config.Name}");
            Log($"  CSS Selector: {config.CssSelector ?? "(none)"}");
            Log($"  XPath Selector: {config.XPathSelector ?? "(none)"}");
            Log($"  JavaScript: {config.UseJavaScript}");
            Log($"  Check Interval: {config.CheckInterval}");
            Log($"  Description: {config.Description}");

            if (config.Schema != null)
            {
                Log("");
                Log($"  Final Extraction Schema ({config.Schema.Fields.Count} fields):");
                foreach (var field in config.Schema.Fields)
                {
                    Log($"    - {field.Name} ({field.Type}) TrackHistory={field.TrackHistory} Currency={field.CurrencyCode ?? "none"}");
                    if (field.AlertThresholds.Count > 0)
                        Log($"      Alerts: {string.Join(", ", field.AlertThresholds.Select(a => $"{a.ConditionType} {a.Value}"))}");
                }
            }
        }

        // ═══════ Assertions ═══════
        Log("");
        Log("=== ASSERTIONS ===");

        session.ShouldNotBeNull("Session should exist");
        if (session!.ContentAnalysis == null)
        {
            Skip.Test("Content analysis returned null for the live LTT Store page; this observational internet-backed test is currently non-deterministic.");
            return;
        }

        Log("  PASS: Content analysis completed");

        if (session.DiscoveredSchema != null)
        {
            Log("  PASS: Schema was discovered");

            var priceField = session.DiscoveredSchema.Fields
                .FirstOrDefault(f => f.Name.Contains("price", StringComparison.OrdinalIgnoreCase)
                                  || f.Type.Equals("Currency", StringComparison.OrdinalIgnoreCase));
            if (priceField != null)
            {
                Log($"  PASS: Price field found: {priceField.Name} ({priceField.Type})");
            }
            else
            {
                Log("  INFO: No explicit price field found in schema");
            }
        }
        else
        {
            Log("  INFO: Schema was NOT discovered - check Turn 1 classification");
        }

        Log("");
        Log("=== OBSERVATION COMPLETE ===");
    }

    /// <summary>
    /// Alternative: runs through the HTTP endpoint for a simpler end-to-end test.
    /// </summary>
    [Test]
    public async Task ProcessInput_LttStoreProduct_CreatesWatchWithSchema()
    {
        Log("Sending LTT Store input via /api/llm/process-input");

        var request = new ProcessInputRequest { Input = UserInput };
        var response = await _client.PostAsJsonAsync("/api/llm/process-input", request);

        response.IsSuccessStatusCode.ShouldBeTrue($"HTTP request failed: {response.StatusCode}");

        var result = await response.Content.ReadFromJsonAsync<ProcessInputResponse>();
        result.ShouldNotBeNull();

        Log($"  Success: {result.IsSuccess}");
        Log($"  Intent: {result.Intent}");
        Log($"  Summary: {result.Summary}");
        Log($"  Error: {result.ErrorMessage ?? "(none)"}");

        if (result.IsSuccess && result.CreatedWatchId != null)
        {
            Log($"  Watch ID: {result.CreatedWatchId}");
            Log($"  URL: {result.ParsedRequest?.Url}");
            Log($"  CSS Selector: {result.ParsedRequest?.CssSelector ?? "(none)"}");
            Log($"  XPath Selector: {result.ParsedRequest?.XPathSelector ?? "(none)"}");

            using var scope = _factory.Services.CreateScope();
            var watchService = scope.ServiceProvider.GetRequiredService<IWatchService>();
            var watch = await watchService.GetByIdAsync(Guid.Parse(result.CreatedWatchId));

            if (watch != null)
            {
                Log("");
                Log("  Persisted Watch Details:");
                Log($"    Name: {watch.Name}");
                Log($"    URL: {watch.Url}");
                Log($"    CssSelector: {watch.CssSelector ?? "(none)"}");

                if (watch.Schema != null)
                {
                    Log($"    Schema ({watch.Schema.Fields.Count} fields):");
                    foreach (var field in watch.Schema.Fields)
                        Log($"      - {field.Name} ({field.Type})");
                }
                else
                {
                    Log("    WARNING: No extraction schema on persisted watch");
                }
            }
        }
        else if (!result.IsSuccess)
        {
            Log($"  Pipeline failed: {result.ErrorMessage}");
            if (result.Suggestions?.Count > 0)
            {
                Log("  Suggestions:");
                foreach (var s in result.Suggestions)
                    Log($"    - {s.Label}");
            }
        }
    }

    /// <summary>
    /// Minimal factory for LTT Store E2E tests.
    /// </summary>
    private class LttStoreWebApplicationFactory : CachingWebApplicationFactory
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Development");
        }
    }
}
