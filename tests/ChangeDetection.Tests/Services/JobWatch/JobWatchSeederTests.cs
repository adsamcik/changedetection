using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using ChangeDetection.Services.JobWatch;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.JobWatch;

/// <summary>
/// Unit tests for JobWatchSeeder — verifies portal definitions and seeding logic.
/// </summary>
[Category("Unit")]
public class JobWatchSeederTests : TestBase
{
    private const string TestProfile = """
        {
            "education": { "level": "MSc", "field": "molecular and cell biology" },
            "techniques_strong": ["PCR", "qPCR", "cell culture", "ELISA"],
            "techniques_basic": ["CRISPR"],
            "techniques_none": ["organoid culture", "mass spectrometry"],
            "target_locations": ["Prague", "Copenhagen", "Malmö"],
            "dealbreakers": ["SOTIO"]
        }
        """;

    [Test]
    public async Task GetAllPortalDefinitions_Returns17Portals()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();

        Log($"Found {portals.Count} portal definitions");
        portals.Count.ShouldBe(17);

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetAllPortalDefinitions_Tier1_Has10Portals()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();
        var tier1 = portals.Where(p => p.Tier == "tier-1").ToList();

        tier1.Count.ShouldBe(10);
        tier1.ShouldAllBe(p => p.CheckInterval <= TimeSpan.FromHours(48));

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetAllPortalDefinitions_Tier2_Has7Portals()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();
        var tier2 = portals.Where(p => p.Tier == "tier-2").ToList();

        tier2.Count.ShouldBe(7);
        tier2.ShouldAllBe(p => p.CheckInterval == TimeSpan.FromDays(7));

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetAllPortalDefinitions_AllHaveValidSchemas()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();

        foreach (var portal in portals)
        {
            portal.Schema.ShouldNotBeNull($"Portal {portal.Id} should have a schema");
            portal.Schema.ItemSelector.ShouldNotBeNullOrWhiteSpace($"Portal {portal.Id} should have an ItemSelector");
            portal.Schema.Fields.ShouldNotBeEmpty($"Portal {portal.Id} should have fields");
            portal.Schema.IdentityFieldNames.ShouldNotBeEmpty($"Portal {portal.Id} should have identity fields");

            // All schemas should have at least title and url
            portal.Schema.Fields.ShouldContain(f => f.Name == "title",
                $"Portal {portal.Id} must have 'title' field");
            portal.Schema.Fields.ShouldContain(f => f.Name == "url",
                $"Portal {portal.Id} must have 'url' field");

            // Title should always be an identity field
            portal.Schema.IdentityFieldNames.ShouldContain("title",
                $"Portal {portal.Id} must use 'title' as identity field");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetAllPortalDefinitions_JsPortals_HaveCorrectFetchSettings()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();
        var jsPortals = portals.Where(p => p.FetchSettings.UseJavaScript).ToList();

        Log($"Found {jsPortals.Count} JS-rendered portals");
        jsPortals.Count.ShouldBeGreaterThan(5, "Should have several JS-rendered portals");

        foreach (var portal in jsPortals)
        {
            portal.FetchSettings.TimeoutSeconds.ShouldBeGreaterThanOrEqualTo(45,
                $"JS portal {portal.Id} should have longer timeout");
            portal.FetchSettings.WaitAfterLoadMs.ShouldBeGreaterThan(0,
                $"JS portal {portal.Id} should have WaitAfterLoadMs");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetAllPortalDefinitions_AllHaveUniqueIds()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();
        var ids = portals.Select(p => p.Id).ToList();

        ids.Count.ShouldBe(ids.Distinct().Count(), "All portal IDs should be unique");

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetAllPortalDefinitions_AllHaveUniqueUrls()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();
        var urls = portals.Select(p => p.Url).ToList();

        urls.Count.ShouldBe(urls.Distinct().Count(), "All portal URLs should be unique");

        await Task.CompletedTask;
    }

    [Test]
    public async Task GetAllPortalDefinitions_SpecificPortals_HaveExpectedConfig()
    {
        var portals = JobWatchSeeder.GetAllPortalDefinitions();

        // UCPH: simple HTML, daily
        var ucph = portals.First(p => p.Id == "watch-ucph");
        ucph.FetchSettings.UseJavaScript.ShouldBeFalse();
        ucph.CheckInterval.ShouldBe(TimeSpan.FromHours(24));
        ucph.ExtraTags.ShouldContain("denmark");

        // Novo Nordisk: heavy JS, daily
        var novo = portals.First(p => p.Id == "watch-novo");
        novo.FetchSettings.UseJavaScript.ShouldBeTrue();
        novo.FetchSettings.TimeoutSeconds.ShouldBeGreaterThanOrEqualTo(60);
        novo.ExtraTags.ShouldContain("pharma");

        // SZÚ: simple HTML, every 2 days
        var szu = portals.First(p => p.Id == "watch-szu");
        szu.FetchSettings.UseJavaScript.ShouldBeFalse();
        szu.CheckInterval.ShouldBe(TimeSpan.FromHours(48));
        szu.ExtraTags.ShouldContain("czech");

        // Bavarian Nordic: Workday, weekly
        var bavarian = portals.First(p => p.Id == "watch-bavarian");
        bavarian.FetchSettings.UseJavaScript.ShouldBeTrue();
        bavarian.CheckInterval.ShouldBe(TimeSpan.FromDays(7));
        bavarian.ExtraTags.ShouldContain("workday");

        await Task.CompletedTask;
    }

    [Test]
    public async Task SeedAsync_CreatesGroupAndWatches()
    {
        var groupService = Substitute.For<IWatchGroupService>();
        var watchService = Substitute.For<IWatchService>();
        var filterGen = new ProfileFilterRuleGenerator();

        var fakeGroup = new WatchGroup { Id = Guid.NewGuid(), Name = "Test" };
        groupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(fakeGroup);

        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<CreateWatchRequest>();
                return new WatchedSite { Id = Guid.NewGuid(), Url = req.Url, Name = req.Name };
            });

        var seeder = new JobWatchSeeder(groupService, watchService, filterGen, CreateLogger<JobWatchSeeder>());

        var result = await seeder.SeedAsync(TestProfile, "Monitor biotech jobs");

        result.Group.ShouldNotBeNull();
        result.CreatedCount.ShouldBe(17);

        // Group created once
        await groupService.Received(1).CreateGroupAsync(
            Arg.Is<WatchGroupCreateRequest>(r =>
                r.Name.Contains("Biotech") &&
                r.AnalysisProfileJson == TestProfile),
            Arg.Any<CancellationToken>());

        // 17 watches created
        await watchService.Received(17).CreateWatchAsync(
            Arg.Any<CreateWatchRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SeedAsync_WatchesHaveFilterRules()
    {
        var groupService = Substitute.For<IWatchGroupService>();
        var watchService = Substitute.For<IWatchService>();
        var filterGen = new ProfileFilterRuleGenerator();

        groupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = Guid.NewGuid(), Name = "Test" });

        var createdRequests = new List<CreateWatchRequest>();
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<CreateWatchRequest>();
                createdRequests.Add(req);
                return new WatchedSite { Id = Guid.NewGuid(), Url = req.Url };
            });

        var seeder = new JobWatchSeeder(groupService, watchService, filterGen, CreateLogger<JobWatchSeeder>());
        await seeder.SeedAsync(TestProfile, "Monitor biotech jobs");

        createdRequests.ShouldNotBeEmpty();

        // Each watch should have filter rules from the profile
        foreach (var req in createdRequests)
        {
            req.FilterRules.ShouldNotBeNull($"Watch {req.Name} should have filter rules");
            req.FilterRules!.Count.ShouldBeGreaterThan(0,
                $"Watch {req.Name} should have at least one filter rule");

            // Should have SOTIO dealbreaker rule
            req.FilterRules.ShouldContain(r => r.Name.Contains("SOTIO"),
                $"Watch {req.Name} should have SOTIO dealbreaker rule");
        }
    }

    [Test]
    public async Task SeedAsync_WatchesHaveGroupIdAndUserIntent()
    {
        var groupService = Substitute.For<IWatchGroupService>();
        var watchService = Substitute.For<IWatchService>();
        var filterGen = new ProfileFilterRuleGenerator();
        var groupId = Guid.NewGuid();

        groupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = groupId, Name = "Test" });

        var createdRequests = new List<CreateWatchRequest>();
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var req = callInfo.Arg<CreateWatchRequest>();
                createdRequests.Add(req);
                return new WatchedSite { Id = Guid.NewGuid(), Url = req.Url };
            });

        var seeder = new JobWatchSeeder(groupService, watchService, filterGen, CreateLogger<JobWatchSeeder>());
        await seeder.SeedAsync(TestProfile, "Monitor biotech jobs");

        foreach (var req in createdRequests)
        {
            req.GroupId.ShouldBe(groupId, $"Watch {req.Name} should be in the group");
            req.UserIntent.ShouldBe("Monitor biotech jobs",
                $"Watch {req.Name} should have user intent");
            req.SchemaEnabled.ShouldBeTrue($"Watch {req.Name} should have schema enabled");
        }
    }
}
