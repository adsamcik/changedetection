using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class OutreachExportEndpointTests : TestBase, IAsyncDisposable
{
    private TestWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

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

    [Test]
    public async Task ExportOutreach_NonExistentGroup_Returns404()
    {
        var response = await _client.GetAsync($"/api/groups/{Guid.NewGuid()}/outreach/export");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ExportOutreach_EmptyGroup_ReturnsEmptyCompanies()
    {
        var group = await CreateGroupAsync("Empty Outreach");

        var response = await _client.GetAsync($"/api/groups/{group.Id}/outreach/export");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var export = await response.Content.ReadFromJsonAsync<OutreachExportDto>();
        export.ShouldNotBeNull();
        export.GroupId.ShouldBe(group.Id);
        export.GroupName.ShouldBe("Empty Outreach");
        export.Companies.ShouldBeEmpty();
    }

    [Test]
    public async Task ExportOutreach_WithOutreachWatch_ReturnsCompany()
    {
        var group = await CreateGroupAsync("Outreach Export");
        var watchId = await CreateAndAddWatchAsync(group.Id,
            "https://example.com/careers", "BioTech Inc (Teamtailor)");

        // Seed outreach assessment directly on the watch
        await SeedOutreachAssessmentAsync(watchId, new OutreachAssessment(true,
        [
            new OutreachSignal("GeneralApplication", "Submit a general application", 0.95f),
            new OutreachSignal("TalentCommunity", "Join our talent community", 0.85f)
        ], 8.5f));

        var response = await _client.GetAsync($"/api/groups/{group.Id}/outreach/export");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var export = await response.Content.ReadFromJsonAsync<OutreachExportDto>();
        export.ShouldNotBeNull();
        export.Companies.Count.ShouldBe(1);
        export.Companies[0].Name.ShouldBe("BioTech Inc");
        export.Companies[0].Url.ShouldBe("https://example.com/careers");
        export.Companies[0].Score.ShouldBe(8.5f);
        export.Companies[0].Signals.Count.ShouldBe(2);
        export.Companies[0].OutreachChannel.ShouldBe("General application page");
    }

    [Test]
    public async Task ExportOutreach_NonOutreachWatch_Excluded()
    {
        var group = await CreateGroupAsync("No Outreach");
        var watchId = await CreateAndAddWatchAsync(group.Id,
            "https://example.com/jobs", "Regular Corp");

        // Seed a non-outreach-friendly assessment
        await SeedOutreachAssessmentAsync(watchId, new OutreachAssessment(false, [], 0f));

        var response = await _client.GetAsync($"/api/groups/{group.Id}/outreach/export");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var export = await response.Content.ReadFromJsonAsync<OutreachExportDto>();
        export.ShouldNotBeNull();
        export.Companies.ShouldBeEmpty();
    }

    [Test]
    public async Task ExportOutreach_LatexFormat_ReturnsPlainText()
    {
        var group = await CreateGroupAsync("LaTeX Export");
        var watchId = await CreateAndAddWatchAsync(group.Id,
            "https://again.teamtailor.com", "Again.bio (Teamtailor)");

        await SeedOutreachAssessmentAsync(watchId, new OutreachAssessment(true,
        [
            new OutreachSignal("GeneralApplication", "general application page", 0.95f)
        ], 7.5f));

        var response = await _client.GetAsync($"/api/groups/{group.Id}/outreach/export?format=latex");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        contentType.ShouldBe("text/plain");

        var latex = await response.Content.ReadAsStringAsync();
        latex.ShouldContain(@"\begin{longtable}");
        latex.ShouldContain(@"\end{longtable}");
        latex.ShouldContain("Again.bio");
        latex.ShouldContain(@"\textbf{Společnost}"); // Czech header
        latex.ShouldContain(@"\textbf{Skóre}");      // Czech header
    }

    [Test]
    public async Task ExportOutreach_LatexFormat_EscapesSpecialChars()
    {
        var group = await CreateGroupAsync("Escape Test");
        var watchId = await CreateAndAddWatchAsync(group.Id,
            "https://example.com/careers", "R&D Bio_Tech #1");

        await SeedOutreachAssessmentAsync(watchId, new OutreachAssessment(true,
        [
            new OutreachSignal("SendCV", "send us your CV at hr@company.com", 0.90f)
        ], 6.0f));

        var response = await _client.GetAsync($"/api/groups/{group.Id}/outreach/export?format=latex");
        var latex = await response.Content.ReadAsStringAsync();

        // Special chars should be escaped
        latex.ShouldContain(@"R\&D Bio\_Tech \#1");
    }

    [Test]
    public async Task ExportOutreach_MultipleWatches_SortedByScore()
    {
        var group = await CreateGroupAsync("Multi Outreach");

        var w1 = await CreateAndAddWatchAsync(group.Id, "https://a.com", "Low Score Co");
        var w2 = await CreateAndAddWatchAsync(group.Id, "https://b.com", "High Score Co");
        var w3 = await CreateAndAddWatchAsync(group.Id, "https://c.com", "Mid Score Co");

        await SeedOutreachAssessmentAsync(w1, new OutreachAssessment(true,
            [new OutreachSignal("AlwaysHiring", "always looking", 0.8f)], 3.5f));
        await SeedOutreachAssessmentAsync(w2, new OutreachAssessment(true,
            [new OutreachSignal("GeneralApplication", "general application", 0.95f)], 9.0f));
        await SeedOutreachAssessmentAsync(w3, new OutreachAssessment(true,
            [new OutreachSignal("TalentCommunity", "talent pool", 0.85f)], 6.0f));

        var response = await _client.GetAsync($"/api/groups/{group.Id}/outreach/export");
        var export = await response.Content.ReadFromJsonAsync<OutreachExportDto>();

        export.ShouldNotBeNull();
        export.Companies.Count.ShouldBe(3);
        export.Companies[0].Score.ShouldBe(9.0f); // Highest first
        export.Companies[1].Score.ShouldBe(6.0f);
        export.Companies[2].Score.ShouldBe(3.5f);
    }

    [Test]
    public async Task ExportOutreach_EmailSignal_ExtractsChannel()
    {
        var group = await CreateGroupAsync("Email Channel");
        var watchId = await CreateAndAddWatchAsync(group.Id,
            "https://biotech.com/careers", "BioTech");

        await SeedOutreachAssessmentAsync(watchId, new OutreachAssessment(true,
        [
            new OutreachSignal("NamedRecruiter",
                "email our HR team at careers@biotech.com for inquiries", 0.70f)
        ], 5.0f));

        var response = await _client.GetAsync($"/api/groups/{group.Id}/outreach/export");
        var export = await response.Content.ReadFromJsonAsync<OutreachExportDto>();

        export.ShouldNotBeNull();
        export.Companies[0].OutreachChannel.ShouldBe("careers@biotech.com");
    }

    // --- Helpers ---

    private async Task<WatchGroupDetailDto> CreateGroupAsync(string name)
    {
        var dto = new WatchGroupCreateDto { Name = name };
        var response = await _client.PostAsJsonAsync("/api/groups", dto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<WatchGroupDetailDto>();
        created.ShouldNotBeNull();
        return created;
    }

    private async Task<string> CreateAndAddWatchAsync(string groupId, string url, string name)
    {
        var watchDto = new WatchCreateDto
        {
            Url = url,
            Title = name,
            CheckInterval = TimeSpan.FromHours(1)
        };
        var watchResponse = await _client.PostAsJsonAsync("/api/watches", watchDto);
        watchResponse.EnsureSuccessStatusCode();
        var json = await watchResponse.Content.ReadFromJsonAsync<JsonElement>();
        var watchId = json.GetProperty("id").GetString()!;

        await _client.PostAsync($"/api/groups/{groupId}/members/{watchId}", null);
        return watchId;
    }

    private async Task SeedOutreachAssessmentAsync(string watchId, OutreachAssessment assessment)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IRepository<WatchedSite>>();
        var watch = await repo.GetByIdAsync(Guid.Parse(watchId));
        watch.ShouldNotBeNull();
        watch.OutreachAssessmentJson = OutreachSignalDetector.Serialize(assessment);
        watch.OutreachLastScannedAt = DateTime.UtcNow;
        await repo.UpdateAsync(watch);
    }
}
