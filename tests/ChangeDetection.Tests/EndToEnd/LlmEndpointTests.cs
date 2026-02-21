using System.Net;
using System.Net.Http.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Shared.Dtos;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class LlmEndpointTests : TestBase, IAsyncDisposable
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

    private LlmProviderCreateDto MakeProvider(string type = "Ollama", int priority = 1) => new()
    {
        ProviderType = type,
        Endpoint = "http://localhost:11434",
        ModelId = "llama3",
        Priority = priority,
        IsEnabled = true
    };

    private async Task<LlmProviderDto> CreateProviderAsync(LlmProviderCreateDto? dto = null)
    {
        dto ??= MakeProvider();
        var response = await _client.PostAsJsonAsync("/api/llm/providers", dto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<LlmProviderDto>();
        created.ShouldNotBeNull();
        return created;
    }

    // --- Provider CRUD ---

    [Test]
    public async Task GetProviders_Empty_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/llm/providers");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var providers = await response.Content.ReadFromJsonAsync<List<LlmProviderDto>>();
        providers.ShouldNotBeNull();
        providers.ShouldBeEmpty();
    }

    [Test]
    public async Task CreateProvider_ValidData_Creates()
    {
        var dto = MakeProvider();
        var response = await _client.PostAsJsonAsync("/api/llm/providers", dto);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var created = await response.Content.ReadFromJsonAsync<LlmProviderDto>();
        created.ShouldNotBeNull();
        created.Id.ShouldNotBeNullOrEmpty();
        created.ProviderType.ShouldBe("Ollama");
        created.Endpoint.ShouldBe("http://localhost:11434");
        created.ModelId.ShouldBe("llama3");
        created.IsEnabled.ShouldBeTrue();
    }

    [Test]
    public async Task CreateProvider_ThenGet_ReturnsCreated()
    {
        var created = await CreateProviderAsync();

        var response = await _client.GetAsync("/api/llm/providers");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var providers = await response.Content.ReadFromJsonAsync<List<LlmProviderDto>>();
        providers.ShouldNotBeNull();
        providers.ShouldContain(p => p.Id == created.Id);
    }

    [Test]
    public async Task UpdateProvider_ExistingProvider_Updates()
    {
        var created = await CreateProviderAsync();

        var updateDto = new LlmProviderCreateDto
        {
            ProviderType = "OpenAI",
            Endpoint = "https://api.openai.com",
            ModelId = "gpt-4",
            Priority = 5,
            IsEnabled = false
        };

        var response = await _client.PutAsJsonAsync($"/api/llm/providers/{created.Id}", updateDto);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var updated = await response.Content.ReadFromJsonAsync<LlmProviderDto>();
        updated.ShouldNotBeNull();
        updated.ProviderType.ShouldBe("OpenAI");
        updated.Endpoint.ShouldBe("https://api.openai.com");
        updated.ModelId.ShouldBe("gpt-4");
        updated.Priority.ShouldBe(5);
        updated.IsEnabled.ShouldBeFalse();
    }

    [Test]
    public async Task DeleteProvider_ExistingProvider_Deletes()
    {
        var created = await CreateProviderAsync();

        var deleteResponse = await _client.DeleteAsync($"/api/llm/providers/{created.Id}");
        deleteResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify gone from list
        var listResponse = await _client.GetAsync("/api/llm/providers");
        var providers = await listResponse.Content.ReadFromJsonAsync<List<LlmProviderDto>>();
        providers.ShouldNotBeNull();
        providers.ShouldNotContain(p => p.Id == created.Id);
    }

    [Test]
    public async Task EnableProvider_SetsEnabled()
    {
        var dto = MakeProvider();
        dto.IsEnabled = false;
        var created = await CreateProviderAsync(dto);

        var response = await _client.PostAsync($"/api/llm/providers/{created.Id}/enable", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify via GET
        var listResponse = await _client.GetAsync("/api/llm/providers");
        var providers = await listResponse.Content.ReadFromJsonAsync<List<LlmProviderDto>>();
        providers.ShouldNotBeNull();
        providers.ShouldContain(p => p.Id == created.Id && p.IsEnabled);
    }

    [Test]
    public async Task DisableProvider_SetsDisabled()
    {
        var created = await CreateProviderAsync();

        var response = await _client.PostAsync($"/api/llm/providers/{created.Id}/disable", null);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify via GET
        var listResponse = await _client.GetAsync("/api/llm/providers");
        var providers = await listResponse.Content.ReadFromJsonAsync<List<LlmProviderDto>>();
        providers.ShouldNotBeNull();
        providers.ShouldContain(p => p.Id == created.Id && !p.IsEnabled);
    }

    // --- Health / Usage ---

    [Test]
    public async Task GetHealth_ReturnsStatus()
    {
        var response = await _client.GetAsync("/api/llm/providers/health");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var health = await response.Content.ReadFromJsonAsync<List<ProviderHealthStatus>>();
        health.ShouldNotBeNull();
    }

    [Test]
    public async Task GetUsage_ReturnsStats()
    {
        var response = await _client.GetAsync("/api/llm/usage");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var stats = await response.Content.ReadFromJsonAsync<LlmUsageStatsDto>();
        stats.ShouldNotBeNull();
        stats.TotalRequests.ShouldBe(0);
        stats.SuccessCount.ShouldBe(0);
        stats.FailureCount.ShouldBe(0);
    }

    // --- Logs ---

    [Test]
    public async Task GetLogs_ReturnsLogs()
    {
        var response = await _client.GetAsync("/api/llm/logs");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var logs = await response.Content.ReadFromJsonAsync<LlmLogsResponse>();
        logs.ShouldNotBeNull();
        logs.TotalCount.ShouldBe(0);
        logs.Logs.ShouldBeEmpty();
    }

    [Test]
    public async Task DeleteLogs_ClearsLogs()
    {
        var response = await _client.DeleteAsync("/api/llm/logs");
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // --- Sessions ---

    [Test]
    public async Task GetPendingSetups_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/llm/pending-setups");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var pending = await response.Content.ReadFromJsonAsync<List<PendingSetupDto>>();
        pending.ShouldNotBeNull();
        pending.ShouldBeEmpty();
    }
}
