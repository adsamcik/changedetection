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
}

public enum ChangeImportance
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}
