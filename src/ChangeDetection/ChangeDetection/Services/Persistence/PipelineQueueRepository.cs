using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using LiteDB;
using static ChangeDetection.Core.Interfaces.IPipelineQueueService;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB repository for the persistent pipeline queue.
/// Enables reliable queue processing that survives server restarts.
/// </summary>
public class PipelineQueueRepository(LiteDbContext context, ILogger<PipelineQueueRepository> logger) 
    : IPipelineQueueRepository
{
    private readonly ILiteCollection<PipelineQueueItem> _collection = InitializeCollection(context);
    private readonly object _claimLock = new();

    private static ILiteCollection<PipelineQueueItem> InitializeCollection(LiteDbContext context)
    {
        var collection = context.Database.GetCollection<PipelineQueueItem>("pipeline_queue");
        
        // Indexes for efficient queries
        collection.EnsureIndex(x => x.Status);
        collection.EnsureIndex(x => x.SessionId);
        collection.EnsureIndex(x => x.OwnerId);
        collection.EnsureIndex(x => x.EnqueuedAt);
        collection.EnsureIndex(x => x.Priority);
        collection.EnsureIndex(x => x.StartedAt);
        
        return collection;
    }

    /// <inheritdoc />
    public Task<PipelineQueueItem> EnqueueAsync(PipelineQueueItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collection.Insert(item);
        logger.LogDebug("Enqueued pipeline item {ItemId} for session {SessionId}", item.Id, item.SessionId);
        return Task.FromResult(item);
    }

    /// <inheritdoc />
    public Task<PipelineQueueItem?> ClaimNextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        // Use lock to ensure atomic claim across multiple workers
        lock (_claimLock)
        {
            // Find next pending item ordered by priority then enqueue time
            // LiteDB only supports single OrderBy, so we fetch limited candidates and sort in memory
            // Limit to reasonable batch to avoid memory explosion with large queues
            var candidates = _collection.Query()
                .Where(x => x.Status == PipelineQueueStatus.Pending)
                .OrderBy(x => x.Priority)
                .Limit(100)
                .ToList();
            
            // Apply full ordering with tie-breaker for deterministic results
            var item = candidates
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.EnqueuedAt)
                .ThenBy(x => x.Id)
                .FirstOrDefault();
            
            if (item == null)
                return Task.FromResult<PipelineQueueItem?>(null);
            
            // Atomically update to Processing
            item.Status = PipelineQueueStatus.Processing;
            item.StartedAt = DateTimeOffset.UtcNow;
            item.Attempts++;
            _collection.Update(item);
            
            logger.LogDebug("Claimed pipeline item {ItemId} (attempt {Attempt})", item.Id, item.Attempts);
            return Task.FromResult<PipelineQueueItem?>(item);
        }
    }

    /// <inheritdoc />
    public Task CompleteAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var item = _collection.FindById(itemId);
        if (item == null)
        {
            logger.LogWarning("Attempted to complete non-existent queue item {ItemId}", itemId);
            return Task.CompletedTask;
        }
        
        item.Status = PipelineQueueStatus.Completed;
        item.CompletedAt = DateTimeOffset.UtcNow;
        _collection.Update(item);
        
        logger.LogDebug("Completed pipeline item {ItemId}", itemId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FailAsync(Guid itemId, string errorMessage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var item = _collection.FindById(itemId);
        if (item == null)
        {
            logger.LogWarning("Attempted to fail non-existent queue item {ItemId}", itemId);
            return Task.CompletedTask;
        }
        
        item.Status = PipelineQueueStatus.Failed;
        item.CompletedAt = DateTimeOffset.UtcNow;
        item.ErrorMessage = errorMessage;
        _collection.Update(item);
        
        logger.LogDebug("Failed pipeline item {ItemId}: {Error}", itemId, errorMessage);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CancelAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var item = _collection.FindById(itemId);
        if (item == null)
            return Task.CompletedTask;
        
        item.Status = PipelineQueueStatus.Cancelled;
        item.CompletedAt = DateTimeOffset.UtcNow;
        _collection.Update(item);
        
        logger.LogDebug("Cancelled pipeline item {ItemId}", itemId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryCancelIfPendingAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        lock (_claimLock)
        {
            var item = _collection.FindById(itemId);
            if (item?.Status != PipelineQueueStatus.Pending)
                return Task.FromResult(false);
            
            item.Status = PipelineQueueStatus.Cancelled;
            item.CompletedAt = DateTimeOffset.UtcNow;
            _collection.Update(item);
            
            logger.LogDebug("Atomically cancelled pending pipeline item {ItemId}", itemId);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<PipelineQueueItem?> GetByIdAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var item = _collection.FindById(itemId);
        return Task.FromResult<PipelineQueueItem?>(item);
    }

    /// <inheritdoc />
    public Task<PipelineQueueItem?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        // Return the most recent item for this session that's pending or processing
        var item = _collection.Query()
            .Where(x => x.SessionId == sessionId && 
                       (x.Status == PipelineQueueStatus.Pending || x.Status == PipelineQueueStatus.Processing))
            .OrderByDescending(x => x.EnqueuedAt)
            .FirstOrDefault();
        
        return Task.FromResult<PipelineQueueItem?>(item);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineQueueItem>> GetPendingItemsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var items = _collection.Query()
            .Where(x => x.Status == PipelineQueueStatus.Pending)
            .OrderBy(x => x.Priority)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PipelineQueueItem>>(items);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineQueueItem>> GetStaleProcessingItemsAsync(
        TimeSpan staleThreshold, 
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;
        var items = _collection.Query()
            .Where(x => x.Status == PipelineQueueStatus.Processing && x.StartedAt < cutoff)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PipelineQueueItem>>(items);
    }

    /// <inheritdoc />
    public Task<int> ResetStaleItemsAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;
        var staleItems = _collection.Query()
            .Where(x => x.Status == PipelineQueueStatus.Processing && x.StartedAt < cutoff)
            .ToList();
        
        foreach (var item in staleItems)
        {
            item.Status = PipelineQueueStatus.Pending;
            item.StartedAt = null;
            _collection.Update(item);
            logger.LogInformation("Reset stale pipeline item {ItemId} back to pending", item.Id);
        }
        
        return Task.FromResult(staleItems.Count);
    }

    /// <inheritdoc />
    public Task<int> GetCountByStatusAsync(PipelineQueueStatus status, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_collection.Count(x => x.Status == status));
    }

    /// <inheritdoc />
    public Task<bool> ResetForRetryAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        lock (_claimLock)
        {
            var item = _collection.FindById(itemId);
            if (item == null || item.Status != PipelineQueueStatus.Processing)
                return Task.FromResult(false);
            
            item.Status = PipelineQueueStatus.Pending;
            item.StartedAt = null;
            // Note: Attempts is already incremented when claimed, so we don't increment again
            _collection.Update(item);
            
            logger.LogInformation("Reset pipeline item {ItemId} for retry (attempt {Attempt})", itemId, item.Attempts);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> MoveToDeadLetterAsync(Guid itemId, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        lock (_claimLock)
        {
            var item = _collection.FindById(itemId);
            if (item == null)
            {
                logger.LogWarning("Attempted to move non-existent queue item {ItemId} to dead letter", itemId);
                return Task.FromResult(false);
            }
            
            // Only allow moving Processing items to dead letter
            if (item.Status != PipelineQueueStatus.Processing)
            {
                logger.LogWarning(
                    "Attempted to move item {ItemId} with status {Status} to dead letter (expected Processing)", 
                    itemId, item.Status);
                return Task.FromResult(false);
            }
            
            item.Status = PipelineQueueStatus.DeadLetter;
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.ErrorMessage = reason;
            _collection.Update(item);
            
            logger.LogWarning("Moved pipeline item {ItemId} to dead letter queue: {Reason}", itemId, reason);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineQueueItem>> GetDeadLetterItemsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var items = _collection.Query()
            .Where(x => x.Status == PipelineQueueStatus.DeadLetter)
            .OrderByDescending(x => x.CompletedAt)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PipelineQueueItem>>(items);
    }

    /// <inheritdoc />
    public Task<bool> RequeueDeadLetterItemAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        lock (_claimLock)
        {
            var item = _collection.FindById(itemId);
            if (item?.Status != PipelineQueueStatus.DeadLetter)
                return Task.FromResult(false);
            
            // Reset for fresh processing (keeps original enqueue time for FIFO ordering context)
            item.Status = PipelineQueueStatus.Pending;
            item.Attempts = 0;
            item.StartedAt = null;
            item.CompletedAt = null;
            item.ErrorMessage = null;
            _collection.Update(item);
            
            logger.LogInformation("Requeued dead letter item {ItemId} for reprocessing", itemId);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeleteDeadLetterItemAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        lock (_claimLock)
        {
            var item = _collection.FindById(itemId);
            if (item?.Status != PipelineQueueStatus.DeadLetter)
                return Task.FromResult(false);
            
            _collection.Delete(itemId);
            logger.LogInformation("Deleted dead letter item {ItemId}", itemId);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<int?> GetQueuePositionAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var item = _collection.FindById(itemId);
        if (item?.Status != PipelineQueueStatus.Pending)
            return Task.FromResult<int?>(null);
        
        // Count items ahead in queue (higher priority, or same priority but earlier enqueue, or same but lower Id)
        // Id is used as final tie-breaker for deterministic ordering matching ClaimNextAsync
        var aheadCount = _collection.Count(x => 
            x.Status == PipelineQueueStatus.Pending &&
            x.Id != itemId &&
            (x.Priority < item.Priority || 
             (x.Priority == item.Priority && x.EnqueuedAt < item.EnqueuedAt) ||
             (x.Priority == item.Priority && x.EnqueuedAt == item.EnqueuedAt && x.Id.CompareTo(item.Id) < 0)));
        
        return Task.FromResult<int?>(aheadCount + 1); // 1-based position
    }

    /// <inheritdoc />
    public Task<int> GetPendingCountByOwnerAsync(Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_collection.Count(x => x.OwnerId == ownerId && x.Status == PipelineQueueStatus.Pending));
    }

    /// <inheritdoc />
    public Task<int> GetProcessingCountByOwnerAsync(Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_collection.Count(x => x.OwnerId == ownerId && x.Status == PipelineQueueStatus.Processing));
    }

    /// <inheritdoc />
    public Task<TimeSpan?> GetAverageProcessingTimeAsync(int hoursLookback = 24, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var cutoff = DateTimeOffset.UtcNow.AddHours(-hoursLookback);
        var completedItems = _collection.Query()
            .Where(x => x.Status == PipelineQueueStatus.Completed && 
                       x.CompletedAt != null && 
                       x.StartedAt != null &&
                       x.CompletedAt > cutoff)
            .ToList();
        
        if (completedItems.Count == 0)
            return Task.FromResult<TimeSpan?>(null);
        
        var totalTicks = completedItems
            .Where(x => x.StartedAt.HasValue && x.CompletedAt.HasValue)
            .Select(x => (x.CompletedAt!.Value - x.StartedAt!.Value).Ticks)
            .Sum();
        
        var avgTicks = totalTicks / completedItems.Count;
        return Task.FromResult<TimeSpan?>(TimeSpan.FromTicks(avgTicks));
    }

    /// <inheritdoc />
    public Task<int> PurgeOldItemsAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        // Dead letter items use 3x longer retention for investigation
        var deadLetterCutoff = DateTimeOffset.UtcNow - TimeSpan.FromTicks(maxAge.Ticks * 3);
        
        var deleted = _collection.DeleteMany(x => 
            ((x.Status == PipelineQueueStatus.Completed || 
              x.Status == PipelineQueueStatus.Failed || 
              x.Status == PipelineQueueStatus.Cancelled) && x.CompletedAt < cutoff) ||
            (x.Status == PipelineQueueStatus.DeadLetter && x.CompletedAt < deadLetterCutoff));
        
        if (deleted > 0)
            logger.LogInformation("Purged {Count} old pipeline queue items", deleted);
        
        return Task.FromResult(deleted);
    }
    
    /// <inheritdoc />
    public Task<(bool Success, PipelineQueueItem? Item, RateLimitCheckResult? RateLimitResult)> TryEnqueueWithLimitAsync(
        PipelineQueueItem item,
        int maxPending,
        int maxConcurrent,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        lock (_claimLock)
        {
            // Atomically check limits and enqueue
            var pendingCount = _collection.Count(x => 
                x.OwnerId == item.OwnerId && x.Status == PipelineQueueStatus.Pending);
            var processingCount = _collection.Count(x => 
                x.OwnerId == item.OwnerId && x.Status == PipelineQueueStatus.Processing);
            
            if (maxPending > 0 && pendingCount >= maxPending)
            {
                var result = new RateLimitCheckResult(
                    false, 
                    $"Maximum pending items ({maxPending}) reached for this user.",
                    pendingCount, maxPending, processingCount, maxConcurrent);
                return Task.FromResult<(bool, PipelineQueueItem?, RateLimitCheckResult?)>((false, null, result));
            }
            
            if (maxConcurrent > 0 && processingCount >= maxConcurrent)
            {
                var result = new RateLimitCheckResult(
                    false,
                    $"Maximum concurrent items ({maxConcurrent}) reached for this user.",
                    pendingCount, maxPending, processingCount, maxConcurrent);
                return Task.FromResult<(bool, PipelineQueueItem?, RateLimitCheckResult?)>((false, null, result));
            }
            
            // Limits OK - enqueue atomically
            _collection.Insert(item);
            logger.LogDebug("Enqueued pipeline item {ItemId} for session {SessionId} (atomic with limit check)", 
                item.Id, item.SessionId);
            
            return Task.FromResult<(bool, PipelineQueueItem?, RateLimitCheckResult?)>((true, item, null));
        }
    }
}
