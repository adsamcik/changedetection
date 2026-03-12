using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

/// <summary>
/// End-to-end tests for the Job Watch feature through real HTTP endpoints.
/// Uses TestWebApplicationFactory with isolated LiteDB — no mocks for service layer.
/// </summary>
[Category("Integration")]
public class JobWatchEndpointTests : TestBase, IAsyncDisposable
{
    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    private const string TestProfile = """
        {
            "education": { "level": "MSc", "field": "molecular and cell biology" },
            "experience_years": "1-3",
            "techniques_strong": ["PCR", "qPCR", "cell culture", "ELISA", "flow cytometry"],
            "techniques_basic": ["CRISPR"],
            "techniques_none": ["organoid culture", "mass spectrometry", "NGS library prep"],
            "target_locations": ["Prague", "Copenhagen", "Lyngby", "Bagsværd", "Malmö", "Lund"],
            "languages": { "Czech": "native", "English": "C1" },
            "salary_floor": { "prague_czk": 50000, "copenhagen_dkk": 30000 },
            "dealbreakers": ["SOTIO", "animal-heavy work"],
            "preferences": ["variety", "autonomy", "intellectual challenge"]
        }
        """;

    [Before(Test)]
    public void Setup()
    {
        _factory = new TestWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_factory != null) await _factory.DisposeAsync();
    }

    #region Seed Endpoint Tests

    [Test]
    public async Task SeedJobWatch_WithProfile_ReturnsCreated()
    {
        var request = new { ProfileJson = TestProfile, UserIntent = "Monitor biotech jobs" };

        var response = await _client.PostAsJsonAsync("/api/jobwatch/seed", request);

        Log($"Status: {response.StatusCode}");
        var body = await response.Content.ReadAsStringAsync();
        Log($"Body: {body}");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var result = JsonSerializer.Deserialize<JsonElement>(body);
        result.GetProperty("groupId").GetString().ShouldNotBeNullOrWhiteSpace();
        result.GetProperty("name").GetString().ShouldContain("Biotech");
        result.GetProperty("portalCount").GetInt32().ShouldBe(17);
    }

    [Test]
    public async Task SeedJobWatch_WithEmptyProfile_ReturnsBadRequest()
    {
        var request = new { ProfileJson = "", UserIntent = "Test" };

        var response = await _client.PostAsJsonAsync("/api/jobwatch/seed", request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SeedJobWatch_CreatesGroupWithProfile()
    {
        var request = new { ProfileJson = TestProfile, UserIntent = "Monitor biotech jobs in Prague and Copenhagen" };
        var seedResponse = await _client.PostAsJsonAsync("/api/jobwatch/seed", request);
        seedResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var seedResult = await seedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = seedResult.GetProperty("groupId").GetString();

        // Verify the group exists and has the profile
        var groupResponse = await _client.GetAsync($"/api/groups/{groupId}");
        groupResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var group = await groupResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        group.ShouldNotBeNull();
        group.Name.ShouldContain("Biotech");
        group.AnalysisProfileJson.ShouldNotBeNullOrWhiteSpace("Group should have analysis profile");
        group.UserIntent.ShouldContain("biotech", Case.Insensitive);
        group.Tags.ShouldContain("job-search");
        group.Tags.ShouldContain("biotech");
    }

    [Test]
    public async Task SeedJobWatch_CreatesWatchesLinkedToGroup()
    {
        var request = new { ProfileJson = TestProfile };
        var seedResponse = await _client.PostAsJsonAsync("/api/jobwatch/seed", request);
        seedResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var seedResult = await seedResponse.Content.ReadFromJsonAsync<JsonElement>();
        var groupId = seedResult.GetProperty("groupId").GetString();

        // Verify group has members
        var groupResponse = await _client.GetAsync($"/api/groups/{groupId}");
        var group = await groupResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        group.ShouldNotBeNull();

        Log($"Group has {group.Members.Count} members");
        group.Members.Count.ShouldBeGreaterThanOrEqualTo(15, "Should have most portal watches (some may fail in test env)");

        // Verify watches exist in the watch list
        var watchesResponse = await _client.GetAsync("/api/watches");
        watchesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task SeedJobWatch_WatchesAccessible()
    {
        var request = new { ProfileJson = TestProfile };
        var seedResponse = await _client.PostAsJsonAsync("/api/jobwatch/seed", request);
        seedResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Verify watches are accessible via the list endpoint
        var watchesResponse = await _client.GetAsync("/api/watches");
        watchesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var watches = await watchesResponse.Content.ReadFromJsonAsync<List<JsonElement>>();
        watches.ShouldNotBeNull();
        Log($"Total watches: {watches.Count}");
        watches.Count.ShouldBeGreaterThanOrEqualTo(15, "Most portal watches should be created");

        // Spot-check that at least one has job-search tags
        // WatchListItemDto.Tags is List<TagDto> where TagDto has { name, color }
        var jobWatches = watches.Where(w =>
        {
            if (w.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                return tags.EnumerateArray().Any(t =>
                    t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == "job-search");
            }
            return false;
        }).ToList();

        jobWatches.Count.ShouldBeGreaterThanOrEqualTo(10, "Most watches should be tagged 'job-search'");
    }

    #endregion

    #region Portals Endpoint Tests

    [Test]
    public async Task GetPortals_Returns17Definitions()
    {
        var response = await _client.GetAsync("/api/jobwatch/portals");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var portals = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        portals.ShouldNotBeNull();
        portals.Count.ShouldBe(17);
    }

    [Test]
    public async Task GetPortals_AllHaveRequiredFields()
    {
        var response = await _client.GetAsync("/api/jobwatch/portals");
        var portals = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        portals.ShouldNotBeNull();

        foreach (var portal in portals)
        {
            var id = portal.GetProperty("id").GetString();
            portal.GetProperty("name").GetString().ShouldNotBeNullOrWhiteSpace($"Portal {id} needs a name");
            portal.GetProperty("url").GetString().ShouldNotBeNullOrWhiteSpace($"Portal {id} needs a URL");
            portal.GetProperty("tier").GetString().ShouldNotBeNullOrWhiteSpace($"Portal {id} needs a tier");

            var schemaFields = portal.GetProperty("schemaFields");
            schemaFields.GetArrayLength().ShouldBeGreaterThan(0, $"Portal {id} needs schema fields");

            // Every portal should have at least a title field
            var fieldNames = schemaFields.EnumerateArray()
                .Select(f => f.GetProperty("name").GetString()).ToList();
            fieldNames.ShouldContain("title", $"Portal {id} must have title field");
        }
    }

    [Test]
    public async Task GetPortals_TierDistribution()
    {
        var response = await _client.GetAsync("/api/jobwatch/portals");
        var portals = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        portals.ShouldNotBeNull();

        var tier1 = portals.Count(p => p.GetProperty("tier").GetString() == "tier-1");
        var tier2 = portals.Count(p => p.GetProperty("tier").GetString() == "tier-2");

        Log($"Tier 1: {tier1}, Tier 2: {tier2}");
        tier1.ShouldBe(10, "Should have 10 Tier-1 portals (daily)");
        tier2.ShouldBe(7, "Should have 7 Tier-2 portals (weekly)");
    }

    [Test]
    public async Task GetPortals_JsPortalsIdentified()
    {
        var response = await _client.GetAsync("/api/jobwatch/portals");
        var portals = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        portals.ShouldNotBeNull();

        var jsPortals = portals.Where(p => p.GetProperty("needsJavaScript").GetBoolean()).ToList();

        Log($"JS-rendered portals: {jsPortals.Count}");
        jsPortals.Count.ShouldBeGreaterThan(5, "Multiple portals need JS rendering");

        // Novo Nordisk and Bavarian Nordic should definitely need JS
        var novoId = portals.FirstOrDefault(p => p.GetProperty("id").GetString() == "watch-novo");
        novoId.GetProperty("needsJavaScript").GetBoolean().ShouldBeTrue("Novo Nordisk needs JS");

        var bavarianId = portals.FirstOrDefault(p => p.GetProperty("id").GetString() == "watch-bavarian");
        bavarianId.GetProperty("needsJavaScript").GetBoolean().ShouldBeTrue("Bavarian Nordic (Workday) needs JS");
    }

    #endregion

    #region Profile Update via Group Endpoint

    [Test]
    public async Task UpdateGroup_WithAnalysisProfile_Persists()
    {
        // Create a group
        var createDto = new WatchGroupCreateDto
        {
            Name = "Job Search Test",
            Description = "Test profile persistence"
        };
        var createResponse = await _client.PostAsJsonAsync("/api/groups", createDto);
        var created = await createResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        created.ShouldNotBeNull();

        // Update with analysis profile
        var updateDto = new WatchGroupUpdateDto
        {
            AnalysisProfileJson = TestProfile
        };
        var updateResponse = await _client.PutAsJsonAsync($"/api/groups/{created.Id}", updateDto);
        updateResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await updateResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        updated.ShouldNotBeNull();
        updated.AnalysisProfileJson.ShouldNotBeNullOrWhiteSpace();

        // Verify the profile JSON parses correctly
        var profileDoc = JsonDocument.Parse(updated.AnalysisProfileJson!);
        var root = profileDoc.RootElement;
        root.GetProperty("education").GetProperty("level").GetString().ShouldBe("MSc");
        root.GetProperty("techniques_none").GetArrayLength().ShouldBe(3);
        root.GetProperty("dealbreakers").GetArrayLength().ShouldBe(2);
    }

    [Test]
    public async Task UpdateGroup_ProfileRoundTrips()
    {
        var createDto = new WatchGroupCreateDto { Name = "Roundtrip Profile Test" };
        var createResponse = await _client.PostAsJsonAsync("/api/groups", createDto);
        var created = await createResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        created.ShouldNotBeNull();
        created.AnalysisProfileJson.ShouldBeNull("New group should not have a profile");

        // Set profile
        var updateDto = new WatchGroupUpdateDto { AnalysisProfileJson = TestProfile };
        await _client.PutAsJsonAsync($"/api/groups/{created.Id}", updateDto);

        // Get and verify
        var getResponse = await _client.GetAsync($"/api/groups/{created.Id}");
        var fetched = await getResponse.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        fetched.ShouldNotBeNull();
        fetched.AnalysisProfileJson.ShouldNotBeNullOrWhiteSpace();

        var profile = JsonDocument.Parse(fetched.AnalysisProfileJson!).RootElement;
        profile.GetProperty("target_locations").GetArrayLength().ShouldBe(6);
    }

    #endregion
}
