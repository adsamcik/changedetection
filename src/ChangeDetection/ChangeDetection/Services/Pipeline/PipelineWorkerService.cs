using System.Runtime.CompilerServices;
using System.Threading.Channels;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Hubs;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Shared.Dtos;
using Microsoft.AspNetCore.SignalR;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Background service that processes pipeline queue items.
/// Spawns configurable number of workers that pick up items from the persistent queue.
/// </summary>
public sealed class PipelineWorkerService : BackgroundService
{
    private readonly IBackgroundServiceScopeFactory _scopeFactory;
    private readonly PipelineQueueService _queueService;
    private readonly IPipelineQueueRepository _queueRepository;
    private readonly IHubContext<SetupConversationHub> _hubContext;
    private readonly ILogger<PipelineWorkerService> _logger;
    
    /// <summary>
    /// Time to consider a processing item as stale (crashed worker).
    /// </summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// Maximum number of retry attempts for transient failures.
    /// </summary>
    private const int MaxRetryAttempts = 3;
    
    /// <summary>
    /// Interval for checking stale items.
    /// </summary>
    private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// Default worker count if settings not available.
    /// </summary>
    private const int DefaultWorkerCount = 1;

    public PipelineWorkerService(
        IBackgroundServiceScopeFactory scopeFactory,
        PipelineQueueService queueService,
        IPipelineQueueRepository queueRepository,
        IHubContext<SetupConversationHub> hubContext,
        ILogger<PipelineWorkerService> logger)
    {
        _scopeFactory = scopeFactory;
        _queueService = queueService;
        _queueRepository = queueRepository;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Pipeline worker service starting...");
        
        // Recover any stale items from previous run
        await RecoverStaleItemsAsync(stoppingToken);
        
        // Load worker count from settings
        var workerCount = await GetWorkerCountAsync(stoppingToken);
        _logger.LogInformation("Starting {Count} pipeline workers", workerCount);
        
        // Signal any pending items from previous run
        var pendingCount = await _queueRepository.GetCountByStatusAsync(PipelineQueueStatus.Pending, stoppingToken);
        if (pendingCount > 0)
        {
            _logger.LogInformation("Found {Count} pending pipeline items from previous run", pendingCount);
            for (var i = 0; i < pendingCount; i++)
            {
                _queueService.SignalItemAvailable();
            }
        }
        
        // Start workers and stale item checker
        var tasks = new List<Task>
        {
            CheckStaleItemsAsync(stoppingToken)
        };
        
        for (var i = 0; i < workerCount; i++)
        {
            var workerId = i;
            tasks.Add(RunWorkerAsync(workerId, stoppingToken));
        }
        
        await Task.WhenAll(tasks);
        
        _logger.LogInformation("Pipeline worker service stopped");
    }

    private async Task<int> GetWorkerCountAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateBackgroundScope();
            var settingsRepo = scope.ServiceProvider.GetRequiredService<IRepository<AppSettings>>();
            var allSettings = await settingsRepo.GetAllAsync(ct);
            var settings = allSettings.FirstOrDefault();
            
            var count = settings?.MaxConcurrentPipelines ?? DefaultWorkerCount;
            return Math.Max(1, Math.Min(count, 10)); // Clamp to 1-10
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load worker count from settings, using default: {Default}", DefaultWorkerCount);
            return DefaultWorkerCount;
        }
    }

    private async Task RecoverStaleItemsAsync(CancellationToken ct)
    {
        try
        {
            var resetCount = await _queueRepository.ResetStaleItemsAsync(StaleThreshold, ct);
            if (resetCount > 0)
            {
                _logger.LogInformation("Recovered {Count} stale pipeline items from previous run", resetCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover stale items");
        }
    }

    private async Task CheckStaleItemsAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(StaleCheckInterval);
        
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                var resetCount = await _queueRepository.ResetStaleItemsAsync(StaleThreshold, ct);
                if (resetCount > 0)
                {
                    _logger.LogWarning("Reset {Count} stale pipeline items", resetCount);
                    for (var i = 0; i < resetCount; i++)
                    {
                        _queueService.SignalItemAvailable();
                    }
                }
                
                // Also purge old completed/failed items (older than 7 days)
                await _queueRepository.PurgeOldItemsAsync(TimeSpan.FromDays(7), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking stale items");
            }
        }
    }

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        _logger.LogDebug("Worker {WorkerId} starting", workerId);
        
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for signal that work might be available
                if (!await _queueService.WaitForItemAsync(ct))
                    break;
                
                // Try to claim an item
                var item = await _queueService.ClaimNextAsync(ct);
                if (item == null)
                    continue; // Another worker got it first
                
                _logger.LogInformation(
                    "Worker {WorkerId} processing item {ItemId} (type: {Type}, session: {SessionId})",
                    workerId, item.Id, item.OperationType, item.SessionId);
                
                try
                {
                    await ProcessItemAsync(workerId, item, ct);
                    await _queueService.CompleteAsync(item.Id, ct);
                    
                    _logger.LogInformation(
                        "Worker {WorkerId} completed item {ItemId}",
                        workerId, item.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Server shutting down - item will be recovered on next startup
                    // Release the slot so ProcessingCount stays accurate
                    _queueService.ReleaseSlot();
                    _logger.LogWarning(
                        "Worker {WorkerId} interrupted while processing {ItemId}, will recover on restart",
                        workerId, item.Id);
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Worker {WorkerId} failed to process item {ItemId} (attempt {Attempt})",
                        workerId, item.Id, item.Attempts);
                    
                    // Check if we should retry
                    if (item.Attempts < MaxRetryAttempts && IsTransientError(ex))
                    {
                        _logger.LogInformation(
                            "Resetting item {ItemId} for retry (attempt {Attempt}/{Max})",
                            item.Id, item.Attempts, MaxRetryAttempts);
                        
                        await _queueService.ResetForRetryAsync(item.Id, ct);
                        
                        // Notify client of retry
                        await NotifyClientAsync(item.SessionId, item.OwnerId, new FlowStateEntryDto
                        {
                            Stage = "Recovery",
                            Status = FlowStateStatusDto.Recovery,
                            Summary = $"Retrying after error: {ex.Message}",
                            Timestamp = DateTimeOffset.UtcNow,
                            IsCurrentState = true
                        }, ct);
                    }
                    else
                    {
                        // Max retries exhausted - move to dead letter queue
                        // Capture full exception details for debugging
                        var reason = BuildDeadLetterReason(item, ex);
                        
                        await _queueService.MoveToDeadLetterAsync(item.Id, reason, ct);
                        
                        _logger.LogWarning(
                            "Moved item {ItemId} to dead letter queue after {Attempts} attempts",
                            item.Id, item.Attempts);
                        
                        // Notify client of dead letter
                        await NotifyClientAsync(item.SessionId, item.OwnerId, new FlowStateEntryDto
                        {
                            Stage = "DeadLetter",
                            Status = FlowStateStatusDto.Failed,
                            Summary = $"Pipeline moved to dead letter queue: {ex.Message}",
                            Timestamp = DateTimeOffset.UtcNow,
                            IsCurrentState = true
                        }, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerId} encountered unexpected error", workerId);
                await Task.Delay(TimeSpan.FromSeconds(5), ct); // Brief delay before retry
            }
        }
        
        _logger.LogDebug("Worker {WorkerId} stopped", workerId);
    }

    private async Task ProcessItemAsync(int workerId, PipelineQueueItem item, CancellationToken ct)
    {
        // Create a scope with background context for this work item
        using var scope = _scopeFactory.CreateBackgroundScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IWatchSetupPipeline>();
        
        // Set up user context override for this specific user
        // Note: Background scope uses BackgroundServiceUserContext by default,
        // but we want to process as the original user
        // For now, we process with the pipeline's default behavior
        
        switch (item.OperationType)
        {
            case PipelineOperationType.Process:
                await ProcessNewPipelineAsync(workerId, item, pipeline, ct);
                break;
                
            case PipelineOperationType.ContinueWithFeedback:
                await ProcessContinueAsync(workerId, item, pipeline, ct);
                break;
                
            case PipelineOperationType.RecoverFromFailure:
                await ProcessRecoveryAsync(workerId, item, pipeline, ct);
                break;
                
            default:
                throw new InvalidOperationException($"Unknown operation type: {item.OperationType}");
        }
    }

    private async Task ProcessNewPipelineAsync(
        int workerId,
        PipelineQueueItem item,
        IWatchSetupPipeline pipeline,
        CancellationToken ct)
    {
        var options = PipelineQueueService.DeserializeOptions(item.OptionsJson);
        
        await foreach (var progress in pipeline.ProcessStreamingAsync(item.UserInput!, options, ct))
        {
            await NotifyProgressAsync(item.SessionId, item.OwnerId, progress, ct);
        }
    }

    private async Task ProcessContinueAsync(
        int workerId,
        PipelineQueueItem item,
        IWatchSetupPipeline pipeline,
        CancellationToken ct)
    {
        var session = PipelineQueueService.DeserializeSession(item.SessionJson);
        if (session == null)
        {
            throw new InvalidOperationException("Session JSON is null for continue operation");
        }
        
        await foreach (var progress in pipeline.ContinueWithFeedbackStreamingAsync(session, item.Feedback!, ct))
        {
            await NotifyProgressAsync(item.SessionId, item.OwnerId, progress, ct);
        }
    }

    private async Task ProcessRecoveryAsync(
        int workerId,
        PipelineQueueItem item,
        IWatchSetupPipeline pipeline,
        CancellationToken ct)
    {
        var session = PipelineQueueService.DeserializeSession(item.SessionJson);
        var failedResult = PipelineQueueService.DeserializeResult(item.FailedResultJson);
        var options = PipelineQueueService.DeserializeOptions(item.OptionsJson);
        
        if (session == null || failedResult == null || options == null)
        {
            throw new InvalidOperationException("Missing required data for recovery operation");
        }
        
        var result = await pipeline.RecoverFromFailureAsync(session, failedResult, options, ct);
        
        // Convert result to progress and notify
        var progress = new PipelineProgress
        {
            Stage = result.CurrentStage,
            Type = result.IsSuccess ? ProgressType.Completed : ProgressType.Failed,
            Summary = result.Summary ?? "Recovery completed",
            Result = result,
            Session = result.Session
        };
        
        await NotifyProgressAsync(item.SessionId, item.OwnerId, progress, ct);
    }

    private async Task NotifyProgressAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineProgress progress,
        CancellationToken ct)
    {
        var entry = MapProgressToFlowState(progress);
        await NotifyClientAsync(sessionId, ownerId, entry, ct);
    }

    private async Task NotifyClientAsync(
        Guid sessionId,
        Guid ownerId,
        FlowStateEntryDto entry,
        CancellationToken ct)
    {
        try
        {
            // Send to the specific session group
            var groupName = $"setup-{sessionId}";
            await _hubContext.Clients.Group(groupName).SendAsync("FlowStateUpdate", entry, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify client for session {SessionId}", sessionId);
        }
    }

    private static FlowStateEntryDto MapProgressToFlowState(PipelineProgress progress)
    {
        return new FlowStateEntryDto
        {
            Stage = progress.Stage.ToString(),
            Status = progress.Type switch
            {
                ProgressType.Starting => FlowStateStatusDto.InProgress,
                ProgressType.InProgress => FlowStateStatusDto.InProgress,
                ProgressType.Thinking => FlowStateStatusDto.Thinking,
                ProgressType.StageCompleted => FlowStateStatusDto.Completed,
                ProgressType.Completed => FlowStateStatusDto.Completed,
                ProgressType.NeedsInput => FlowStateStatusDto.Question,
                ProgressType.Failed => FlowStateStatusDto.Failed,
                _ => FlowStateStatusDto.InProgress
            },
            Summary = progress.Summary,
            Details = progress.Details,
            Timestamp = progress.Timestamp,
            IsCurrentState = true
        };
    }

    /// <summary>
    /// Determines if an exception is transient and the operation should be retried.
    /// </summary>
    private static bool IsTransientError(Exception ex)
    {
        // HTTP transient errors
        if (ex is HttpRequestException)
            return true;
        
        // Task cancelled (not by our token) could be transient
        if (ex is TaskCanceledException tce && tce.CancellationToken == default)
            return true;
        
        // Timeout errors
        if (ex is TimeoutException)
            return true;
        
        // LLM rate limiting or temporary failures
        if (ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("429", StringComparison.Ordinal) ||
            ex.Message.Contains("503", StringComparison.Ordinal) ||
            ex.Message.Contains("temporarily", StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Check inner exceptions
        if (ex.InnerException != null && IsTransientError(ex.InnerException))
            return true;
        
        return false;
    }
    
    /// <summary>
    /// Builds a detailed reason string for dead letter queue, including full exception details.
    /// </summary>
    private static string BuildDeadLetterReason(PipelineQueueItem item, Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        
        if (item.Attempts >= MaxRetryAttempts)
        {
            sb.AppendLine($"Max retries ({MaxRetryAttempts}) exhausted.");
        }
        else
        {
            sb.AppendLine("Non-transient error (no retry).");
        }
        
        sb.AppendLine($"Attempts: {item.Attempts}");
        sb.AppendLine($"Error Type: {ex.GetType().FullName}");
        sb.AppendLine($"Message: {ex.Message}");
        
        if (ex.StackTrace != null)
        {
            sb.AppendLine("Stack Trace:");
            sb.AppendLine(ex.StackTrace);
        }
        
        if (ex.InnerException != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Inner Exception: {ex.InnerException.GetType().FullName}");
            sb.AppendLine($"Inner Message: {ex.InnerException.Message}");
            if (ex.InnerException.StackTrace != null)
            {
                sb.AppendLine("Inner Stack Trace:");
                sb.AppendLine(ex.InnerException.StackTrace);
            }
        }
        
        return sb.ToString();
    }
}
