namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a website being monitored for changes.
/// </summary>
public class WatchedSite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The URL to monitor.
    /// </summary>
    public required string Url { get; set; }
    
    /// <summary>
    /// A friendly name for the watch.
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// Optional CSS selector to target specific content.
    /// </summary>
    public string? CssSelector { get; set; }
    
    /// <summary>
    /// Optional XPath selector to target specific content.
    /// </summary>
    public string? XPathSelector { get; set; }
    
    /// <summary>
    /// How often to check for changes.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// When the site was last checked.
    /// </summary>
    public DateTime? LastChecked { get; set; }
    
    /// <summary>
    /// When a change was last detected.
    /// </summary>
    public DateTime? LastChanged { get; set; }
    
    /// <summary>
    /// Hash of the last captured content for quick comparison.
    /// </summary>
    public string? LastContentHash { get; set; }
    
    /// <summary>
    /// Current status of the watch.
    /// </summary>
    public WatchStatus Status { get; set; } = WatchStatus.Active;
    
    /// <summary>
    /// Whether the watch is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
    
    /// <summary>
    /// Category ID for organizing watches.
    /// If null, the watch belongs to the default "Uncategorized" category.
    /// </summary>
    public Guid? CategoryId { get; set; }
    
    /// <summary>
    /// LLM-generated tags for organizing and searching watches.
    /// Tags are automatically normalized (lowercase, trimmed, deduped).
    /// </summary>
    public List<string> Tags { get; set; } = [];
    
    /// <summary>
    /// User-overridden colors for specific tags (tag name -> hex color).
    /// If a tag is not in this dictionary, its color is auto-generated from a hash.
    /// </summary>
    public Dictionary<string, string> TagColors { get; set; } = [];
    
    /// <summary>
    /// Regex patterns to ignore when comparing content (e.g., timestamps, session IDs).
    /// </summary>
    public List<string> IgnorePatterns { get; set; } = [];
    
    /// <summary>
    /// Notification settings for this watch.
    /// </summary>
    public NotificationSettings Notifications { get; set; } = new();
    
    /// <summary>
    /// Settings for fetching content.
    /// </summary>
    public FetchSettings FetchSettings { get; set; } = new();
    
    /// <summary>
    /// Optional LLM provider override for this specific watch.
    /// If null, uses global settings.
    /// </summary>
    public string? LlmProviderOverride { get; set; }
    
    /// <summary>
    /// When this watch was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// When this watch was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Optional description from LLM when created via natural language.
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// Last error message if status is Error.
    /// </summary>
    public string? LastError { get; set; }
    
    /// <summary>
    /// Number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Whether structured object extraction is enabled for this watch.
    /// When enabled, pages are parsed into objects using the schema.
    /// </summary>
    public bool SchemaEnabled { get; set; }

    /// <summary>
    /// Schema for extracting structured objects from the page.
    /// Null if schema extraction is not configured.
    /// </summary>
    public ExtractionSchema? Schema { get; set; }

    /// <summary>
    /// Filter rules applied to extracted objects and changes.
    /// Rules are evaluated in priority order.
    /// </summary>
    public List<FilterRule> FilterRules { get; set; } = [];
}

public enum WatchStatus
{
    Active,
    Paused,
    Checking,
    Error
}
