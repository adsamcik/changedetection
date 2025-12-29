using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for queuing notifications to the persistent outbox.
/// Notifications are queued first, then processed asynchronously for reliable delivery.
/// </summary>
public interface INotificationOutboxService
{
    /// <summary>
    /// Queues a change notification to the outbox for reliable delivery.
    /// </summary>
    Task QueueChangeNotificationAsync(
        WatchedSite watch, 
        ChangeEvent change, 
        string? summary = null, 
        CancellationToken ct = default);

    /// <summary>
    /// Queues an alert notification to the outbox for reliable delivery.
    /// </summary>
    Task QueueAlertNotificationAsync(
        WatchedSite watch,
        AlertEvaluationResult alertResult,
        NotificationContext context,
        CancellationToken ct = default);

    /// <summary>
    /// Processes pending notifications from the outbox.
    /// Called by the background processor service.
    /// </summary>
    Task<int> ProcessPendingAsync(int batchSize = 50, CancellationToken ct = default);

    /// <summary>
    /// Processes notifications that are ready for retry.
    /// Called by the background processor service.
    /// </summary>
    Task<int> ProcessRetryAsync(int batchSize = 20, CancellationToken ct = default);

    /// <summary>
    /// Recovers stale processing entries (stuck due to crash).
    /// Should be called on startup.
    /// </summary>
    Task<int> RecoverStaleAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets statistics about the outbox for monitoring.
    /// </summary>
    Task<NotificationOutboxStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Cleans up old sent notifications.
    /// </summary>
    Task<int> CleanupOldNotificationsAsync(TimeSpan olderThan, CancellationToken ct = default);
}
