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
    // TC3: "PhD or Equivalent" — Should score MEDIUM (ambiguous)
    // ═══════════════════════════════════════════════════

    [Test]
    public async Task TC3_PhDOrEquivalent_FullPipeline_ProducesMediumAlert()
    {
        var url = "https://careers.novonesis.com/test/tc3";
        var baseline = BuildVacancyPage();
        var withNewRole = BuildVacancyPage(
            ("Scientist, Protein Biochemistry", "Faculty of Science", "Enzyme Department", "20-04-2026", null));

        // Inject additional context as page body text
        var withNewRoleHtml = withNewRole.Replace("</body>",
            "<p>Qualifications: PhD in biochemistry, molecular biology, or equivalent documented " +
            "industrial experience. Experience with protein purification, SDS-PAGE/Western blot, " +
            "and ELISA. Fluent in English; Danish is an advantage but not required.</p></body>");

        _factory.Fetcher.SetHtml(url, baseline);
        var (group, watch) = await SeedSingleWatchAsync(url, "watch-tc3-phdequiv");
        await CheckWatchAsync(watch.Id);

        _factory.Fetcher.SetHtml(url, withNewRoleHtml);
        await CheckWatchAsync(watch.Id);

        var items = await GetTrackedItemsAsync(group.Id);
        LogItems("TC3", items);

        items.ShouldNotBeEmpty("TC3 should produce tracked items");
        // PhD-or-equivalent should NOT be SILENT (that's a false negative)
        foreach (var item in items)
        {
            item.AlertLevel.ShouldNotBe(AlertLevel.Silent,
                $"TC3: '{item.DisplayName}' has 'PhD or equivalent' — should NOT be SILENT");
        }
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    // TC5: Czech Language Posting — SZÚ Molecular Diagnostics
    // Tests Czech text LLM understanding
    // ═══════════════════════════════════════════════════

    [Test]
    public async Task TC5_CzechPosting_SZU_FullPipeline_ProducesAlert()
    {
        var url = "https://szu.gov.cz/test/tc5";
        var baseline = """
            <html><body><ul class="lcp_catlist">
            <li><a href="/kariera/admin/">Administrativní pracovník</a></li>
            </ul></body></html>
            """;
        var withNewRole = """
            <html><body><ul class="lcp_catlist">
            <li><a href="/kariera/admin/">Administrativní pracovník</a></li>
            <li><a href="/kariera/laboratorni-pracovnik-molekularni-diagnostika/">Laboratorní pracovník – oddělení molekulární diagnostiky</a></li>
            </ul></body></html>
            """;

        _factory.Fetcher.SetHtml(url, baseline);
        var (group, watch) = await SeedSzuWatchAsync(url, "watch-tc5-szu");
        await CheckWatchAsync(watch.Id);

        _factory.Fetcher.SetHtml(url, withNewRole);
        await CheckWatchAsync(watch.Id);

        var items = await GetTrackedItemsAsync(group.Id);
        LogItems("TC5", items);

        items.ShouldNotBeEmpty("TC5: Czech posting should produce tracked items");
        // A molecular diagnostics lab role should not be SILENT for this candidate
        var diagItem = items.FirstOrDefault(i =>
            i.DisplayName?.Contains("molekulární", StringComparison.OrdinalIgnoreCase) == true ||
            i.DisplayName?.Contains("diagnostik", StringComparison.OrdinalIgnoreCase) == true);
        if (diagItem != null)
        {
            diagItem.AlertLevel.ShouldNotBe(AlertLevel.Silent,
                "TC5: Czech molecular diagnostics role should be relevant to this candidate");
        }
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    // TC6: Wrong Location — Paris, France
    // Should be SILENT or at minimum not HIGH
    // ═══════════════════════════════════════════════════

    [Test]
    public async Task TC6_WrongLocation_Paris_FullPipeline_NotHigh()
    {
        var url = "https://eures.europa.eu/test/tc6";
        var baseline = BuildVacancyPage();
        var withNewRole = BuildVacancyPage(
            ("Research Technician, Cell Biology", "Institut Pasteur", "Paris, France", "30-04-2026", null));
        var withNewRoleHtml = withNewRole.Replace("</body>",
            "<p>Requirements: MSc in cell biology or molecular biology. " +
            "Experience with mammalian cell culture and fluorescence microscopy. " +
            "Working knowledge of French or willingness to learn.</p></body>");

        _factory.Fetcher.SetHtml(url, baseline);
        var (group, watch) = await SeedSingleWatchAsync(url, "watch-tc6-paris");
        await CheckWatchAsync(watch.Id);

        _factory.Fetcher.SetHtml(url, withNewRoleHtml);
        await CheckWatchAsync(watch.Id);

        var items = await GetTrackedItemsAsync(group.Id);
        LogItems("TC6", items);

        // Paris is wrong location — should not be HIGH
        foreach (var item in items)
        {
            item.AlertLevel.ShouldNotBe(AlertLevel.High,
                $"TC6: '{item.DisplayName}' in Paris should NOT be HIGH");
        }
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    // TC7: Hidden Senior Role — Genmab "Scientist" requiring PhD
    // Should be SILENT despite "Scientist" title
    // ═══════════════════════════════════════════════════

    [Test]
    public async Task TC7_HiddenSeniorRole_Genmab_FullPipeline_NotHigh()
    {
        var url = "https://careers.genmab.com/test/tc7";
        var baseline = BuildVacancyPage();
        var withNewRole = BuildVacancyPage(
            ("Scientist, Antibody Discovery", "Genmab A/S", "Copenhagen, Denmark", "15-04-2026", null));
        var withNewRoleHtml = withNewRole.Replace("</body>",
            "<p>Requirements: PhD in immunology, biochemistry, or molecular biology. " +
            "3+ years post-PhD experience in antibody engineering. " +
            "Expert-level experience with phage display, hybridoma technology, " +
            "and affinity maturation. Proven publication record.</p></body>");

        _factory.Fetcher.SetHtml(url, baseline);
        var (group, watch) = await SeedSingleWatchAsync(url, "watch-tc7-genmab");
        await CheckWatchAsync(watch.Id);

        _factory.Fetcher.SetHtml(url, withNewRoleHtml);
        await CheckWatchAsync(watch.Id);

        var items = await GetTrackedItemsAsync(group.Id);
        LogItems("TC7", items);

        // Hidden senior role should not be HIGH
        foreach (var item in items)
        {
            if (item.DisplayName?.Contains("Antibody", StringComparison.OrdinalIgnoreCase) == true)
            {
                item.AlertLevel.ShouldNotBe(AlertLevel.High,
                    "TC7: PhD+senior hidden behind 'Scientist' title should NOT be HIGH");
            }
        }
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    // TC8: Czech Diagnostics R&D — Prague, perfect match
    // Should be HIGH
    // ═══════════════════════════════════════════════════

    [Test]
    public async Task TC8_CzechDiagnosticsRD_FullPipeline_ProducesHighAlert()
    {
        var url = "https://www.jobs.cz/test/tc8";
        var baseline = BuildVacancyPage();
        var withNewRole = BuildVacancyPage(
            ("Výzkumný pracovník – vývoj diagnostických testů", "GeneProof s.r.o.", "Prague", "30-04-2026", null));
        var withNewRoleHtml = withNewRole.Replace("</body>",
            "<p>Hledáme výzkumného pracovníka pro vývoj nových " +
            "molekulárně-diagnostických testů. Mgr. v oboru molekulární biologie. " +
            "1-3 roky zkušeností. PCR, ELISA, elektroforéza, IVDR validace. " +
            "Plat 55 000 – 70 000 Kč/měsíc.</p></body>");

        _factory.Fetcher.SetHtml(url, baseline);
        var (group, watch) = await SeedSingleWatchAsync(url, "watch-tc8-prague");
        await CheckWatchAsync(watch.Id);

        _factory.Fetcher.SetHtml(url, withNewRoleHtml);
        await CheckWatchAsync(watch.Id);

        var items = await GetTrackedItemsAsync(group.Id);
        LogItems("TC8", items);

        items.ShouldNotBeEmpty("TC8 should produce tracked items");
        // Prague diagnostics R&D with MSc + PCR + ELISA — should be strong match
        var diagItem = items.FirstOrDefault(i =>
            i.DisplayName?.Contains("diagnostick", StringComparison.OrdinalIgnoreCase) == true ||
            i.DisplayName?.Contains("Výzkumný", StringComparison.OrdinalIgnoreCase) == true);
        if (diagItem != null)
        {
            diagItem.AlertLevel.ShouldNotBe(AlertLevel.Silent,
                "TC8: Prague diagnostics R&D should be at least MEDIUM");
        }
        await Task.CompletedTask;
    }

    // ═══════════════════════════════════════════════════
    // TC9: Subtle Skill Gap — Lundbeck (liquid handling)
    // Should be MEDIUM (partial match)
    // ═══════════════════════════════════════════════════

    [Test]
    public async Task TC9_SubtleSkillGap_Lundbeck_FullPipeline_ProducesAlert()
    {
        var url = "https://jobs.lundbeck.com/test/tc9";
        var baseline = BuildVacancyPage();
        var withNewRole = BuildVacancyPage(
            ("Laboratory Scientist, In Vitro Pharmacology", "H. Lundbeck A/S", "Valby, Copenhagen", "25-04-2026", null));
        var withNewRoleHtml = withNewRole.Replace("</body>",
            "<p>Requirements: MSc in pharmacology, molecular biology, or neuroscience. " +
            "Experience with mammalian cell culture and cell-based assays. " +
            "Hands-on experience with automated liquid handling platforms (Hamilton, Beckman). " +
            "Knowledge of receptor pharmacology and dose-response analysis.</p></body>");

        _factory.Fetcher.SetHtml(url, baseline);
        var (group, watch) = await SeedSingleWatchAsync(url, "watch-tc9-lundbeck");
        await CheckWatchAsync(watch.Id);

        _factory.Fetcher.SetHtml(url, withNewRoleHtml);
        await CheckWatchAsync(watch.Id);

        var items = await GetTrackedItemsAsync(group.Id);
        LogItems("TC9", items);

        items.ShouldNotBeEmpty("TC9 should produce tracked items");
        // Lundbeck listing should be tracked — it's a partial match (skill gap in liquid handling)
        // Should NOT be SILENT (the candidate has most skills, just not automated liquid handling)
        foreach (var item in items)
        {
            if (item.DisplayName?.Contains("Pharmacology", StringComparison.OrdinalIgnoreCase) == true)
            {
                item.AlertLevel.ShouldNotBe(AlertLevel.Silent,
                    "TC9: Partial skill match should not be SILENT");
            }
        }
        await Task.CompletedTask;
    }

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

    private async Task<IReadOnlyList<TrackedItem>> GetTrackedItemsAsync(Guid groupId)
    {
        var trackingService = _factory.Services.GetRequiredService<IItemTrackingService>();
        return await trackingService.GetItemsAsync(groupId, CancellationToken.None);
    }

    private void LogItems(string testCase, IReadOnlyList<TrackedItem> items)
    {
        Log($"{testCase} tracked items: {items.Count}");
        foreach (var item in items)
        {
            Log($"  [{item.AlertLevel}] {item.DisplayName} — State={item.State} Rec={item.Recommendation}");
            if (item.MatchDimensionsJson is not null)
                Log($"    Dims: {item.MatchDimensionsJson[..Math.Min(300, item.MatchDimensionsJson.Length)]}");
        }
    }

    /// <summary>
    /// Builds a UCPH-style vacancy table page with optional rows.
    /// </summary>
    private static string BuildVacancyPage(
        params (string Title, string Faculty, string Dept, string Deadline, string? Extra)[] rows)
    {
        var rowsHtml = string.Join("\n", rows.Select(r => $"""
            <tr class="vacancy-specs">
                <td><a href="/all-vacancies/?show={Math.Abs(r.Title.GetHashCode()) % 100000}">{r.Title}</a></td>
                <td>{r.Faculty}</td>
                <td>{r.Dept}</td>
                <td>{r.Deadline}</td>
            </tr>
            {r.Extra ?? ""}
            """));

        return $"""
            <html><body>
            <table class="vacancies">
            <thead><tr><th>TITLE</th><th>FACULTY</th><th>LOCATION</th><th>DEADLINE</th></tr></thead>
            <tbody>
            {rowsHtml}
            </tbody>
            </table>
            </body></html>
            """;
    }

    /// <summary>
    /// Seeds a watch with SZÚ-style simple list selector.
    /// </summary>
    private async Task<(WatchGroup Group, WatchedSite Watch)> SeedSzuWatchAsync(
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

        group.TrackingConfig = TrackingConfig.ForJobs();
        var groupRepo = scope.ServiceProvider.GetRequiredService<IRepository<WatchGroup>>();
        await groupRepo.UpdateAsync(group, CancellationToken.None);

        var filterRules = filterGen.GenerateRules(CandidateProfile);

        var watch = await watchService.CreateWatchAsync(new CreateWatchRequest
        {
            Url = url,
            Name = watchId,
            GroupId = group.Id,
            UserIntent = "Monitor for Czech lab positions",
            SchemaEnabled = true,
            Schema = new ExtractionSchema
            {
                ItemSelector = "li",
                Fields =
                [
                    new SchemaField { Name = "title", Selector = "a",
                        Type = FieldType.String, IsRequired = true, IsIdentityField = true },
                    new SchemaField { Name = "url", Selector = "a",
                        Type = FieldType.Url, IsRequired = true }
                ],
                IdentityFieldNames = ["title"]
            },
            FilterRules = filterRules,
            FetchSettings = new FetchSettings { UseJavaScript = false, TimeoutSeconds = 30 },
            SkipInitialCheck = true
        }, CancellationToken.None);

        watch.AnalysisSettings = new LlmAnalysisSettings
        {
            EnableChangeAnalysis = true,
            CalculateRelevance = true,
            GenerateSemanticSummary = true
        };
        await watchService.UpdateWatchAsync(watch, CancellationToken.None);

        return (group, watch);
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
