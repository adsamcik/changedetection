using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for sending notifications about changes.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Sends a notification about a detected change.
    /// </summary>
    Task SendNotificationAsync(WatchedSite watch, ChangeEvent change, string? summary = null, CancellationToken ct = default);

    /// <summary>
    /// Sends a notification about a triggered alert threshold.
    /// Uses the notification template engine to render messages.
    /// </summary>
    Task SendAlertAsync(WatchedSite watch, AlertEvaluationResult alertResult, NotificationContext context, CancellationToken ct = default);

    /// <summary>
    /// Sends a test notification to verify settings.
    /// </summary>
    Task SendTestNotificationAsync(NotificationSettings settings, CancellationToken ct = default);
}
