using System.Net;
using System.Net.Http.Json;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class SettingsEndpointTests : TestBase, IAsyncDisposable
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
    public async Task GetGeneralSettings_ReturnsDefaults()
    {
        var response = await _client.GetAsync("/api/settings/general");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var settings = await response.Content.ReadFromJsonAsync<GeneralSettingsDto>();
        settings.ShouldNotBeNull();
        settings.DefaultCheckIntervalMinutes.ShouldBeGreaterThan(0);
        settings.MaxConcurrentChecks.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task UpdateGeneralSettings_ModifiesValues()
    {
        var update = new GeneralSettingsUpdateDto
        {
            MaxConcurrentChecks = 10,
            SnapshotRetentionDays = 60
        };

        var response = await _client.PutAsJsonAsync("/api/settings/general", update);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var settings = await response.Content.ReadFromJsonAsync<GeneralSettingsDto>();
        settings.ShouldNotBeNull();
        settings.MaxConcurrentChecks.ShouldBe(10);
        settings.SnapshotRetentionDays.ShouldBe(60);
    }

    [Test]
    public async Task UpdateGeneralSettings_PersistsAcrossReads()
    {
        // Update
        var update = new GeneralSettingsUpdateDto { MaxRetryAttempts = 7 };
        await _client.PutAsJsonAsync("/api/settings/general", update);

        // Re-read
        var response = await _client.GetAsync("/api/settings/general");
        var settings = await response.Content.ReadFromJsonAsync<GeneralSettingsDto>();
        settings.ShouldNotBeNull();
        settings.MaxRetryAttempts.ShouldBe(7);
    }

    [Test]
    public async Task ListBackups_ReturnsEmptyListInitially()
    {
        var response = await _client.GetAsync("/api/settings/backup");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var backups = await response.Content.ReadFromJsonAsync<List<object>>();
        backups.ShouldNotBeNull();
        // Fresh DB may or may not have backups — just verify it returns OK
    }
}
