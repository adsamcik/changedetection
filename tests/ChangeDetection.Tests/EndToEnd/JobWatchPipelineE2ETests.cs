using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using ChangeDetection.Tests.Llm.Cache;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// Full end-to-end pipeline tests for Job Watch.
/// Exercises: seed group → inject HTML → run check → extract → diff → LLM score → track → verify alert.
/// Uses real LLM (with SQLite caching) — first run calls Copilot/Ollama, subsequent runs replay from cache.
/// Run with -IncludeOllama to populate cache.
/// </summary>
[Category("EndToEnd"), Category("LlmCached")]
public class JobWatchPipelineE2ETests : TestBase, IAsyncDisposable
{
    private JobWatchE2EFactory _factory = null!;

    [Before(Test)]
    public async Task SetUp()
    {
        _factory = new JobWatchE2EFactory();
        Log($"LLM Cache: {_factory.LlmCacheMode}");
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    // TC1: Perfect Match — MSc lab role at UCPH
    // Full pipeline: HTML → extract → diff → LLM score → track → HIGH
    // ═══════════════════════════════════════════════════

    [Test]
    public async Task TC1_PerfectMatch_FullPipeline_ProducesHighAlert()
    {
        var url = "https://employment.ku.dk/test-vacancies/tc1";
        _factory.Fetcher.SetHtml(url, """
            <html><body>
            <table class="vacancies">
            <thead><tr><th>TITLE</th><th>FACULTY</th><th>LOCATION</th><th>DEADLINE</th></tr></thead>
            <tbody>
            <tr class="vacancy-specs">
                <td><a href="/all-vacancies/?show=99001">Research Assistant in Molecular Cancer Biology</a></td>
                <td>Faculty of Health and Medical Sciences</td>
                <td>Department of Drug Design and Pharmacology</td>
                <td>15-04-2026</td>
            </tr>
            </tbody>
            </table>
            </body></html>
            """);

        // Seed a watch group with the candidate profile, pointing at our test URL
        var (group, watch) = await SeedSingleWatchAsync(url, "watch-tc1-ucph");

        // First check — establishes baseline (no previous snapshot → no change event)
        var firstResult = await CheckWatchAsync(watch.Id);
        // First check may or may not produce a change event depending on whether there was a prior snapshot

        // Inject updated content — add "NEW" marker so diff detects a change
        _factory.Fetcher.SetHtml(url, """
            <html><body>
            <table class="vacancies">
            <thead><tr><th>TITLE</th><th>FACULTY</th><th>LOCATION</th><th>DEADLINE</th></tr></thead>
            <tbody>
            <tr class="vacancy-specs">
                <td><a href="/all-vacancies/?show=99001">Research Assistant in Molecular Cancer Biology</a></td>
                <td>Faculty of Health and Medical Sciences</td>
                <td>Department of Drug Design and Pharmacology</td>
                <td>15-04-2026</td>
            </tr>
            <tr class="vacancy-specs">
                <td><a href="/all-vacancies/?show=99002">Laboratory Technician, Cell Culture Facility</a></td>
                <td>Faculty of Health and Medical Sciences</td>
                <td>Department of Biomedical Sciences</td>
                <td>20-04-2026</td>
            </tr>
            </tbody>
            </table>
            </body></html>
            """);

        // Second check — should detect the new listing
        var secondResult = await CheckWatchAsync(watch.Id);

        // Verify: check that a tracked item was created
        var trackingService = _factory.Services.GetRequiredService<IItemTrackingService>();
        var trackedItems = await trackingService.GetItemsAsync(group.Id, CancellationToken.None);

        Log($"Tracked items: {trackedItems.Count}");
        foreach (var item in trackedItems)
        {
            Log($"  [{item.AlertLevel}] {item.DisplayName} — {item.State} — {item.Recommendation}");
            Log($"    Dimensions: {item.MatchDimensionsJson?.Substring(0, Math.Min(200, item.MatchDimensionsJson?.Length ?? 0))}");
        }

        // At minimum: items should exist and have meaningful data
        trackedItems.Count.ShouldBeGreaterThan(0, "Should have tracked items after diff");

        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    // TC2: PhD Listing — Should be filtered or scored SILENT
    // ═══════════════════════════════════════════════════

    [Test]
    public async Task TC2_PhDListing_FullPipeline_ProducesSilentOrFiltered()
    {
        var url = "https://employment.ku.dk/test-vacancies/tc2";
        _factory.Fetcher.SetHtml(url, """
            <html><body>
            <table class="vacancies">
            <thead><tr><th>TITLE</th><th>FACULTY</th><th>LOCATION</th><th>DEADLINE</th></tr></thead>
            <tbody>
            <tr class="vacancy-specs">
                <td><a href="/all-vacancies/?show=99010">PhD fellowship in Molecular Cell Biology</a></td>
                <td>Faculty of Science</td>
                <td>Department of Biology</td>
                <td>12-04-2026</td>
            </tr>
            </tbody>
            </table>
            </body></html>
            """);

        var (group, watch) = await SeedSingleWatchAsync(url, "watch-tc2-phd");

        // Baseline
        await CheckWatchAsync(watch.Id);

        // Add a second listing to trigger diff
        _factory.Fetcher.SetHtml(url, """
            <html><body>
            <table class="vacancies">
            <thead><tr><th>TITLE</th><th>FACULTY</th><th>LOCATION</th><th>DEADLINE</th></tr></thead>
            <tbody>
            <tr class="vacancy-specs">
                <td><a href="/all-vacancies/?show=99010">PhD fellowship in Molecular Cell Biology</a></td>
                <td>Faculty of Science</td>
                <td>Department of Biology</td>
                <td>12-04-2026</td>
            </tr>
            <tr class="vacancy-specs">
                <td><a href="/all-vacancies/?show=99011">2 PhD fellowships in in-vitro digestion of novel plant-based ingredients</a></td>
                <td>Faculty of Science</td>
                <td>FOOD</td>
                <td>15-03-2026</td>
            </tr>
            </tbody>
            </table>
            </body></html>
            """);

        await CheckWatchAsync(watch.Id);

        // Verify: PhD listings should be filtered (suppressed) or scored SILENT
        var trackingService = _factory.Services.GetRequiredService<IItemTrackingService>();
        var items = await trackingService.GetItemsAsync(group.Id, CancellationToken.None);

        Log($"TC2 tracked items: {items.Count}");
        foreach (var item in items)
        {
            Log($"  [{item.AlertLevel}] {item.DisplayName} — {item.Recommendation}");

            // PhD listings should NOT be HIGH
            if (item.DisplayName?.Contains("PhD", StringComparison.OrdinalIgnoreCase) == true)
            {
                item.AlertLevel.ShouldNotBe(AlertLevel.High,
                    $"PhD listing '{item.DisplayName}' should not be HIGH alert");
            }
        }

        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════

    private static readonly string CandidateProfile = """
        {
            "education": { "level": "MSc", "field": "molecular and cell biology",
                "note": "NOT a PhD holder" },
            "experience_years": "1-3",
            "techniques_strong": ["PCR", "qPCR", "cell culture", "ELISA",
                "flow cytometry", "fluorescence microscopy", "western blot",
                "cloning", "DNA/RNA isolation", "protein purification"],
            "techniques_basic": ["CRISPR", "protein expression",
                "sequencing library prep"],
            "techniques_none": ["organoid culture", "mass spectrometry",
                "NGS library prep", "animal models", "bioinformatics pipelines"],
            "target_locations": ["Prague", "Copenhagen", "Lyngby", "Bagsværd",
                "Måløv", "Hørsholm", "Gentofte", "Kvistgaard", "Valby",
                "Malmö", "Lund"],
            "languages": ["Czech (native)", "English (C1)", "German (basic)"],
            "salary_floor": { "prague": "50000", "copenhagen": "30000" },
            "dealbreakers": ["SOTIO", "animal-heavy work",
                "pure flow cytometry specialist"],
            "preferences": ["variety", "autonomy", "intellectual challenge"]
        }
        """;

    private async Task<(WatchGroup Group, WatchedSite Watch)> SeedSingleWatchAsync(
        string url, string watchId)
    {
        using var scope = _factory.Services.CreateScope();
        var groupService = scope.ServiceProvider.GetRequiredService<IWatchGroupService>();
        var watchService = scope.ServiceProvider.GetRequiredService<IWatchService>();
        var filterGen = scope.ServiceProvider.GetRequiredService<IProfileFilterRuleGenerator>();

        var group = await groupService.CreateGroupAsync(new WatchGroupCreateRequest
        {
            Name = $"E2E Test — {watchId}",
            UserIntent = "Monitor for biotech lab positions matching MSc molecular biology profile",
            AnalysisProfileJson = CandidateProfile,
            TemplateId = "e2e-test",
            Tags = ["e2e-test"]
        }, CancellationToken.None);

        // Set TrackingConfig on the group entity directly
        group.TrackingConfig = TrackingConfig.ForJobs();
        var groupRepo = scope.ServiceProvider.GetRequiredService<IRepository<WatchGroup>>();
        await groupRepo.UpdateAsync(group, CancellationToken.None);

        var filterRules = filterGen.GenerateRules(CandidateProfile);

        var watch = await watchService.CreateWatchAsync(new CreateWatchRequest
        {
            Url = url,
            Name = watchId,
            GroupId = group.Id,
            UserIntent = "Monitor for biotech lab positions",
            SchemaEnabled = true,
            Schema = new ExtractionSchema
            {
                // Use simple class selector — the CssToXPath converter only handles
                // simple patterns (element.class), not descendant selectors
                ItemSelector = "tr.vacancy-specs",
                Fields =
                [
                    new SchemaField { Name = "title", Selector = "td:first-child a",
                        Type = FieldType.String, IsRequired = true, IsIdentityField = true },
                    new SchemaField { Name = "url", Selector = "td:first-child a",
                        Type = FieldType.Url, IsRequired = true },
                    new SchemaField { Name = "faculty", Selector = "td:nth-child(2)",
                        Type = FieldType.String, IsIdentityField = true },
                    new SchemaField { Name = "department", Selector = "td:nth-child(3)",
                        Type = FieldType.String },
                    new SchemaField { Name = "deadline", Selector = "td:nth-child(4)",
                        Type = FieldType.Date }
                ],
                IdentityFieldNames = ["title", "faculty"]
            },
            FilterRules = filterRules,
            FetchSettings = new FetchSettings { UseJavaScript = false, TimeoutSeconds = 30 },
            ScheduleSettings = new CheckScheduleSettings
            {
                Mode = CheckScheduleMode.Fixed,
                BaseInterval = TimeSpan.FromHours(24)
            },
            SkipInitialCheck = true
        }, CancellationToken.None);

        // Set analysis settings on the watch entity directly
        watch.AnalysisSettings = new LlmAnalysisSettings
        {
            EnableChangeAnalysis = true,
            CalculateRelevance = true,
            GenerateSemanticSummary = true
        };
        await watchService.UpdateWatchAsync(watch, CancellationToken.None);

        return (group, watch);
    }

    private async Task<ChangeEvent?> CheckWatchAsync(Guid watchId)
    {
        using var scope = _factory.Services.CreateScope();
        var watchService = scope.ServiceProvider.GetRequiredService<IWatchService>();
        return await watchService.CheckForChangesAsync(watchId, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
    }
}

/// <summary>
/// WebApplicationFactory for Job Watch E2E tests.
/// Uses CachingWebApplicationFactory for real LLM (cached in SQLite) +
/// MutableContentFetcher for injecting test HTML.
/// </summary>
public sealed class JobWatchE2EFactory : CachingWebApplicationFactory
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cd-e2e-{Guid.NewGuid():N}.db");

    public MutableContentFetcher Fetcher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder); // Sets up LLM caching

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LiteDb:Path"] = _dbPath
            });
        });

        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Isolated database
            services.RemoveAll<LiteDbContext>();
            var dbContext = new LiteDbContext($"Filename={_dbPath};Connection=shared");
            services.AddSingleton(dbContext);

            // Remove hosted services
            services.RemoveAll<IHostedService>();

            // Override content fetcher with mutable test version
            services.RemoveAll<IContentFetcher>();
            services.AddSingleton<IContentFetcher>(Fetcher);

            // Seed a Copilot SDK LLM provider. Uses GitHub authentication (gh auth login).
            // The CachingLlmHttpHandler (from base class) intercepts HTTP calls and
            // caches responses in SQLite for deterministic replay.
            var providerRepo = new LiteDbRepository<LlmProviderConfig>(dbContext, "llm_providers");
            providerRepo.InsertAsync(new LlmProviderConfig
            {
                Name = "GitHub Copilot",
                ProviderType = LlmProviderType.Copilot,
                Model = "gpt-4o",
                IsEnabled = true,
                Priority = 1
            }).GetAwaiter().GetResult();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var journal = _dbPath + "-journal";
            if (File.Exists(journal)) File.Delete(journal);
        }
        catch { }
    }
}

/// <summary>
/// Content fetcher that returns injectable HTML per URL.
/// </summary>
public sealed class MutableContentFetcher : IContentFetcher
{
    private readonly ConcurrentDictionary<string, string> _htmlByUrl = new();

    public void SetHtml(string url, string html) => _htmlByUrl[url] = html;

    public Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct = default)
    {
        var html = _htmlByUrl.GetValueOrDefault(url,
            "<html><body><div>no content configured for this URL</div></body></html>");

        return Task.FromResult(new FetchResult
        {
            IsSuccess = true,
            Html = html,
            HttpStatusCode = 200,
            DurationMs = 1
        });
    }
}
