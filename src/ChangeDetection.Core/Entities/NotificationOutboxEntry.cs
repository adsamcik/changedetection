using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a pending or sent notification in the outbox.
/// Ensures notifications survive crashes and can be retried on failure.
/// </summary>
public class NotificationOutboxEntry : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The ID of the user who owns this notification.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;

    /// <summary>
    /// The watch that triggered this notification.
    /// </summary>
    public Guid WatchedSiteId { get; set; }

    /// <summary>
    /// The change event that triggered this notification (if applicable).
    /// </summary>
    public Guid? ChangeEventId { get; set; }

    /// <summary>
    /// Type of notification: Email, Webhook, Discord, Alert.
    /// </summary>
    public required NotificationType NotificationType { get; set; }

    /// <summary>
    /// Destination address (email, URL, etc.).
    /// </summary>
    public required string Destination { get; set; }

    /// <summary>
    /// JSON-serialized payload containing all data needed to send the notification.
    /// </summary>
    public required string PayloadJson { get; set; }

    /// <summary>
    /// Current status of this notification.
    /// </summary>
    public NotificationStatus Status { get; set; } = NotificationStatus.Pending;

    /// <summary>
    /// When the notification was queued.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When processing started (null if not yet attempted).
    /// </summary>
    public DateTime? ProcessingStartedAt { get; set; }

    /// <summary>
    /// When the notification was successfully sent.
    /// </summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// Number of delivery attempts.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Maximum retry attempts before marking as failed permanently.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// When to retry next (for exponential backoff).
    /// </summary>
    public DateTime? NextRetryAt { get; set; }

    /// <summary>
    /// Last error message if sending failed.
    /// </summary>
    public string? LastError { get; set; }
}

/// <summary>
/// Types of notifications that can be sent.
/// </summary>
public enum NotificationType
{
    Email,
    Webhook,
    Discord,
    Alert
}

/// <summary>
/// Status of a notification in the outbox.
/// </summary>
public enum NotificationStatus
{
    /// <summary>
    /// Notification is queued and ready to send.
    /// </summary>
    Pending,

    /// <summary>
    /// Notification is currently being processed.
    /// </summary>
    Processing,

    /// <summary>
    /// Notification was sent successfully.
    /// </summary>
    Sent,

    /// <summary>
    /// Notification failed and is waiting for retry.
    /// </summary>
    RetryPending,

    /// <summary>
    /// Notification failed permanently after max retries.
    /// </summary>
    Failed
}
