namespace ChangeDetection.Core.Entities;

/// <summary>
/// Notification settings for a watch.
/// </summary>
public class NotificationSettings
{
    /// <summary>
    /// Whether email notifications are enabled.
    /// </summary>
    public bool EmailEnabled { get; set; }
    
    /// <summary>
    /// Email address to send notifications to.
    /// </summary>
    public string? EmailAddress { get; set; }
    
    /// <summary>
    /// Whether webhook notifications are enabled.
    /// </summary>
    public bool WebhookEnabled { get; set; }
    
    /// <summary>
    /// Webhook URL for notifications.
    /// </summary>
    public string? WebhookUrl { get; set; }
    
    /// <summary>
    /// Whether Discord notifications are enabled.
    /// </summary>
    public bool DiscordEnabled { get; set; }
    
    /// <summary>
    /// Discord webhook URL.
    /// </summary>
    public string? DiscordWebhookUrl { get; set; }
    
    /// <summary>
    /// Whether to use LLM to summarize changes in notifications.
    /// </summary>
    public bool UseLlmSummary { get; set; }
    
    /// <summary>
    /// Minimum change importance to trigger notification.
    /// </summary>
    public ChangeImportance MinimumImportance { get; set; } = ChangeImportance.Low;

    /// <summary>
    /// Named notification channels for filter-based routing.
    /// </summary>
    public List<NotificationChannel> Channels { get; set; } = [];

    /// <summary>
    /// Default channel name for notifications not routed by filters.
    /// If null, uses the enabled channels above.
    /// </summary>
    public string? DefaultChannelName { get; set; }
}

/// <summary>
/// A named notification channel for filter-based routing.
/// </summary>
public class NotificationChannel
{
    /// <summary>
    /// Unique name for this channel (e.g., "urgent", "low-priority").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Type of notification channel.
    /// </summary>
    public NotificationChannelType Type { get; set; }

    /// <summary>
    /// Channel-specific configuration.
    /// Examples:
    /// - Email: "address" = email address
    /// - Webhook: "url" = webhook URL
    /// - Discord: "webhookUrl" = Discord webhook URL
    /// </summary>
    public Dictionary<string, string> Config { get; set; } = [];

    /// <summary>
    /// Whether this channel is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// Types of notification channels.
/// </summary>
public enum NotificationChannelType
{
    Email,
    Webhook,
    Discord,
    Browser
}

public enum ChangeImportance
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}
