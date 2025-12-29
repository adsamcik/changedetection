using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for managing the persistent pipeline queue.
/// Combines database persistence with in-memory signaling for efficient processing.
/// </summary>
public interface IPipelineQueueService
{
    /// <summary>
    /// Gets the current queue depth (number of pending items).
    /// </summary>
    int QueueDepth { get; }
    
    /// <summary>
    /// Gets the number of items currently being processed.
    /// </summary>
    int ProcessingCount { get; }
    
    /// <summary>
    /// Enqueues a new pipeline execution request.
    /// </summary>
    /// <param name="sessionId">Session ID for result correlation.</param>
    /// <param name="ownerId">User who initiated the request.</param>
    /// <param name="userInput">The user's input to process.</param>
    /// <param name="options">Pipeline options (will be serialized).</param>
    /// <param name="priority">Lower number = higher priority.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The queue item ID for tracking.</returns>
    Task<Guid> EnqueueProcessAsync(
        Guid sessionId,
        Guid ownerId,
        string userInput,
        PipelineOptions? options = null,
        int priority = 0,
        CancellationToken ct = default);
    
    /// <summary>
    /// Enqueues a continuation request with user feedback.
    /// </summary>
    Task<Guid> EnqueueContinueAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineSession session,
        string feedback,
        int priority = 0,
        CancellationToken ct = default);
    
    /// <summary>
    /// Enqueues a recovery request for a failed result.
    /// </summary>
    Task<Guid> EnqueueRecoveryAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineSession session,
        PipelineResult failedResult,
        PipelineOptions options,
        int priority = 0,
        CancellationToken ct = default);
    
    /// <summary>
    /// Enqueues a new pipeline execution request with rate limiting check.
    /// Returns a result indicating success or rate limit rejection.
    /// </summary>
    Task<EnqueueResult> TryEnqueueProcessAsync(
        Guid sessionId,
        Guid ownerId,
        string userInput,
        PipelineOptions? options = null,
        int priority = 0,
        CancellationToken ct = default);
    
    /// <summary>
    /// Enqueues a continuation request with rate limiting check.
    /// </summary>
    Task<EnqueueResult> TryEnqueueContinueAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineSession session,
        string feedback,
        int priority = 0,
        CancellationToken ct = default);
    
    /// <summary>
    /// Enqueues a recovery request with rate limiting check.
    /// </summary>
    Task<EnqueueResult> TryEnqueueRecoveryAsync(
        Guid sessionId,
        Guid ownerId,
        PipelineSession session,
        PipelineResult failedResult,
        PipelineOptions options,
        int priority = 0,
        CancellationToken ct = default);
    
    /// <summary>
    /// Cancels a queued item if it hasn't started processing yet.
    /// </summary>
    /// <returns>True if cancelled, false if already processing or completed.</returns>
    Task<bool> TryCancelAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the status of a queue item.
    /// </summary>
    Task<PipelineQueueItem?> GetItemAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the queue item for a session.
    /// </summary>
    Task<PipelineQueueItem?> GetItemBySessionAsync(Guid sessionId, CancellationToken ct = default);
    
    /// <summary>
    /// Waits for an item to become available for processing.
    /// Used by workers to efficiently wait for work.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if an item may be available, false if cancelled.</returns>
    Task<bool> WaitForItemAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Signals that a new item is available for processing.
    /// </summary>
    void SignalItemAvailable();
    
    // === Worker Operations (used by background workers) ===
    
    /// <summary>
    /// Claims the next available item for processing.
    /// </summary>
    Task<PipelineQueueItem?> ClaimNextAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Marks an item as completed.
    /// </summary>
    Task CompleteAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Marks an item as failed.
    /// </summary>
    Task FailAsync(Guid itemId, string errorMessage, CancellationToken ct = default);
    
    /// <summary>
    /// Releases the processing slot without changing item status.
    /// Called when worker is interrupted.
    /// </summary>
    void ReleaseSlot();
    
    /// <summary>
    /// Resets an item for retry after a transient failure.
    /// </summary>
    Task<bool> ResetForRetryAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Moves an item to the dead letter queue.
    /// </summary>
    Task MoveToDeadLetterAsync(Guid itemId, string reason, CancellationToken ct = default);
    
    /// <summary>
    /// Gets the current queue depth asynchronously.
    /// Preferred over the synchronous QueueDepth property.
    /// </summary>
    Task<int> GetQueueDepthAsync(CancellationToken ct = default);
    
    // === Dead Letter Queue Operations ===
    
    /// <summary>
    /// Gets all items in the dead letter queue.
    /// </summary>
    Task<IReadOnlyList<PipelineQueueItem>> GetDeadLetterItemsAsync(CancellationToken ct = default);
    
    /// <summary>
    /// Requeues a dead letter item for reprocessing.
    /// Resets retry counter and moves back to pending.
    /// </summary>
    /// <returns>True if requeued, false if item not found or not in dead letter.</returns>
    Task<bool> RequeueDeadLetterItemAsync(Guid itemId, CancellationToken ct = default);
    
    /// <summary>
    /// Permanently deletes a dead letter item.
    /// </summary>
    /// <returns>True if deleted, false if not found.</returns>
    Task<bool> DeleteDeadLetterItemAsync(Guid itemId, CancellationToken ct = default);
    
    // === Queue Position and ETA ===
    
    /// <summary>
    /// Gets the position of an item in the pending queue.
    /// </summary>
    /// <returns>Queue position info, or null if item is not pending.</returns>
    Task<QueuePositionInfo?> GetQueuePositionAsync(Guid itemId, CancellationToken ct = default);
    
    // === Rate Limiting ===
    
    /// <summary>
    /// Checks if a user can enqueue more items.
    /// </summary>
    /// <returns>Result indicating if allowed and remaining quota.</returns>
    Task<RateLimitCheckResult> CheckRateLimitAsync(Guid ownerId, CancellationToken ct = default);
}

/// <summary>
/// Information about an item's position in the queue.
/// </summary>
public record QueuePositionInfo(
    /// <summary>1-based position in the queue.</summary>
    int Position,
    /// <summary>Total number of pending items.</summary>
    int TotalPending,
    /// <summary>Estimated wait time based on average processing time.</summary>
    TimeSpan? EstimatedWait);

/// <summary>
/// Result of a rate limit check.
/// </summary>
public record RateLimitCheckResult(
    /// <summary>Whether the user can enqueue more items.</summary>
    bool IsAllowed,
    /// <summary>Reason if not allowed.</summary>
    string? Reason,
    /// <summary>User's current pending item count.</summary>
    int CurrentPending,
    /// <summary>Maximum allowed pending items per user.</summary>
    int MaxPending,
    /// <summary>User's current concurrent processing count.</summary>
    int CurrentProcessing,
    /// <summary>Maximum allowed concurrent items per user.</summary>
    int MaxConcurrent);

/// <summary>
/// Result of an enqueue operation that may be rate limited.
/// </summary>
public record EnqueueResult(
    /// <summary>Whether the item was successfully enqueued.</summary>
    bool Success,
    /// <summary>The queue item ID if successful.</summary>
    Guid? ItemId,
    /// <summary>Rate limit check result (null if rate limiting not checked).</summary>
    RateLimitCheckResult? RateLimitResult)
{
    /// <summary>
    /// Creates a successful enqueue result.
    /// </summary>
    public static EnqueueResult Succeeded(Guid itemId) => new(true, itemId, null);
    
    /// <summary>
    /// Creates a rate-limited (rejected) enqueue result.
    /// </summary>
    public static EnqueueResult RateLimited(RateLimitCheckResult result) => new(false, null, result);
}
