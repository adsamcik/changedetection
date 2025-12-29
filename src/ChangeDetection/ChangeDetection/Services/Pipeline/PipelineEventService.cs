using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Persistence;
using LiteDB;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// LiteDB implementation of pipeline event tracking.
/// Persists all pipeline execution history for debugging and analytics.
/// </summary>
public class PipelineEventService(LiteDbContext context, ILogger<PipelineEventService> logger) : IPipelineEventService
{
    private readonly ILiteCollection<PipelineRun> _runs = context.Database.GetCollection<PipelineRun>("pipeline_runs");
    private readonly ILiteCollection<PipelineEvent> _events = context.Database.GetCollection<PipelineEvent>("pipeline_events");
    
    private readonly object _sequenceLock = new();
    private readonly Dictionary<Guid, int> _sequenceCounters = new();

    /// <inheritdoc />
    public async Task<PipelineRun> StartRunAsync(
        Guid sessionId, 
        string originalInput, 
        Guid ownerId, 
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var run = new PipelineRun
        {
            SessionId = sessionId,
            OriginalInput = originalInput,
            OwnerId = ownerId,
            Status = PipelineRunStatus.Started,
            StartedAt = DateTime.UtcNow
        };
        
        _runs.Insert(run);
        
        // Initialize sequence counter
        lock (_sequenceLock)
        {
            _sequenceCounters[run.Id] = 0;
        }
        
        logger.LogDebug("Started pipeline run {RunId} for session {SessionId}", run.Id, sessionId);
        
        return await Task.FromResult(run);
    }

    /// <inheritdoc />
    public async Task<PipelineEvent> RecordEventAsync(
        Guid runId,
        string stage,
        string eventType,
        string? summary = null,
        string? details = null,
        string? dataJson = null,
        bool isSuccess = true,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var sequenceNumber = GetNextSequenceNumber(runId);
        
        var ev = new PipelineEvent
        {
            PipelineRunId = runId,
            Stage = stage,
            EventType = eventType,
            SequenceNumber = sequenceNumber,
            Summary = summary,
            Details = details,
            DataJson = dataJson,
            IsSuccess = isSuccess,
            Timestamp = DateTime.UtcNow
        };
        
        _events.Insert(ev);
        
        logger.LogDebug("Recorded pipeline event {EventType} for run {RunId} at stage {Stage}", 
            eventType, runId, stage);
        
        return await Task.FromResult(ev);
    }

    /// <inheritdoc />
    public async Task<PipelineEvent> RecordLlmCallAsync(
        Guid runId,
        string stage,
        string provider,
        string model,
        int inputTokens,
        int outputTokens,
        long durationMs,
        bool isSuccess = true,
        string? errorMessage = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var sequenceNumber = GetNextSequenceNumber(runId);
        
        var ev = new PipelineEvent
        {
            PipelineRunId = runId,
            Stage = stage,
            EventType = PipelineEventTypes.LlmCall,
            SequenceNumber = sequenceNumber,
            Summary = $"LLM call to {provider}/{model}",
            LlmProvider = provider,
            LlmModel = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            DurationMs = durationMs,
            IsSuccess = isSuccess,
            ErrorMessage = errorMessage,
            Timestamp = DateTime.UtcNow
        };
        
        _events.Insert(ev);
        
        // Update run totals
        var run = _runs.FindById(runId);
        if (run != null)
        {
            run.LlmCallCount++;
            run.TotalInputTokens += inputTokens;
            run.TotalOutputTokens += outputTokens;
            _runs.Update(run);
        }
        
        logger.LogDebug("Recorded LLM call for run {RunId}: {Provider}/{Model}, {InputTokens}+{OutputTokens} tokens", 
            runId, provider, model, inputTokens, outputTokens);
        
        return await Task.FromResult(ev);
    }

    /// <inheritdoc />
    public async Task<PipelineEvent> RecordFailureAsync(
        Guid runId,
        string stage,
        string errorMessage,
        string? stackTrace = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var sequenceNumber = GetNextSequenceNumber(runId);
        
        var ev = new PipelineEvent
        {
            PipelineRunId = runId,
            Stage = stage,
            EventType = PipelineEventTypes.StageFailed,
            SequenceNumber = sequenceNumber,
            Summary = $"Stage {stage} failed",
            ErrorMessage = errorMessage,
            StackTrace = stackTrace,
            IsSuccess = false,
            Timestamp = DateTime.UtcNow
        };
        
        _events.Insert(ev);
        
        logger.LogWarning("Recorded failure for run {RunId} at stage {Stage}: {Error}", 
            runId, stage, errorMessage);
        
        return await Task.FromResult(ev);
    }

    /// <inheritdoc />
    public async Task<PipelineEvent> RecordUserInteractionAsync(
        Guid runId,
        string eventType,
        string? userInput,
        string? summary = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var sequenceNumber = GetNextSequenceNumber(runId);
        
        var ev = new PipelineEvent
        {
            PipelineRunId = runId,
            Stage = PipelineStageNames.Confirmation,
            EventType = eventType,
            SequenceNumber = sequenceNumber,
            Summary = summary,
            Details = userInput,
            Timestamp = DateTime.UtcNow
        };
        
        _events.Insert(ev);
        
        // Update run interaction count
        var run = _runs.FindById(runId);
        if (run != null)
        {
            run.UserInteractionCount++;
            _runs.Update(run);
        }
        
        logger.LogDebug("Recorded user interaction for run {RunId}: {EventType}", runId, eventType);
        
        return await Task.FromResult(ev);
    }

    /// <inheritdoc />
    public Task UpdateRunStatusAsync(
        Guid runId,
        PipelineRunStatus status,
        string? currentStage = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var run = _runs.FindById(runId);
        if (run != null)
        {
            run.Status = status;
            if (currentStage != null)
            {
                run.CurrentStage = currentStage;
            }
            _runs.Update(run);
            
            logger.LogDebug("Updated run {RunId} status to {Status}", runId, status);
        }
        
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task CompleteRunAsync(
        Guid runId,
        Guid watchId,
        string? finalConfigurationJson = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var run = _runs.FindById(runId);
        if (run != null)
        {
            run.Status = PipelineRunStatus.Completed;
            run.CreatedWatchId = watchId;
            run.FinalConfigurationJson = finalConfigurationJson;
            run.CompletedAt = DateTime.UtcNow;
            run.DurationMs = (long)(run.CompletedAt.Value - run.StartedAt).TotalMilliseconds;
            _runs.Update(run);
            
            // Record completion event
            await RecordEventAsync(
                runId,
                PipelineStageNames.Configuration,
                PipelineEventTypes.WatchCreated,
                $"Watch {watchId} created successfully",
                ct: ct);
            
            logger.LogInformation("Completed pipeline run {RunId}, created watch {WatchId} in {DurationMs}ms", 
                runId, watchId, run.DurationMs);
        }
        
        // Clean up sequence counter
        lock (_sequenceLock)
        {
            _sequenceCounters.Remove(runId);
        }
    }

    /// <inheritdoc />
    public async Task FailRunAsync(
        Guid runId,
        string errorMessage,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var run = _runs.FindById(runId);
        if (run != null)
        {
            run.Status = PipelineRunStatus.Failed;
            run.ErrorMessage = errorMessage;
            run.CompletedAt = DateTime.UtcNow;
            run.DurationMs = (long)(run.CompletedAt.Value - run.StartedAt).TotalMilliseconds;
            _runs.Update(run);
            
            logger.LogWarning("Failed pipeline run {RunId}: {Error}", runId, errorMessage);
        }
        
        // Clean up sequence counter
        lock (_sequenceLock)
        {
            _sequenceCounters.Remove(runId);
        }
        
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelRunAsync(Guid runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var run = _runs.FindById(runId);
        if (run != null)
        {
            run.Status = PipelineRunStatus.Cancelled;
            run.CompletedAt = DateTime.UtcNow;
            run.DurationMs = (long)(run.CompletedAt.Value - run.StartedAt).TotalMilliseconds;
            _runs.Update(run);
            
            logger.LogInformation("Cancelled pipeline run {RunId}", runId);
        }
        
        // Clean up sequence counter
        lock (_sequenceLock)
        {
            _sequenceCounters.Remove(runId);
        }
        
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateExtractedUrlAsync(
        Guid runId,
        string url,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var run = _runs.FindById(runId);
        if (run != null)
        {
            run.ExtractedUrl = url;
            _runs.Update(run);
        }
        
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateLlmUsageAsync(
        Guid runId,
        int inputTokens,
        int outputTokens,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var run = _runs.FindById(runId);
        if (run != null)
        {
            run.TotalInputTokens += inputTokens;
            run.TotalOutputTokens += outputTokens;
            _runs.Update(run);
        }
        
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PipelineRun?> GetRunByIdAsync(Guid runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<PipelineRun?>(_runs.FindById(runId));
    }

    /// <inheritdoc />
    public Task<PipelineRun?> GetRunBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var run = _runs.FindOne(r => r.SessionId == sessionId);
        return Task.FromResult<PipelineRun?>(run);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineEvent>> GetEventsForRunAsync(
        Guid runId, 
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var events = _events
            .Query()
            .Where(e => e.PipelineRunId == runId)
            .OrderBy(e => e.SequenceNumber)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PipelineEvent>>(events);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineRun>> GetRecentRunsAsync(
        Guid ownerId,
        int count = 10,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var runs = _runs
            .Query()
            .Where(r => r.OwnerId == ownerId)
            .OrderByDescending(r => r.StartedAt)
            .Limit(count)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PipelineRun>>(runs);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineRun>> GetRunsByStatusAsync(
        Guid ownerId,
        PipelineRunStatus status,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var runs = _runs
            .Query()
            .Where(r => r.OwnerId == ownerId && r.Status == status)
            .OrderByDescending(r => r.StartedAt)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PipelineRun>>(runs);
    }

    /// <inheritdoc />
    public Task<PipelineRunStatistics> GetStatisticsAsync(
        Guid ownerId,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var runs = _runs
            .Query()
            .Where(r => r.OwnerId == ownerId && r.StartedAt >= from && r.StartedAt <= to)
            .ToList();
        
        var stats = new PipelineRunStatistics
        {
            TotalRuns = runs.Count,
            SuccessfulRuns = runs.Count(r => r.Status == PipelineRunStatus.Completed),
            FailedRuns = runs.Count(r => r.Status == PipelineRunStatus.Failed),
            CancelledRuns = runs.Count(r => r.Status == PipelineRunStatus.Cancelled),
            TotalLlmCalls = runs.Sum(r => r.LlmCallCount),
            TotalInputTokens = runs.Sum(r => r.TotalInputTokens),
            TotalOutputTokens = runs.Sum(r => r.TotalOutputTokens),
            AverageRecoveryAttempts = runs.Count > 0 ? runs.Average(r => r.RecoveryAttempts) : 0
        };
        
        var completedRuns = runs.Where(r => r.DurationMs.HasValue).ToList();
        stats.AverageDurationMs = completedRuns.Count > 0 
            ? completedRuns.Average(r => r.DurationMs!.Value) 
            : 0;
        
        return Task.FromResult(stats);
    }

    /// <inheritdoc />
    public Task<int> CleanupOldRunsAsync(
        TimeSpan olderThan,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var cutoff = DateTime.UtcNow - olderThan;
        
        // Get run IDs to delete
        var runIds = _runs
            .Query()
            .Where(r => r.StartedAt < cutoff)
            .Select(r => r.Id)
            .ToList();
        
        // Delete events for these runs
        foreach (var runId in runIds)
        {
            _events.DeleteMany(e => e.PipelineRunId == runId);
        }
        
        // Delete runs
        var deleted = _runs.DeleteMany(r => r.StartedAt < cutoff);
        
        logger.LogInformation("Cleaned up {Count} old pipeline runs older than {CutoffDate}", 
            deleted, cutoff);
        
        return Task.FromResult(deleted);
    }

    private int GetNextSequenceNumber(Guid runId)
    {
        lock (_sequenceLock)
        {
            if (!_sequenceCounters.TryGetValue(runId, out var current))
            {
                // Load from database if not in memory
                var maxSeq = _events
                    .Query()
                    .Where(e => e.PipelineRunId == runId)
                    .OrderByDescending(e => e.SequenceNumber)
                    .Limit(1)
                    .FirstOrDefault()?.SequenceNumber ?? 0;
                
                current = maxSeq;
                _sequenceCounters[runId] = current;
            }
            
            _sequenceCounters[runId] = current + 1;
            return current + 1;
        }
    }
}
