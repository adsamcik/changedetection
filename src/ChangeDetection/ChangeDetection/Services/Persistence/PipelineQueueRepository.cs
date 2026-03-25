using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using LiteDB;
using static ChangeDetection.Core.Interfaces.IPipelineQueueService;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB repository for the persistent pipeline queue.
/// All operations are serialized through <see cref="ThreadSafeLiteDbContext"/>.
/// </summary>
public class PipelineQueueRepository : IPipelineQueueRepository
{
    private readonly ThreadSafeLiteDbContext _safeContext;
    private readonly ILogger<PipelineQueueRepository> _logger;

    public PipelineQueueRepository(ThreadSafeLiteDbContext safeContext, ILogger<PipelineQueueRepository> logger)
    {
        _safeContext = safeContext;
        _logger = logger;
    }

    private static ILiteCollection<PipelineQueueItem> Col(ILiteDatabase db)
        => db.GetCollection<PipelineQueueItem>("pipeline_queue");

    /// <inheritdoc />
    public async Task<PipelineQueueItem> EnqueueAsync(PipelineQueueItem item, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db => { Col(db).Insert(item); }, ct);
        _logger.LogDebug("Enqueued pipeline item {ItemId} for session {SessionId}", item.Id, item.SessionId);
        return item;
    }

    /// <inheritdoc />
    public async Task<PipelineQueueItem?> ClaimNextAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var item = await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var candidates = col.Query()
                .Where(x => x.Status == PipelineQueueStatus.Pending)
                .OrderBy(x => x.Priority)
                .Limit(100)
                .ToList();

            var next = candidates
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.EnqueuedAt)
                .ThenBy(x => x.Id)
                .FirstOrDefault();

            if (next == null) return (PipelineQueueItem?)null;

            next.Status = PipelineQueueStatus.Processing;
            next.StartedAt = DateTimeOffset.UtcNow;
            next.Attempts++;
            col.Update(next);
            return next;
        }, ct);

        if (item != null)
            _logger.LogDebug("Claimed pipeline item {ItemId} (attempt {Attempt})", item.Id, item.Attempts);

        return item;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item == null)
            {
                _logger.LogWarning("Attempted to complete non-existent queue item {ItemId}", itemId);
                return;
            }
            item.Status = PipelineQueueStatus.Completed;
            item.CompletedAt = DateTimeOffset.UtcNow;
            col.Update(item);
            _logger.LogDebug("Completed pipeline item {ItemId}", itemId);
        }, ct);
    }

    /// <inheritdoc />
    public async Task FailAsync(Guid itemId, string errorMessage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item == null)
            {
                _logger.LogWarning("Attempted to fail non-existent queue item {ItemId}", itemId);
                return;
            }
            item.Status = PipelineQueueStatus.Failed;
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.ErrorMessage = errorMessage;
            col.Update(item);
            _logger.LogDebug("Failed pipeline item {ItemId}: {Error}", itemId, errorMessage);
        }, ct);
    }

    /// <inheritdoc />
    public async Task CancelAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item == null) return;
            item.Status = PipelineQueueStatus.Cancelled;
            item.CompletedAt = DateTimeOffset.UtcNow;
            col.Update(item);
            _logger.LogDebug("Cancelled pipeline item {ItemId}", itemId);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryCancelIfPendingAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item?.Status != PipelineQueueStatus.Pending) return false;
            item.Status = PipelineQueueStatus.Cancelled;
            item.CompletedAt = DateTimeOffset.UtcNow;
            col.Update(item);
            _logger.LogDebug("Atomically cancelled pending pipeline item {ItemId}", itemId);
            return true;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<PipelineQueueItem?> GetByIdAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db => (PipelineQueueItem?)Col(db).FindById(itemId), ct);
    }

    /// <inheritdoc />
    public async Task<PipelineQueueItem?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            (PipelineQueueItem?)Col(db).Query()
                .Where(x => x.SessionId == sessionId &&
                    (x.Status == PipelineQueueStatus.Pending || x.Status == PipelineQueueStatus.Processing))
                .OrderByDescending(x => x.EnqueuedAt)
                .FirstOrDefault(), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineQueueItem>> GetPendingItemsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            (IReadOnlyList<PipelineQueueItem>)Col(db).Query()
                .Where(x => x.Status == PipelineQueueStatus.Pending)
                .OrderBy(x => x.Priority)
                .ToList(), ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineQueueItem>> GetStaleProcessingItemsAsync(
        TimeSpan staleThreshold, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var cutoff = DateTimeOffset.UtcNow - staleThreshold;
        return await _safeContext.ExecuteAsync(db =>
            (IReadOnlyList<PipelineQueueItem>)Col(db).Query()
                .Where(x => x.Status == PipelineQueueStatus.Processing && x.StartedAt < cutoff)
                .ToList(), ct);
    }

    /// <inheritdoc />
    public async Task<int> ResetStaleItemsAsync(TimeSpan staleThreshold, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var cutoff = DateTimeOffset.UtcNow - staleThreshold;
            var staleItems = col.Query()
                .Where(x => x.Status == PipelineQueueStatus.Processing && x.StartedAt < cutoff)
                .ToList();
            foreach (var item in staleItems)
            {
                item.Status = PipelineQueueStatus.Pending;
                item.StartedAt = null;
                col.Update(item);
                _logger.LogInformation("Reset stale pipeline item {ItemId} back to pending", item.Id);
            }
            return staleItems.Count;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<int> GetCountByStatusAsync(PipelineQueueStatus status, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db => Col(db).Count(x => x.Status == status), ct);
    }

    /// <inheritdoc />
    public async Task<bool> ResetForRetryAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item == null || item.Status != PipelineQueueStatus.Processing) return false;
            item.Status = PipelineQueueStatus.Pending;
            item.StartedAt = null;
            col.Update(item);
            _logger.LogInformation("Reset pipeline item {ItemId} for retry (attempt {Attempt})", itemId, item.Attempts);
            return true;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> MoveToDeadLetterAsync(Guid itemId, string reason, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item == null)
            {
                _logger.LogWarning("Attempted to move non-existent queue item {ItemId} to dead letter", itemId);
                return false;
            }
            if (item.Status != PipelineQueueStatus.Processing)
            {
                _logger.LogWarning("Attempted to move item {ItemId} with status {Status} to dead letter (expected Processing)", itemId, item.Status);
                return false;
            }
            item.Status = PipelineQueueStatus.DeadLetter;
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.ErrorMessage = reason;
            col.Update(item);
            _logger.LogWarning("Moved pipeline item {ItemId} to dead letter queue: {Reason}", itemId, reason);
            return true;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PipelineQueueItem>> GetDeadLetterItemsAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            (IReadOnlyList<PipelineQueueItem>)Col(db).Query()
                .Where(x => x.Status == PipelineQueueStatus.DeadLetter)
                .OrderByDescending(x => x.CompletedAt)
                .ToList(), ct);
    }

    /// <inheritdoc />
    public async Task<bool> RequeueDeadLetterItemAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item?.Status != PipelineQueueStatus.DeadLetter) return false;
            item.Status = PipelineQueueStatus.Pending;
            item.Attempts = 0;
            item.StartedAt = null;
            item.CompletedAt = null;
            item.ErrorMessage = null;
            col.Update(item);
            _logger.LogInformation("Requeued dead letter item {ItemId} for reprocessing", itemId);
            return true;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteDeadLetterItemAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item?.Status != PipelineQueueStatus.DeadLetter) return false;
            col.Delete(itemId);
            _logger.LogInformation("Deleted dead letter item {ItemId}", itemId);
            return true;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<int?> GetQueuePositionAsync(Guid itemId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var item = col.FindById(itemId);
            if (item?.Status != PipelineQueueStatus.Pending) return (int?)null;
            var aheadCount = col.Count(x =>
                x.Status == PipelineQueueStatus.Pending &&
                x.Id != itemId &&
                (x.Priority < item.Priority ||
                 (x.Priority == item.Priority && x.EnqueuedAt < item.EnqueuedAt) ||
                 (x.Priority == item.Priority && x.EnqueuedAt == item.EnqueuedAt && x.Id.CompareTo(item.Id) < 0)));
            return (int?)(aheadCount + 1);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<int> GetPendingCountByOwnerAsync(Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            Col(db).Count(x => x.OwnerId == ownerId && x.Status == PipelineQueueStatus.Pending), ct);
    }

    /// <inheritdoc />
    public async Task<int> GetProcessingCountByOwnerAsync(Guid ownerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            Col(db).Count(x => x.OwnerId == ownerId && x.Status == PipelineQueueStatus.Processing), ct);
    }

    /// <inheritdoc />
    public async Task<TimeSpan?> GetAverageProcessingTimeAsync(int hoursLookback = 24, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-hoursLookback);
            var completedItems = Col(db).Query()
                .Where(x => x.Status == PipelineQueueStatus.Completed &&
                    x.CompletedAt != null && x.StartedAt != null && x.CompletedAt > cutoff)
                .ToList();
            if (completedItems.Count == 0) return (TimeSpan?)null;
            var totalTicks = completedItems
                .Where(x => x.StartedAt.HasValue && x.CompletedAt.HasValue)
                .Select(x => (x.CompletedAt!.Value - x.StartedAt!.Value).Ticks)
                .Sum();
            return (TimeSpan?)TimeSpan.FromTicks(totalTicks / completedItems.Count);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<int> PurgeOldItemsAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var cutoff = DateTimeOffset.UtcNow - maxAge;
            var deadLetterCutoff = DateTimeOffset.UtcNow - TimeSpan.FromTicks(maxAge.Ticks * 3);
            var deleted = Col(db).DeleteMany(x =>
                ((x.Status == PipelineQueueStatus.Completed ||
                  x.Status == PipelineQueueStatus.Failed ||
                  x.Status == PipelineQueueStatus.Cancelled) && x.CompletedAt < cutoff) ||
                (x.Status == PipelineQueueStatus.DeadLetter && x.CompletedAt < deadLetterCutoff));
            if (deleted > 0)
                _logger.LogInformation("Purged {Count} old pipeline queue items", deleted);
            return deleted;
        }, ct);
    }

    /// <inheritdoc />
    public async Task<(bool Success, PipelineQueueItem? Item, RateLimitCheckResult? RateLimitResult)> TryEnqueueWithLimitAsync(
        PipelineQueueItem item, int maxPending, int maxConcurrent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
        {
            var col = Col(db);
            var pendingCount = col.Count(x => x.OwnerId == item.OwnerId && x.Status == PipelineQueueStatus.Pending);
            var processingCount = col.Count(x => x.OwnerId == item.OwnerId && x.Status == PipelineQueueStatus.Processing);

            if (maxPending > 0 && pendingCount >= maxPending)
            {
                var r = new RateLimitCheckResult(false,
                    $"Maximum pending items ({maxPending}) reached for this user.",
                    pendingCount, maxPending, processingCount, maxConcurrent);
                return ((bool, PipelineQueueItem?, RateLimitCheckResult?))(false, null, r);
            }
            if (maxConcurrent > 0 && processingCount >= maxConcurrent)
            {
                var r = new RateLimitCheckResult(false,
                    $"Maximum concurrent items ({maxConcurrent}) reached for this user.",
                    pendingCount, maxPending, processingCount, maxConcurrent);
                return ((bool, PipelineQueueItem?, RateLimitCheckResult?))(false, null, r);
            }

            col.Insert(item);
            _logger.LogDebug("Enqueued pipeline item {ItemId} for session {SessionId} (atomic with limit check)",
                item.Id, item.SessionId);
            return ((bool, PipelineQueueItem?, RateLimitCheckResult?))(true, item, null);
        }, ct);
    }
}
