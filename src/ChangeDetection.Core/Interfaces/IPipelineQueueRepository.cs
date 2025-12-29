using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Repository for persistent pipeline queue operations.
/// Enables reliable queue processing that survives server restarts.
/// </summary>
public interface IPipelineQueueRepository
{
    /// <summary>
    /// Enqueues a new item for processing.
    /// </summary>
    Task<PipelineQueueItem> EnqueueAsync(PipelineQueueItem item, CancellationToken ct = default);
    
    /// <summary>
    /// Claims the next available pending item for processing.
    /// Atomically updates status to Processing.
    /// </summary>
    /// <returns>The claimed item, or null if no items are pending.</returns>
    Task<PipelineQueueItem?> ClaimNextAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Marks an item as completed.
    /// </summary>
    Task CompleteAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Marks an item as failed with an error message.
    /// </summary>
    Task FailAsync(Guid itemId, string errorMessage, CancellationToken ct = default);
    
    /// <summary>
    /// Marks an item as cancelled.
    /// </summary>
    Task CancelAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Atomically cancels an item only if it's still pending.
    /// Prevents race condition between status check and cancellation.
    /// </summary>
    /// <returns>True if cancelled, false if item was already claimed or not found.</returns>
    Task<bool> TryCancelIfPendingAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets an item by ID.
    /// </summary>
    Task<PipelineQueueItem?> GetByIdAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets an item by session ID.
    /// </summary>
    Task<PipelineQueueItem?> GetBySessionIdAsync(Guid sessionId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets all pending items in priority order.
    /// Used for recovery on startup.
    /// </summary>
    Task<IReadOnlyList<PipelineQueueItem>> GetPendingItemsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Gets all items that were processing when the server stopped.
    /// These need to be re-queued on startup.
    /// </summary>
    Task<IReadOnlyList<PipelineQueueItem>> GetStaleProcessingItemsAsync(
        TimeSpan staleThreshold, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Resets stale processing items back to pending status.
    /// Returns the number of items reset.
    /// </summary>
    Task<int> ResetStaleItemsAsync(TimeSpan staleThreshold, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the count of items by status.
    /// </summary>
    Task<int> GetCountByStatusAsync(PipelineQueueStatus status, CancellationToken ct = default);
    
    /// <summary>
    /// Resets an item back to pending for retry.
    /// Increments the attempt counter.
    /// </summary>
    /// <returns>True if reset, false if item not found or not in processing state.</returns>
    Task<bool> ResetForRetryAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Moves an item to the dead letter queue after exhausting all retries.
    /// Returns true if successful, false if item not found or not in Processing status.
    /// </summary>
    Task<bool> MoveToDeadLetterAsync(Guid itemId, string reason, CancellationToken ct = default);
    
    /// <summary>
    /// Gets all dead letter items for manual inspection.
    /// </summary>
    Task<IReadOnlyList<PipelineQueueItem>> GetDeadLetterItemsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Requeues a dead letter item for reprocessing.
    /// Resets attempt counter and moves back to pending.
    /// </summary>
    /// <returns>True if requeued, false if item not found or not in dead letter status.</returns>
    Task<bool> RequeueDeadLetterItemAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Deletes a dead letter item permanently.
    /// </summary>
    /// <returns>True if deleted, false if not found.</returns>
    Task<bool> DeleteDeadLetterItemAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the position of an item in the pending queue.
    /// </summary>
    /// <returns>1-based position, or null if not pending.</returns>
    Task<int?> GetQueuePositionAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the count of pending items for a specific owner.
    /// </summary>
    Task<int> GetPendingCountByOwnerAsync(Guid ownerId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the count of processing items for a specific owner.
    /// </summary>
    Task<int> GetProcessingCountByOwnerAsync(Guid ownerId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets average processing time for completed items in the last N hours.
    /// Used for ETA estimation.
    /// </summary>
    Task<TimeSpan?> GetAverageProcessingTimeAsync(int hoursLookback = 24, CancellationToken ct = default);
    
    /// <summary>
    /// Deletes completed/failed items older than the specified age.
    /// Dead letter items use 3x longer retention.
    /// </summary>
    Task<int> PurgeOldItemsAsync(TimeSpan maxAge, CancellationToken ct = default);
    
    /// <summary>
    /// Atomically checks rate limits and enqueues an item.
    /// This prevents TOCTOU race conditions where concurrent requests could bypass limits.
    /// </summary>
    /// <param name="item">The item to enqueue.</param>
    /// <param name="maxPending">Maximum pending items per user (0 = unlimited).</param>
    /// <param name="maxConcurrent">Maximum concurrent items per user (0 = unlimited).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (success, item if success, rate limit result if rejected).</returns>
    Task<(bool Success, PipelineQueueItem? Item, RateLimitCheckResult? RateLimitResult)> TryEnqueueWithLimitAsync(
        PipelineQueueItem item,
        int maxPending,
        int maxConcurrent,
        CancellationToken ct = default);
}
