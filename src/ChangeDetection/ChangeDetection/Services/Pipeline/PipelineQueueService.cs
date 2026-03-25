using System.Text.Json;
using System.Threading.Channels;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Service for managing the persistent pipeline queue.
/// Combines database persistence with in-memory signaling for efficient worker notification.
/// </summary>
public sealed class PipelineQueueService : IPipelineQueueService, IDisposable
{
    private readonly IPipelineQueueRepository _repository;
    private readonly ILogger<PipelineQueueService> _logger;
    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    
    // Channel for signaling workers that items are available
    // Uses unbounded since we just signal availability, not actual items
    private readonly Channel<bool> _signalChannel;
    
    private int _processingCount;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public PipelineQueueService(
        IPipelineQueueRepository repository,
        ILogger<PipelineQueueService> logger,
        IOptionsMonitor<AppSettings> settings)
    {
        _repository = repository;
        _logger = logger;
        _settingsMonitor = settings;
        
        _signalChannel = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    /// <inheritdoc />
    public int QueueDepth
    {
        get
        {
            // Synchronous approximation for interface compatibility.
            // Prefer GetQueueDepthAsync for accurate counts.
            try
            {
                return Task.Run(() => _repository.GetCountByStatusAsync(PipelineQueueStatus.Pending))
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get queue depth synchronously");
                return 0;
            }
        }
    }
    
    /// <summary>
    /// Gets the queue depth asynchronously (preferred over QueueDepth property).
    /// </summary>
    public Task<int> GetQueueDepthAsync(CancellationToken ct = default)
        => _repository.GetCountByStatusAsync(PipelineQueueStatus.Pending, ct);

    /// <inheritdoc />
    public int ProcessingCount => _processingCount;

    /// <inheritdoc />
    public async Task<Guid> EnqueueProcessAsync(
        Guid sessionId,
        Guid ownerId,
        string userInput,
        PipelineOptions? options = null,
        int priority = 0,
        CancellationToken ct = default)
    {
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.Process,
            UserInput = userInput,
            OptionsJson = options != null ? JsonSerializer.Serialize(options, JsonOptions) : null,
            Priority = priority
        };

        await _repository.EnqueueAsync(item, ct);
        SignalItemAvailable();
        
        _logger.LogInformation(
            "Enqueued pipeline process request {ItemId} for session {SessionId}", 
            item.Id, sessionId);
        
        return item.Id;
    }

    /// <inheritdoc />
    public async Task<EnqueueResult> TryEnqueueProcessAsync(
        Guid sessionId,
        Guid ownerId,
        string userInput,
        PipelineOptions? options = null,
        int priority = 0,
        CancellationToken ct = default)
    {
        var settings = _settingsMonitor.CurrentValue;
        
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.Process,
            UserInput = userInput,
            OptionsJson = options != null ? JsonSerializer.Serialize(options, JsonOptions) : null,
            Priority = priority
        };
        
        // Use atomic check-and-enqueue to prevent TOCTOU race
        var (success, enqueued, rateLimitResult) = await _repository.TryEnqueueWithLimitAsync(
            item,
            settings.MaxPendingItemsPerUser,
            settings.MaxConcurrentItemsPerUser,
            ct);
        
        if (!success)
        {
            _logger.LogWarning(
                "Rate limit exceeded for user {OwnerId}: {Reason}", 
                ownerId, rateLimitResult?.Reason);
            return EnqueueResult.RateLimited(rateLimitResult!);
        }
        
        SignalItemAvailable();
        
        _logger.LogInformation(
            "Enqueued pipeline process request {ItemId} for session {SessionId}", 
            enqueued!.Id, sessionId);
        
        return EnqueueResult.Succeeded(enqueued.Id);
    }

    /// <inheritdoc />
    public async Task<Guid> EnqueueContinueAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineSession session,
        string feedback,
        int priority = 0,
        CancellationToken ct = default)
    {
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.ContinueWithFeedback,
            Feedback = feedback,
            SessionJson = JsonSerializer.Serialize(session, JsonOptions),
            Priority = priority
        };

        await _repository.EnqueueAsync(item, ct);
        SignalItemAvailable();
        
        _logger.LogInformation(
            "Enqueued pipeline continue request {ItemId} for session {SessionId}", 
            item.Id, sessionId);
        
        return item.Id;
    }

    /// <inheritdoc />
    public async Task<EnqueueResult> TryEnqueueContinueAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineSession session,
        string feedback,
        int priority = 0,
        CancellationToken ct = default)
    {
        var settings = _settingsMonitor.CurrentValue;
        
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.ContinueWithFeedback,
            Feedback = feedback,
            SessionJson = JsonSerializer.Serialize(session, JsonOptions),
            Priority = priority
        };
        
        var (success, enqueued, rateLimitResult) = await _repository.TryEnqueueWithLimitAsync(
            item,
            settings.MaxPendingItemsPerUser,
            settings.MaxConcurrentItemsPerUser,
            ct);
        
        if (!success)
        {
            _logger.LogWarning(
                "Rate limit exceeded for user {OwnerId} (continue): {Reason}", 
                ownerId, rateLimitResult?.Reason);
            return EnqueueResult.RateLimited(rateLimitResult!);
        }
        
        SignalItemAvailable();
        
        _logger.LogInformation(
            "Enqueued pipeline continue request {ItemId} for session {SessionId}", 
            enqueued!.Id, sessionId);
        
        return EnqueueResult.Succeeded(enqueued.Id);
    }

    /// <inheritdoc />
    public async Task<Guid> EnqueueRecoveryAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineSession session,
        PipelineResult failedResult,
        PipelineOptions options,
        int priority = 0,
        CancellationToken ct = default)
    {
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.RecoverFromFailure,
            SessionJson = JsonSerializer.Serialize(session, JsonOptions),
            FailedResultJson = JsonSerializer.Serialize(failedResult, JsonOptions),
            OptionsJson = JsonSerializer.Serialize(options, JsonOptions),
            Priority = priority
        };

        await _repository.EnqueueAsync(item, ct);
        SignalItemAvailable();
        
        _logger.LogInformation(
            "Enqueued pipeline recovery request {ItemId} for session {SessionId}", 
            item.Id, sessionId);
        
        return item.Id;
    }

    /// <inheritdoc />
    public async Task<EnqueueResult> TryEnqueueRecoveryAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineSession session,
        PipelineResult failedResult,
        PipelineOptions options,
        int priority = 0,
        CancellationToken ct = default)
    {
        var settings = _settingsMonitor.CurrentValue;
        
        var item = new PipelineQueueItem
        {
            SessionId = sessionId,
            OwnerId = ownerId,
            OperationType = PipelineOperationType.RecoverFromFailure,
            SessionJson = JsonSerializer.Serialize(session, JsonOptions),
            FailedResultJson = JsonSerializer.Serialize(failedResult, JsonOptions),
            OptionsJson = JsonSerializer.Serialize(options, JsonOptions),
            Priority = priority
        };
        
        var (success, enqueued, rateLimitResult) = await _repository.TryEnqueueWithLimitAsync(
            item,
            settings.MaxPendingItemsPerUser,
            settings.MaxConcurrentItemsPerUser,
            ct);
        
        if (!success)
        {
            _logger.LogWarning(
                "Rate limit exceeded for user {OwnerId} (recovery): {Reason}", 
                ownerId, rateLimitResult?.Reason);
            return EnqueueResult.RateLimited(rateLimitResult!);
        }
        
        SignalItemAvailable();
        
        _logger.LogInformation(
            "Enqueued pipeline recovery request {ItemId} for session {SessionId}", 
            enqueued!.Id, sessionId);
        
        return EnqueueResult.Succeeded(enqueued.Id);
    }

    /// <inheritdoc />
    public async Task<bool> TryCancelAsync(Guid itemId, CancellationToken ct = default)
    {
        // Use atomic cancel operation to avoid TOCTOU race
        var cancelled = await _repository.TryCancelIfPendingAsync(itemId, ct);
        if (cancelled)
        {
            _logger.LogInformation("Cancelled pipeline queue item {ItemId}", itemId);
        }
        return cancelled;
    }

    /// <inheritdoc />
    public Task<PipelineQueueItem?> GetItemAsync(Guid itemId, CancellationToken ct = default)
        => _repository.GetByIdAsync(itemId, ct);

    /// <inheritdoc />
    public Task<PipelineQueueItem?> GetItemBySessionAsync(Guid sessionId, CancellationToken ct = default)
        => _repository.GetBySessionIdAsync(sessionId, ct);

    /// <inheritdoc />
    public async Task<bool> WaitForItemAsync(CancellationToken ct = default)
    {
        try
        {
            // Wait for a signal that an item might be available
            await _signalChannel.Reader.ReadAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void SignalItemAvailable()
    {
        // Non-blocking write - just signal that work might be available
        _signalChannel.Writer.TryWrite(true);
    }

    /// <summary>
    /// Claims the next available item for processing.
    /// Called by workers.
    /// </summary>
    public async Task<PipelineQueueItem?> ClaimNextAsync(CancellationToken ct = default)
    {
        var item = await _repository.ClaimNextAsync(ct);
        if (item != null)
        {
            Interlocked.Increment(ref _processingCount);
        }
        return item;
    }

    /// <summary>
    /// Marks an item as completed.
    /// Called by workers.
    /// </summary>
    public async Task CompleteAsync(Guid itemId, CancellationToken ct = default)
    {
        await _repository.CompleteAsync(itemId, ct);
        Interlocked.Decrement(ref _processingCount);
    }

    /// <summary>
    /// Marks an item as failed.
    /// Called by workers.
    /// </summary>
    public async Task FailAsync(Guid itemId, string errorMessage, CancellationToken ct = default)
    {
        await _repository.FailAsync(itemId, errorMessage, ct);
        Interlocked.Decrement(ref _processingCount);
    }
    
    /// <summary>
    /// Releases the processing slot without changing item status.
    /// Called when worker is interrupted (e.g., shutdown).
    /// </summary>
    public void ReleaseSlot()
    {
        Interlocked.Decrement(ref _processingCount);
    }

    /// <summary>
    /// Resets an item for retry after a transient failure.
    /// Releases the processing slot and signals for pickup.
    /// </summary>
    /// <returns>True if reset, false if item can't be retried.</returns>
    public async Task<bool> ResetForRetryAsync(Guid itemId, CancellationToken ct = default)
    {
        var reset = await _repository.ResetForRetryAsync(itemId, ct);
        if (reset)
        {
            Interlocked.Decrement(ref _processingCount);
            SignalItemAvailable();
            _logger.LogInformation("Reset pipeline item {ItemId} for retry", itemId);
        }
        return reset;
    }

    /// <summary>
    /// Gets the current attempt count for an item.
    /// </summary>
    public async Task<int> GetAttemptCountAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await _repository.GetByIdAsync(itemId, ct);
        return item?.Attempts ?? 0;
    }

    /// <summary>
    /// Deserializes PipelineOptions from JSON.
    /// </summary>
    public static PipelineOptions? DeserializeOptions(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<PipelineOptions>(json, JsonOptions);

    /// <summary>
    /// Deserializes PipelineSession from JSON.
    /// </summary>
    public static PipelineSession? DeserializeSession(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<PipelineSession>(json, JsonOptions);

    /// <summary>
    /// Deserializes PipelineResult from JSON.
    /// </summary>
    public static PipelineResult? DeserializeResult(string? json)
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<PipelineResult>(json, JsonOptions);

    // === Dead Letter Queue Operations ===

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineQueueItem>> GetDeadLetterItemsAsync(CancellationToken ct = default)
        => _repository.GetDeadLetterItemsAsync(ct);

    /// <inheritdoc />
    public async Task<bool> RequeueDeadLetterItemAsync(Guid itemId, CancellationToken ct = default)
    {
        var requeued = await _repository.RequeueDeadLetterItemAsync(itemId, ct);
        if (requeued)
        {
            SignalItemAvailable();
            _logger.LogInformation("Requeued dead letter item {ItemId} for processing", itemId);
        }
        return requeued;
    }

    /// <inheritdoc />
    public Task<bool> DeleteDeadLetterItemAsync(Guid itemId, CancellationToken ct = default)
        => _repository.DeleteDeadLetterItemAsync(itemId, ct);

    /// <summary>
    /// Moves an item to the dead letter queue.
    /// Called by workers after exhausting retries.
    /// </summary>
    public async Task MoveToDeadLetterAsync(Guid itemId, string reason, CancellationToken ct = default)
    {
        var success = await _repository.MoveToDeadLetterAsync(itemId, reason, ct);
        if (success)
        {
            Interlocked.Decrement(ref _processingCount);
            _logger.LogWarning("Moved pipeline item {ItemId} to dead letter queue: {Reason}", itemId, reason);
        }
    }

    // === Queue Position and ETA ===

    /// <inheritdoc />
    public async Task<QueuePositionInfo?> GetQueuePositionAsync(Guid itemId, CancellationToken ct = default)
    {
        var position = await _repository.GetQueuePositionAsync(itemId, ct);
        if (position == null)
            return null;
        
        var totalPending = await _repository.GetCountByStatusAsync(PipelineQueueStatus.Pending, ct);
        var avgProcessingTime = await _repository.GetAverageProcessingTimeAsync(hoursLookback: 24, ct);
        
        // Estimate wait time: position * average processing time
        TimeSpan? estimatedWait = avgProcessingTime.HasValue 
            ? TimeSpan.FromTicks(avgProcessingTime.Value.Ticks * position.Value)
            : null;
        
        return new QueuePositionInfo(position.Value, totalPending, estimatedWait);
    }

    // === Rate Limiting ===

    /// <inheritdoc />
    public async Task<RateLimitCheckResult> CheckRateLimitAsync(Guid ownerId, CancellationToken ct = default)
    {
        // Get current counts for this user
        var currentPending = await _repository.GetPendingCountByOwnerAsync(ownerId, ct);
        var currentProcessing = await _repository.GetProcessingCountByOwnerAsync(ownerId, ct);
        
        // Use dynamic settings (supports hot reload)
        var settings = _settingsMonitor.CurrentValue;
        var maxPending = settings.MaxPendingItemsPerUser;
        var maxConcurrent = settings.MaxConcurrentItemsPerUser;
        
        var isAllowed = true;
        string? reason = null;
        
        if (maxPending > 0 && currentPending >= maxPending)
        {
            isAllowed = false;
            reason = $"Maximum pending items ({maxPending}) reached for this user.";
        }
        else if (maxConcurrent > 0 && currentProcessing >= maxConcurrent)
        {
            isAllowed = false;
            reason = $"Maximum concurrent items ({maxConcurrent}) reached for this user.";
        }
        
        return new RateLimitCheckResult(
            isAllowed,
            reason,
            currentPending,
            maxPending,
            currentProcessing,
            maxConcurrent);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _signalChannel.Writer.Complete();
    }
}
