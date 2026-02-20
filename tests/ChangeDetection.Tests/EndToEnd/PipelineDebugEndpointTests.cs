using System.Net;
using System.Net.Http.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Endpoints;
using ChangeDetection.Shared.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class PipelineDebugEndpointTests : TestBase, IAsyncDisposable
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
    public async Task GetRecentRuns_ReturnsOk_WhenEmpty()
    {
        var response = await _client.GetAsync("/api/debug/pipeline/runs");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var runs = await response.Content.ReadFromJsonAsync<List<PipelineRunSummaryDto>>();
        runs.ShouldNotBeNull();
        runs.Count.ShouldBe(0);
    }

    [Test]
    public async Task GetRecentRuns_ReturnsRuns_AfterCreatingOne()
    {
        await SeedPipelineRun("test input");

        var response = await _client.GetAsync("/api/debug/pipeline/runs");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var runs = await response.Content.ReadFromJsonAsync<List<PipelineRunSummaryDto>>();
        runs.ShouldNotBeNull();
        runs.Count.ShouldBeGreaterThanOrEqualTo(1);
        runs[0].OriginalInput.ShouldBe("test input");
    }

    [Test]
    public async Task GetRunDetail_ReturnsNotFound_ForMissingRun()
    {
        var response = await _client.GetAsync($"/api/debug/pipeline/runs/{Guid.NewGuid()}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetRunDetail_ReturnsRunWithEvents()
    {
        var run = await SeedPipelineRunWithEvents();

        var response = await _client.GetAsync($"/api/debug/pipeline/runs/{run.Id}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<PipelineRunDetailDto>();
        detail.ShouldNotBeNull();
        detail.Run.Id.ShouldBe(run.Id);
        detail.Events.ShouldNotBeNull();
        detail.Events.Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public async Task GetRunEvents_ReturnsEvents()
    {
        var run = await SeedPipelineRunWithEvents();

        var response = await _client.GetAsync($"/api/debug/pipeline/runs/{run.Id}/events");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var events = await response.Content.ReadFromJsonAsync<List<PipelineEventDto>>();
        events.ShouldNotBeNull();
        events.Count.ShouldBeGreaterThan(0);
        events[0].Stage.ShouldBe(PipelineStageNames.UrlExtraction);
    }

    [Test]
    public async Task GetRunBySession_ReturnsNotFound_ForMissingSession()
    {
        var response = await _client.GetAsync($"/api/debug/pipeline/sessions/{Guid.NewGuid()}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetRunBySession_ReturnsRun_ForValidSession()
    {
        var run = await SeedPipelineRun("session lookup test");

        var response = await _client.GetAsync($"/api/debug/pipeline/sessions/{run.SessionId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var detail = await response.Content.ReadFromJsonAsync<PipelineRunDetailDto>();
        detail.ShouldNotBeNull();
        detail.Run.SessionId.ShouldBe(run.SessionId);
    }

    [Test]
    public async Task GetActiveSessions_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/debug/pipeline/active");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var active = await response.Content.ReadFromJsonAsync<ActiveSessionsDto>();
        active.ShouldNotBeNull();
        active.InMemoryPipelineSessions.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task GetLlmLog_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/debug/pipeline/llm/log");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var logs = await response.Content.ReadFromJsonAsync<List<LlmLogEntryDto>>();
        logs.ShouldNotBeNull();
    }

    [Test]
    public async Task GetLlmLogEntry_ReturnsNotFound_ForMissingEntry()
    {
        var response = await _client.GetAsync($"/api/debug/pipeline/llm/log/{Guid.NewGuid()}");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task GetLlmLog_ReturnsEntries_AfterLogging()
    {
        // Seed an LLM log entry via the service
        using var scope = _factory.Services.CreateScope();
        var llmLog = scope.ServiceProvider.GetRequiredService<ILlmLogService>();
        var requestId = Guid.NewGuid();
        llmLog.Log(new LlmLogEntry
        {
            RequestId = requestId,
            Level = LlmLogLevel.Info,
            ProviderName = "TestProvider",
            Model = "test-model",
            Category = LlmLogCategory.Response,
            Message = "Test LLM call completed",
            PromptPreview = "What is the price?",
            FullPrompt = "System: You are a price extractor. User: What is the price?",
            ResponsePreview = "The price is $29.99",
            FullResponse = "The price is $29.99 based on the content provided.",
            DurationMs = 150,
            InputTokens = 42,
            OutputTokens = 18,
            IsSuccess = true
        });

        var response = await _client.GetAsync("/api/debug/pipeline/llm/log?count=10");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var logs = await response.Content.ReadFromJsonAsync<List<LlmLogEntryDto>>();
        logs.ShouldNotBeNull();
        logs.Count.ShouldBeGreaterThan(0);
        logs.ShouldContain(l => l.RequestId == requestId);

        var entry = logs.First(l => l.RequestId == requestId);
        entry.ProviderName.ShouldBe("TestProvider");
        entry.Model.ShouldBe("test-model");
        entry.IsSuccess.ShouldBe(true);
    }

    [Test]
    public async Task GetLlmLogEntry_ReturnsDetail_ForValidRequestId()
    {
        using var scope = _factory.Services.CreateScope();
        var llmLog = scope.ServiceProvider.GetRequiredService<ILlmLogService>();
        var requestId = Guid.NewGuid();
        llmLog.Log(new LlmLogEntry
        {
            RequestId = requestId,
            Level = LlmLogLevel.Info,
            ProviderName = "TestProvider",
            Category = LlmLogCategory.Response,
            Message = "Completed",
            FullPrompt = "Full prompt text here",
            FullResponse = "Full response text here",
            IsSuccess = true
        });

        var response = await _client.GetAsync($"/api/debug/pipeline/llm/log/{requestId}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var entries = await response.Content.ReadFromJsonAsync<List<LlmLogEntryDto>>();
        entries.ShouldNotBeNull();
        entries.Count.ShouldBe(1);
        entries[0].FullPrompt.ShouldBe("Full prompt text here");
        entries[0].FullResponse.ShouldBe("Full response text here");
    }

    [Test]
    public async Task GetRecentRuns_WithStatusFilter_FiltersCorrectly()
    {
        var run = await SeedPipelineRun("status filter test");

        // Run should be in Started status by default
        var response = await _client.GetAsync("/api/debug/pipeline/runs?status=Started");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var runs = await response.Content.ReadFromJsonAsync<List<PipelineRunSummaryDto>>();
        runs.ShouldNotBeNull();
        runs.ShouldContain(r => r.SessionId == run.SessionId);

        // Completed status should not include our run
        var completedResponse = await _client.GetAsync("/api/debug/pipeline/runs?status=Completed");
        completedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        var completedRuns = await completedResponse.Content.ReadFromJsonAsync<List<PipelineRunSummaryDto>>();
        completedRuns.ShouldNotBeNull();
        completedRuns.ShouldNotContain(r => r.SessionId == run.SessionId);
    }

    // --- Helpers ---

    private async Task<PipelineRun> SeedPipelineRun(string input)
    {
        using var scope = _factory.Services.CreateScope();
        var eventService = scope.ServiceProvider.GetRequiredService<IPipelineEventService>();
        return await eventService.StartRunAsync(Guid.NewGuid(), input, Guid.Empty);
    }

    private async Task<PipelineRun> SeedPipelineRunWithEvents()
    {
        using var scope = _factory.Services.CreateScope();
        var eventService = scope.ServiceProvider.GetRequiredService<IPipelineEventService>();

        var run = await eventService.StartRunAsync(Guid.NewGuid(), "test with events", Guid.Empty);

        await eventService.RecordEventAsync(
            run.Id,
            PipelineStageNames.UrlExtraction,
            PipelineEventTypes.StageStarted,
            summary: "Extracting URL from input");

        await eventService.RecordEventAsync(
            run.Id,
            PipelineStageNames.UrlExtraction,
            PipelineEventTypes.StageCompleted,
            summary: "URL extracted: https://example.com",
            isSuccess: true);

        return run;
    }
}
