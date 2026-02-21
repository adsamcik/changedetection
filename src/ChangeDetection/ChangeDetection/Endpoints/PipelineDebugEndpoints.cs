using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;
using ChangeDetection.Shared.Dtos;

namespace ChangeDetection.Endpoints;

/// <summary>
/// Debug endpoints for inspecting pipeline runs, events, LLM logs, and active sessions.
/// Provides full observability into the LLM pipeline execution for debugging stalls and failures.
/// </summary>
public static class PipelineDebugEndpoints
{
    public static RouteGroupBuilder MapPipelineDebugEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/runs", GetRecentRuns)
            .WithName("GetPipelineRuns")
            .Produces<List<PipelineRunSummaryDto>>();

        group.MapGet("/runs/{runId:guid}", GetRunDetail)
            .WithName("GetPipelineRunDetail")
            .Produces<PipelineRunDetailDto>()
            .Produces(404);

        group.MapGet("/runs/{runId:guid}/events", GetRunEvents)
            .WithName("GetPipelineRunEvents")
            .Produces<List<PipelineEventDto>>();

        group.MapGet("/sessions/{sessionId:guid}", GetRunBySession)
            .WithName("GetPipelineRunBySession")
            .Produces<PipelineRunDetailDto>()
            .Produces(404);

        group.MapGet("/active", GetActiveSessions)
            .WithName("GetActivePipelineSessions")
            .Produces<ActiveSessionsDto>();

        group.MapGet("/llm/log", GetLlmLog)
            .WithName("GetLlmLog")
            .Produces<List<LlmLogEntryDto>>();

        group.MapGet("/llm/log/{requestId:guid}", GetLlmLogEntry)
            .WithName("GetLlmLogEntry")
            .Produces<List<LlmLogEntryDto>>()
            .Produces(404);

        return group;
    }

    private static async Task<IResult> GetRecentRuns(
        IPipelineEventService eventService,
        int? count,
        string? status,
        CancellationToken ct)
    {
        IReadOnlyList<PipelineRun> runs;

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<PipelineRunStatus>(status, true, out var parsedStatus))
        {
            runs = await eventService.GetRunsByStatusAsync(Guid.Empty, parsedStatus, ct);
        }
        else
        {
            runs = await eventService.GetRecentRunsAsync(Guid.Empty, count ?? 20, ct);
        }

        var dtos = runs.Select(r => new PipelineRunSummaryDto
        {
            Id = r.Id,
            SessionId = r.SessionId,
            OriginalInput = r.OriginalInput,
            Status = r.Status.ToString(),
            CurrentStage = r.CurrentStage,
            ExtractedUrl = r.ExtractedUrl,
            CreatedWatchId = r.CreatedWatchId,
            ErrorMessage = r.ErrorMessage,
            StartedAt = r.StartedAt,
            CompletedAt = r.CompletedAt,
            DurationMs = r.DurationMs,
            LlmCallCount = r.LlmCallCount,
            TotalInputTokens = r.TotalInputTokens,
            TotalOutputTokens = r.TotalOutputTokens,
            RecoveryAttempts = r.RecoveryAttempts
        }).ToList();

        return Results.Ok(dtos);
    }

    private static async Task<IResult> GetRunDetail(
        Guid runId,
        IPipelineEventService eventService,
        CancellationToken ct)
    {
        var run = await eventService.GetRunByIdAsync(runId, ct);
        if (run == null)
            return Results.NotFound(new { error = "Pipeline run not found", runId });

        var events = await eventService.GetEventsForRunAsync(runId, ct);

        return Results.Ok(new PipelineRunDetailDto
        {
            Run = MapRunSummary(run),
            Events = events.Select(MapEvent).ToList(),
            FinalConfigurationJson = run.FinalConfigurationJson,
            SessionStateJson = run.SessionStateJson
        });
    }

    private static async Task<IResult> GetRunEvents(
        Guid runId,
        IPipelineEventService eventService,
        CancellationToken ct)
    {
        var events = await eventService.GetEventsForRunAsync(runId, ct);
        return Results.Ok(events.Select(MapEvent).ToList());
    }

    private static async Task<IResult> GetRunBySession(
        Guid sessionId,
        IPipelineEventService eventService,
        CancellationToken ct)
    {
        var run = await eventService.GetRunBySessionIdAsync(sessionId, ct);
        if (run == null)
            return Results.NotFound(new { error = "No pipeline run found for session", sessionId });

        var events = await eventService.GetEventsForRunAsync(run.Id, ct);

        return Results.Ok(new PipelineRunDetailDto
        {
            Run = MapRunSummary(run),
            Events = events.Select(MapEvent).ToList(),
            FinalConfigurationJson = run.FinalConfigurationJson,
            SessionStateJson = run.SessionStateJson
        });
    }

    private static Task<IResult> GetActiveSessions(
        ISessionPersistenceService sessionPersistence,
        CancellationToken ct)
    {
        var pipelineSessionCount = SetupConversationHub.PipelineSessionCount;
        var stateHistoryCount = SetupConversationHub.StateHistoryCount;

        return Task.FromResult(Results.Ok(new ActiveSessionsDto
        {
            InMemoryPipelineSessions = pipelineSessionCount,
            InMemoryStateHistories = stateHistoryCount
        }));
    }

    private static Task<IResult> GetLlmLog(
        ILlmLogService llmLog,
        int? count,
        string? provider)
    {
        var entries = string.IsNullOrEmpty(provider)
            ? llmLog.GetRecentLogs(count ?? 50)
            : llmLog.GetLogsForProvider(provider, count ?? 50);

        var dtos = entries.Select(e => new LlmLogEntryDto
        {
            Id = e.Id,
            RequestId = e.RequestId,
            Timestamp = e.Timestamp,
            Level = e.Level.ToString(),
            ProviderName = e.ProviderName,
            Model = e.Model,
            Category = e.Category.ToString(),
            Message = e.Message,
            PromptPreview = e.PromptPreview,
            FullPrompt = e.FullPrompt,
            ResponsePreview = e.ResponsePreview,
            FullResponse = e.FullResponse,
            DurationMs = e.DurationMs,
            InputTokens = e.InputTokens,
            OutputTokens = e.OutputTokens,
            IsSuccess = e.IsSuccess,
            ErrorMessage = e.ErrorMessage,
            ExceptionType = e.ExceptionType,
            StackTrace = e.StackTrace,
            Metadata = e.Metadata
        }).ToList();

        return Task.FromResult(Results.Ok(dtos));
    }

    private static Task<IResult> GetLlmLogEntry(
        Guid requestId,
        ILlmLogService llmLog)
    {
        var entries = llmLog.GetRecentLogs(500);
        var matching = entries.Where(e => e.RequestId == requestId).ToList();

        if (matching.Count == 0)
            return Task.FromResult(Results.NotFound(new { error = "LLM log entry not found", requestId }));

        var detail = matching.Select(e => new LlmLogEntryDto
        {
            Id = e.Id,
            RequestId = e.RequestId,
            Timestamp = e.Timestamp,
            Level = e.Level.ToString(),
            ProviderName = e.ProviderName,
            Model = e.Model,
            Category = e.Category.ToString(),
            Message = e.Message,
            PromptPreview = e.PromptPreview,
            FullPrompt = e.FullPrompt,
            ResponsePreview = e.ResponsePreview,
            FullResponse = e.FullResponse,
            DurationMs = e.DurationMs,
            InputTokens = e.InputTokens,
            OutputTokens = e.OutputTokens,
            IsSuccess = e.IsSuccess,
            ErrorMessage = e.ErrorMessage,
            ExceptionType = e.ExceptionType,
            StackTrace = e.StackTrace,
            Metadata = e.Metadata
        }).ToList();

        return Task.FromResult(Results.Ok(detail));
    }

    // --- Mapping helpers ---

    private static PipelineRunSummaryDto MapRunSummary(PipelineRun run) => new()
    {
        Id = run.Id,
        SessionId = run.SessionId,
        OriginalInput = run.OriginalInput,
        Status = run.Status.ToString(),
        CurrentStage = run.CurrentStage,
        ExtractedUrl = run.ExtractedUrl,
        CreatedWatchId = run.CreatedWatchId,
        ErrorMessage = run.ErrorMessage,
        StartedAt = run.StartedAt,
        CompletedAt = run.CompletedAt,
        DurationMs = run.DurationMs,
        LlmCallCount = run.LlmCallCount,
        TotalInputTokens = run.TotalInputTokens,
        TotalOutputTokens = run.TotalOutputTokens,
        RecoveryAttempts = run.RecoveryAttempts
    };

    private static PipelineEventDto MapEvent(PipelineEvent evt) => new()
    {
        Id = evt.Id,
        Stage = evt.Stage,
        EventType = evt.EventType,
        SequenceNumber = evt.SequenceNumber,
        Summary = evt.Summary,
        Details = evt.Details,
        DataJson = evt.DataJson,
        IsSuccess = evt.IsSuccess,
        ErrorMessage = evt.ErrorMessage,
        StackTrace = evt.StackTrace,
        Timestamp = evt.Timestamp,
        DurationMs = evt.DurationMs,
        LlmProvider = evt.LlmProvider,
        LlmModel = evt.LlmModel,
        InputTokens = evt.InputTokens,
        OutputTokens = evt.OutputTokens,
        Confidence = evt.Confidence,
        PromptText = evt.PromptText,
        ResponseText = evt.ResponseText
    };
}

// --- DTOs ---

public record PipelineRunSummaryDto
{
    public Guid Id { get; init; }
    public Guid SessionId { get; init; }
    public string OriginalInput { get; init; } = "";
    public string Status { get; init; } = "";
    public string? CurrentStage { get; init; }
    public string? ExtractedUrl { get; init; }
    public Guid? CreatedWatchId { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public long? DurationMs { get; init; }
    public int LlmCallCount { get; init; }
    public int TotalInputTokens { get; init; }
    public int TotalOutputTokens { get; init; }
    public int RecoveryAttempts { get; init; }
}

public record PipelineRunDetailDto
{
    public required PipelineRunSummaryDto Run { get; init; }
    public required List<PipelineEventDto> Events { get; init; }
    public string? FinalConfigurationJson { get; init; }
    public string? SessionStateJson { get; init; }
}

public record PipelineEventDto
{
    public Guid Id { get; init; }
    public string Stage { get; init; } = "";
    public string EventType { get; init; } = "";
    public int SequenceNumber { get; init; }
    public string? Summary { get; init; }
    public string? Details { get; init; }
    public string? DataJson { get; init; }
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public string? StackTrace { get; init; }
    public DateTime Timestamp { get; init; }
    public long? DurationMs { get; init; }
    public string? LlmProvider { get; init; }
    public string? LlmModel { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public float? Confidence { get; init; }
    public string? PromptText { get; init; }
    public string? ResponseText { get; init; }
}

public record ActiveSessionsDto
{
    public int InMemoryPipelineSessions { get; init; }
    public int InMemoryStateHistories { get; init; }
}


