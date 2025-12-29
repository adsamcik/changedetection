using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Repository for managing the notification outbox.
/// Provides persistence for notifications that need to be sent reliably.
/// </summary>
public interface INotificationOutboxRepository
{
    /// <summary>
    /// Adds a new notification to the outbox.
    /// </summary>
    Task<NotificationOutboxEntry> AddAsync(NotificationOutboxEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Gets all pending notifications ready to be sent.
    /// Only returns entries with Status = Pending.
    /// </summary>
    Task<IReadOnlyList<NotificationOutboxEntry>> GetPendingAsync(int maxCount = 100, CancellationToken ct = default);

    /// <summary>
    /// Gets notifications that failed and are ready to retry.
    /// Returns entries with Status = RetryPending and NextRetryAt &lt;= now.
    /// </summary>
    Task<IReadOnlyList<NotificationOutboxEntry>> GetReadyForRetryAsync(int maxCount = 50, CancellationToken ct = default);

    /// <summary>
    /// Atomically marks a notification as processing.
    /// Returns false if the entry was already claimed by another processor.
    /// </summary>
    Task<bool> TryClaimForProcessingAsync(Guid entryId, CancellationToken ct = default);

    /// <summary>
    /// Marks a notification as successfully sent.
    /// </summary>
    Task MarkSentAsync(Guid entryId, CancellationToken ct = default);

    /// <summary>
    /// Marks a notification as failed with an error message.
    /// Calculates next retry time based on retry count.
    /// </summary>
    Task MarkFailedAsync(Guid entryId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Marks a notification as permanently failed (no more retries).
    /// </summary>
    Task MarkPermanentlyFailedAsync(Guid entryId, string errorMessage, CancellationToken ct = default);

    /// <summary>
    /// Recovers stale processing entries (stuck due to crash).
    /// Resets entries that have been Processing for longer than the timeout.
    /// </summary>
    Task<int> RecoverStaleProcessingAsync(TimeSpan processingTimeout, CancellationToken ct = default);

    /// <summary>
    /// Gets a notification by ID.
    /// </summary>
    Task<NotificationOutboxEntry?> GetByIdAsync(Guid entryId, CancellationToken ct = default);

    /// <summary>
    /// Deletes old sent notifications (for cleanup).
    /// </summary>
    Task<int> DeleteOldSentAsync(TimeSpan olderThan, CancellationToken ct = default);

    /// <summary>
    /// Gets counts of notifications by status for monitoring.
    /// </summary>
    Task<NotificationOutboxStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
/// Statistics about the notification outbox.
/// </summary>
public record NotificationOutboxStats(
    int PendingCount,
    int ProcessingCount,
    int RetryPendingCount,
    int FailedCount,
    int SentLast24Hours);
