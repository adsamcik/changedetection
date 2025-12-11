namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for listing watches in a dashboard view.
/// </summary>
public class WatchListItemDto
{
    public string Id { get; set; } = "";
    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? CssSelector { get; set; }
    public TimeSpan CheckInterval { get; set; }
    public DateTime? LastCheck { get; set; }
    public string Status { get; set; } = "Idle";
    public bool IsEnabled { get; set; } = true;
    public int ChangeCount { get; set; }
    public bool HasRecentChanges { get; set; }
    
    /// <summary>
    /// Last error message if status is Error.
    /// </summary>
    public string? LastError { get; set; }
    
    /// <summary>
    /// ID of the most recent change for linking.
    /// </summary>
    public string? LatestChangeId { get; set; }
    
    /// <summary>
    /// Summary of the most recent change for preview display.
    /// </summary>
    public string? LatestChangeSummary { get; set; }
    
    /// <summary>
    /// When the most recent change was detected.
    /// </summary>
    public DateTime? LatestChangeAt { get; set; }
    
    /// <summary>
    /// Number of unviewed changes for this watch.
    /// </summary>
    public int UnviewedChangeCount { get; set; }
    
    // Category information
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategoryColor { get; set; }
    
    // Tags with their colors
    public List<TagDto> Tags { get; set; } = [];
}

/// <summary>
/// DTO for detailed watch view.
/// </summary>
public class WatchDetailDto
{
    public string Id { get; set; } = "";
    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? CssSelector { get; set; }
    public string? XpathSelector { get; set; }
    public List<string> IgnorePatterns { get; set; } = [];
    public TimeSpan CheckInterval { get; set; }
    public DateTime? LastCheck { get; set; }
    public DateTime? NextCheck { get; set; }
    public string Status { get; set; } = "Idle";
    public bool IsEnabled { get; set; } = true;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public FetchSettingsDto? FetchSettings { get; set; }
    public NotificationSettingsDto? NotificationSettings { get; set; }
    public SnapshotDto? LatestSnapshot { get; set; }
    
    // Category information
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategoryColor { get; set; }
    
    // Tags with their colors
    public List<TagDto> Tags { get; set; } = [];
}

/// <summary>
/// DTO for creating a new watch.
/// </summary>
public class WatchCreateDto
{
    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? CssSelector { get; set; }
    public string? XpathSelector { get; set; }
    public List<string> IgnorePatterns { get; set; } = [];
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
    public bool IsEnabled { get; set; } = true;
    public FetchSettingsDto FetchSettings { get; set; } = new();
    public NotificationSettingsDto NotificationSettings { get; set; } = new();
    
    // Category assignment
    public string? CategoryId { get; set; }
    
    // Tags (will be normalized)
    public List<string> Tags { get; set; } = [];
    
    // Tag color overrides (tag name -> hex color)
    public Dictionary<string, string> TagColors { get; set; } = [];
}

/// <summary>
/// DTO for fetch settings.
/// </summary>
public class FetchSettingsDto
{
    public bool UseJavaScript { get; set; }
    public string? WaitForSelector { get; set; }
    public int WaitTimeMs { get; set; }
    public bool CaptureScreenshot { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
}

/// <summary>
/// DTO for notification settings.
/// </summary>
public class NotificationSettingsDto
{
    public bool EmailEnabled { get; set; }
    public List<string> EmailRecipients { get; set; } = new();
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
    public bool DiscordEnabled { get; set; }
    public string? DiscordWebhookUrl { get; set; }
    public string MinimumImportanceToNotify { get; set; } = "Medium";
}

/// <summary>
/// DTO for content snapshots.
/// </summary>
public class SnapshotDto
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public int ContentLength { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public string? ScreenshotPath { get; set; }
}

/// <summary>
/// DTO for a tag with its display color.
/// </summary>
public class TagDto
{
    public required string Name { get; set; }
    public required string Color { get; set; }
    public bool IsColorOverridden { get; set; }
}

// ============================================================================
// Schema and Filter DTOs
// ============================================================================

/// <summary>
/// DTO for extraction schema configuration.
/// </summary>
public class ExtractionSchemaDto
{
    /// <summary>
    /// CSS or XPath selector for the repeating item container.
    /// </summary>
    public required string ItemSelector { get; set; }

    /// <summary>
    /// Fields to extract from each item.
    /// </summary>
    public List<SchemaFieldDto> Fields { get; set; } = [];

    /// <summary>
    /// Names of fields that uniquely identify an object.
    /// </summary>
    public List<string> IdentityFieldNames { get; set; } = [];

    /// <summary>
    /// Schema version number.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Diff computation settings.
    /// </summary>
    public ObjectDiffSettingsDto DiffSettings { get; set; } = new();
}

/// <summary>
/// DTO for a schema field.
/// </summary>
public class SchemaFieldDto
{
    /// <summary>
    /// Human-readable field name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Data type: String, Date, Url, Number, Image, Html.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Selector relative to item container.
    /// </summary>
    public required string Selector { get; set; }

    /// <summary>
    /// Whether the field is required.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Whether this field is part of the object's identity.
    /// </summary>
    public bool IsIdentityField { get; set; }

    /// <summary>
    /// Sample value from schema discovery.
    /// </summary>
    public string? SampleValue { get; set; }

    /// <summary>
    /// LLM confidence score (0-1).
    /// </summary>
    public double? Confidence { get; set; }
}

/// <summary>
/// DTO for object diff settings.
/// </summary>
public class ObjectDiffSettingsDto
{
    /// <summary>
    /// Diff granularity: ItemsOnly, FieldLevel, Both.
    /// </summary>
    public string Granularity { get; set; } = "Both";

    /// <summary>
    /// Whether to use LLM for importance scoring.
    /// </summary>
    public bool EnableImportanceScoring { get; set; } = true;

    /// <summary>
    /// Default importance when LLM scoring disabled.
    /// </summary>
    public string DefaultImportance { get; set; } = "Medium";
}

/// <summary>
/// DTO for filter rules.
/// </summary>
public class FilterRuleDto
{
    /// <summary>
    /// Rule ID.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// Rule name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Filter conditions.
    /// </summary>
    public List<FilterConditionDto> Conditions { get; set; } = [];

    /// <summary>
    /// Logic: And or Or.
    /// </summary>
    public string Logic { get; set; } = "And";

    /// <summary>
    /// Actions to execute when matched.
    /// </summary>
    public List<FilterActionDto> Actions { get; set; } = [];

    /// <summary>
    /// Whether the rule is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Rule priority (higher = first).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Stop processing further rules after match.
    /// </summary>
    public bool StopProcessing { get; set; }
}

/// <summary>
/// DTO for filter condition.
/// </summary>
public class FilterConditionDto
{
    /// <summary>
    /// Field name to evaluate.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Operator: Equals, Contains, GreaterThan, etc.
    /// </summary>
    public required string Operator { get; set; }

    /// <summary>
    /// Value to compare against.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Whether to negate the condition.
    /// </summary>
    public bool Negate { get; set; }
}

/// <summary>
/// DTO for filter action.
/// </summary>
public class FilterActionDto
{
    /// <summary>
    /// Action type: SuppressNotification, AddTag, SetImportance, RouteToChannel.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Action parameters.
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = [];
}

/// <summary>
/// DTO for exporting schema + filter rules as JSON.
/// </summary>
public class SchemaExportDto
{
    /// <summary>
    /// Export format version.
    /// </summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// When the export was created.
    /// </summary>
    public DateTimeOffset ExportedAt { get; set; }

    /// <summary>
    /// The extraction schema.
    /// </summary>
    public ExtractionSchemaDto? Schema { get; set; }

    /// <summary>
    /// Filter rules.
    /// </summary>
    public List<FilterRuleDto> FilterRules { get; set; } = [];
}

/// <summary>
/// DTO for notification channel configuration.
/// </summary>
public class NotificationChannelDto
{
    /// <summary>
    /// Channel name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Channel type: Email, Webhook, Discord.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Channel-specific configuration.
    /// </summary>
    public Dictionary<string, string> Config { get; set; } = [];

    /// <summary>
    /// Whether the channel is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>
/// DTO for object modification.
/// </summary>
public class ObjectModificationDto
{
    /// <summary>
    /// Identity key of modified object.
    /// </summary>
    public required string IdentityKey { get; set; }

    /// <summary>
    /// Object before changes.
    /// </summary>
    public required ExtractedObjectDto PreviousObject { get; set; }

    /// <summary>
    /// Object after changes.
    /// </summary>
    public required ExtractedObjectDto CurrentObject { get; set; }

    /// <summary>
    /// Individual field changes.
    /// </summary>
    public List<FieldChangeDto> FieldChanges { get; set; } = [];
}
