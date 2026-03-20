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

    /// <summary>How this watch acquires content.</summary>
    public SourceTypeDto SourceType { get; set; } = SourceTypeDto.Url;

    /// <summary>Search configuration (when SourceType is Search).</summary>
    public SearchConfigDto? SearchConfig { get; set; }
}

/// <summary>
/// DTO for detailed watch view.
/// </summary>
public class WatchDetailDto
{
    public string Id { get; set; } = "";
    public required string Url { get; set; }
    public string? Title { get; set; }
    
    /// <summary>
    /// Optional description from LLM when created via natural language.
    /// </summary>
    public string? Description { get; set; }
    
    public string? CssSelector { get; set; }
    public string? XpathSelector { get; set; }
    public List<string> IgnorePatterns { get; set; } = [];
    public TimeSpan CheckInterval { get; set; }
    public DateTime? LastCheck { get; set; }
    public DateTime? NextCheck { get; set; }
    
    /// <summary>
    /// When a change was last detected.
    /// </summary>
    public DateTime? LastChanged { get; set; }
    
    public string Status { get; set; } = "Idle";
    public bool IsEnabled { get; set; } = true;
    public string? LastError { get; set; }
    
    /// <summary>
    /// Number of consecutive failures.
    /// </summary>
    public int ConsecutiveFailures { get; set; }
    
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// When this watch was last updated.
    /// </summary>
    public DateTime UpdatedAt { get; set; }
    
    public FetchSettingsDto? FetchSettings { get; set; }
    public NotificationSettingsDto? NotificationSettings { get; set; }
    public SnapshotDto? LatestSnapshot { get; set; }
    
    // Category information
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? CategoryColor { get; set; }
    
    // Tags with their colors
    public List<TagDto> Tags { get; set; } = [];
    
    /// <summary>
    /// Optional LLM provider override for this specific watch.
    /// </summary>
    public string? LlmProviderOverride { get; set; }
    
    /// <summary>
    /// Whether structured object extraction is enabled.
    /// </summary>
    public bool SchemaEnabled { get; set; }
    
    /// <summary>
    /// Extraction schema configuration.
    /// </summary>
    public ExtractionSchemaDto? Schema { get; set; }
    
    /// <summary>
    /// Filter rules for this watch.
    /// </summary>
    public List<FilterRuleDto> FilterRules { get; set; } = [];
    
    /// <summary>
    /// Schedule settings for check frequency control.
    /// </summary>
    public CheckScheduleSettingsDto ScheduleSettings { get; set; } = new();
    
    /// <summary>
    /// Current average time between detected changes (adaptive mode).
    /// Null if not enough data yet.
    /// </summary>
    public TimeSpan? AverageChangeInterval { get; set; }
    
    /// <summary>
    /// When the check interval was last adjusted (adaptive mode).
    /// </summary>
    public DateTime? LastIntervalAdjustment { get; set; }
    
    /// <summary>
    /// Whether automatic error resolution via LLM is enabled.
    /// </summary>
    public bool AutoErrorResolutionEnabled { get; set; } = true;
    
    /// <summary>
    /// Number of auto-resolution attempts for current error.
    /// </summary>
    public int AutoResolutionAttempts { get; set; }
    
    /// <summary>
    /// Maximum auto-resolution attempts before requiring user intervention.
    /// </summary>
    public int MaxAutoResolutionAttempts { get; set; } = 3;
    
    /// <summary>
    /// Last resolution diagnosis message from LLM.
    /// </summary>
    public string? LastResolutionDiagnosis { get; set; }
    
    /// <summary>
    /// When the last auto-resolution was attempted.
    /// </summary>
    public DateTime? LastResolutionAttempt { get; set; }
    
    /// <summary>
    /// History of selector changes from auto-resolution.
    /// </summary>
    public List<SelectorHistoryEntryDto> SelectorHistory { get; set; } = [];

    /// <summary>How this watch acquires content.</summary>
    public SourceTypeDto SourceType { get; set; } = SourceTypeDto.Url;

    /// <summary>Search configuration (when SourceType is Search).</summary>
    public SearchConfigDto? SearchConfig { get; set; }
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
    
    /// <summary>
    /// Schedule settings for check frequency control.
    /// </summary>
    public CheckScheduleSettingsDto ScheduleSettings { get; set; } = new();
    
    // Category assignment
    public string? CategoryId { get; set; }
    
    // Tags (will be normalized)
    public List<string> Tags { get; set; } = [];
    
    // Tag color overrides (tag name -> hex color)
    public Dictionary<string, string> TagColors { get; set; } = [];

    /// <summary>How this watch acquires content. Defaults to Url.</summary>
    public SourceTypeDto SourceType { get; set; } = SourceTypeDto.Url;

    /// <summary>Search configuration (when SourceType is Search).</summary>
    public SearchConfigDto? SearchConfig { get; set; }
}

/// <summary>
/// DTO for fetch settings.
/// </summary>
public class FetchSettingsDto
{
    public bool UseJavaScript { get; set; }
    public string? WaitForSelector { get; set; }
    public int WaitTimeMs { get; set; }
    
    /// <summary>
    /// Legacy screenshot flag. Use <see cref="Screenshot"/> for detailed settings.
    /// </summary>
    public bool CaptureScreenshot { get; set; }
    
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
    
    /// <summary>
    /// Optional proxy URL.
    /// </summary>
    public string? ProxyUrl { get; set; }
    
    /// <summary>
    /// Custom user agent string.
    /// </summary>
    public string? UserAgent { get; set; }
    
    /// <summary>
    /// Viewport width for screenshot capture.
    /// </summary>
    public int ViewportWidth { get; set; } = 1920;
    
    /// <summary>
    /// Viewport height for screenshot capture.
    /// </summary>
    public int ViewportHeight { get; set; } = 1080;

    /// <summary>
    /// Detailed screenshot capture settings.
    /// </summary>
    public ScreenshotSettingsDto Screenshot { get; set; } = new();
}

/// <summary>
/// DTO for screenshot capture settings.
/// </summary>
public class ScreenshotSettingsDto
{
    /// <summary>
    /// Screenshot capture mode: None, Viewport, FullPage, ElementOnly, FullPageAndElement.
    /// </summary>
    public string Mode { get; set; } = "None";

    /// <summary>
    /// Whether to capture a screenshot on every check.
    /// </summary>
    public bool CaptureOnEveryCheck { get; set; } = true;

    /// <summary>
    /// Whether to capture a screenshot when a change is detected.
    /// </summary>
    public bool CaptureOnChange { get; set; } = true;

    /// <summary>
    /// Image quality for JPEG format (1-100).
    /// </summary>
    public int JpegQuality { get; set; } = 85;

    /// <summary>
    /// Image format: Png or Jpeg.
    /// </summary>
    public string Format { get; set; } = "Png";

    /// <summary>
    /// Scale factor for screenshots (0.1 to 2.0).
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>
    /// Padding in pixels around element screenshots.
    /// </summary>
    public int ElementPadding { get; set; } = 10;

    /// <summary>
    /// Whether to highlight the monitored element in screenshots.
    /// </summary>
    public bool HighlightElement { get; set; } = true;

    /// <summary>
    /// Color of the element highlight border (hex format).
    /// </summary>
    public string HighlightColor { get; set; } = "#FF6B6B";

    /// <summary>
    /// Width of the element highlight border in pixels.
    /// </summary>
    public int HighlightBorderWidth { get; set; } = 3;

    /// <summary>
    /// Maximum width for screenshots (null = no limit).
    /// </summary>
    public int? MaxWidth { get; set; }

    /// <summary>
    /// Maximum height for screenshots (null = no limit).
    /// </summary>
    public int? MaxHeight { get; set; }
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
    
    /// <summary>
    /// Whether to use LLM to summarize changes in notifications.
    /// </summary>
    public bool UseLlmSummary { get; set; }
    
    /// <summary>
    /// Named notification channels for filter-based routing.
    /// </summary>
    public List<NotificationChannelDto> Channels { get; set; } = [];
    
    /// <summary>
    /// Default channel name for notifications not routed by filters.
    /// </summary>
    public string? DefaultChannelName { get; set; }
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
    
    /// <summary>
    /// Path to the full page or viewport screenshot.
    /// </summary>
    public string? ScreenshotPath { get; set; }

    /// <summary>
    /// Path to the element-specific screenshot.
    /// </summary>
    public string? ElementScreenshotPath { get; set; }

    /// <summary>
    /// Bounding box of the monitored element in the screenshot.
    /// </summary>
    public ElementBoundingBoxDto? ElementBoundingBox { get; set; }
    
    /// <summary>
    /// Extracted structured objects when schema extraction is enabled.
    /// </summary>
    public List<ExtractedObjectDto>? ExtractedObjects { get; set; }
}

/// <summary>
/// DTO for element bounding box coordinates.
/// </summary>
public class ElementBoundingBoxDto
{
    /// <summary>
    /// X coordinate of the element's top-left corner.
    /// </summary>
    public double X { get; set; }

    /// <summary>
    /// Y coordinate of the element's top-left corner.
    /// </summary>
    public double Y { get; set; }

    /// <summary>
    /// Width of the element in pixels.
    /// </summary>
    public double Width { get; set; }

    /// <summary>
    /// Height of the element in pixels.
    /// </summary>
    public double Height { get; set; }
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

    // ========== Rich Field Metadata ==========

    /// <summary>
    /// Currency code for Currency fields (e.g., "USD", "EUR", "GBP").
    /// </summary>
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Number of decimal places for Number/Currency fields.
    /// </summary>
    public int? DecimalPlaces { get; set; }

    /// <summary>
    /// Custom format string for display.
    /// </summary>
    public string? FormatString { get; set; }

    /// <summary>
    /// Whether this field is tracked for historical charting.
    /// </summary>
    public bool TrackHistory { get; set; }

    /// <summary>
    /// Unit label for display (e.g., "kg", "miles").
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Possible values for Status type fields.
    /// </summary>
    public List<string> AllowedValues { get; set; } = [];

    // ========== Numeric Tracking Settings (for stock/price monitoring) ==========

    /// <summary>
    /// Baseline value for comparison.
    /// </summary>
    public double? BaselineValue { get; set; }

    /// <summary>
    /// When the baseline was set.
    /// </summary>
    public DateTime? BaselineSetAt { get; set; }

    /// <summary>
    /// Tracking mode: Absolute, Percentage, Both.
    /// </summary>
    public string TrackingMode { get; set; } = "Both";

    /// <summary>
    /// Minimum absolute change to consider significant.
    /// </summary>
    public double? MinSignificantChange { get; set; }

    /// <summary>
    /// Minimum percentage change to consider significant.
    /// </summary>
    public double? MinSignificantChangePercent { get; set; }

    /// <summary>
    /// Alert thresholds for this field.
    /// </summary>
    public List<FieldAlertThresholdDto> AlertThresholds { get; set; } = [];

    /// <summary>
    /// Whether to calculate trends.
    /// </summary>
    public bool CalculateTrend { get; set; } = true;

    /// <summary>
    /// Number of values for trend calculation.
    /// </summary>
    public int TrendWindowSize { get; set; } = 10;

    /// <summary>
    /// Whether to track min/max.
    /// </summary>
    public bool TrackMinMax { get; set; } = true;

    /// <summary>
    /// Historical minimum observed.
    /// </summary>
    public double? HistoricalMin { get; set; }

    /// <summary>
    /// When the historical minimum was observed.
    /// </summary>
    public DateTime? HistoricalMinAt { get; set; }

    /// <summary>
    /// Historical maximum observed.
    /// </summary>
    public double? HistoricalMax { get; set; }

    /// <summary>
    /// When the historical maximum was observed.
    /// </summary>
    public DateTime? HistoricalMaxAt { get; set; }
}

/// <summary>
/// DTO for field alert threshold configuration.
/// </summary>
public class FieldAlertThresholdDto
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Alert name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Condition type: DropsBelow, RisesAbove, ChangesBy, ChangesByPercent, etc.
    /// </summary>
    public string ConditionType { get; set; } = "DropsBelow";

    /// <summary>
    /// Primary threshold value.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Secondary value for range conditions.
    /// </summary>
    public double? SecondaryValue { get; set; }

    /// <summary>
    /// Whether the alert is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether this is a one-time alert.
    /// </summary>
    public bool OneTime { get; set; }

    /// <summary>
    /// Cooldown between alerts in minutes.
    /// </summary>
    public int? CooldownMinutes { get; set; }

    /// <summary>
    /// When last triggered.
    /// </summary>
    public DateTime? LastTriggeredAt { get; set; }

    /// <summary>
    /// Number of times triggered.
    /// </summary>
    public int TriggerCount { get; set; }

    /// <summary>
    /// Custom notification message.
    /// </summary>
    public string? NotificationTemplate { get; set; }

    /// <summary>
    /// Importance level when triggered.
    /// </summary>
    public string? ImportanceOverride { get; set; }
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
/// DTO for extracted object modification comparison.
/// </summary>
public class ExtractedObjectModificationDto
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

// ============================================================================
// Schedule Settings DTOs
// ============================================================================

/// <summary>
/// DTO for check schedule settings.
/// </summary>
public class CheckScheduleSettingsDto
{
    /// <summary>
    /// The scheduling mode: "Fixed" or "Adaptive".
    /// </summary>
    public string Mode { get; set; } = "Fixed";
    
    /// <summary>
    /// Base interval for fixed mode, or starting point for adaptive mode.
    /// </summary>
    public TimeSpan BaseInterval { get; set; } = TimeSpan.FromHours(1);
    
    /// <summary>
    /// Minimum interval between checks (adaptive mode only).
    /// </summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromHours(1);
    
    /// <summary>
    /// Maximum interval between checks (adaptive mode only).
    /// </summary>
    public TimeSpan MaxInterval { get; set; } = TimeSpan.FromDays(7);
    
    /// <summary>
    /// How many times faster to check than the content changes (adaptive mode).
    /// Default: 3 means check 3x as often as changes occur.
    /// </summary>
    public double FrequencyMultiplier { get; set; } = 3.0;
}

// ============================================================================
// Error Resolution DTOs
// ============================================================================

/// <summary>
/// Historical record of a selector change.
/// </summary>
public class SelectorHistoryEntryDto
{
    /// <summary>
    /// When the selector was changed.
    /// </summary>
    public DateTime ChangedAt { get; set; }
    
    /// <summary>
    /// Previous CSS selector value.
    /// </summary>
    public string? PreviousCssSelector { get; set; }
    
    /// <summary>
    /// Previous XPath selector value.
    /// </summary>
    public string? PreviousXPathSelector { get; set; }
    
    /// <summary>
    /// Reason for the change.
    /// </summary>
    public string? ChangeReason { get; set; }
    
    /// <summary>
    /// LLM diagnosis that triggered the change.
    /// </summary>
    public string? Diagnosis { get; set; }
    
    /// <summary>
    /// Confidence score of the auto-resolution (0-1).
    /// </summary>
    public float? Confidence { get; set; }
}

/// <summary>
/// DTO for error resolution result.
/// </summary>
public class ErrorResolutionResultDto
{
    /// <summary>
    /// Whether the resolution was successful.
    /// </summary>
    public bool IsResolved { get; set; }
    
    /// <summary>
    /// Whether a fix was applied automatically.
    /// </summary>
    public bool AutoFixApplied { get; set; }
    
    /// <summary>
    /// Diagnosis of the problem.
    /// </summary>
    public required string Diagnosis { get; set; }
    
    /// <summary>
    /// Suggested action for the user.
    /// </summary>
    public string? SuggestedAction { get; set; }
    
    /// <summary>
    /// New CSS selector if the fix involves a selector change.
    /// </summary>
    public string? NewCssSelector { get; set; }
    
    /// <summary>
    /// New XPath selector if the fix involves a selector change.
    /// </summary>
    public string? NewXPathSelector { get; set; }
    
    /// <summary>
    /// Confidence in the fix (0-1).
    /// </summary>
    public float Confidence { get; set; }
    
    /// <summary>
    /// LLM reasoning for the diagnosis.
    /// </summary>
    public string? Reasoning { get; set; }
    
    /// <summary>
    /// Whether user approval is recommended.
    /// </summary>
    public bool RequiresUserApproval { get; set; }
    
    /// <summary>
    /// Sample of extracted content from new selector.
    /// </summary>
    public string? ExtractedSample { get; set; }
    
    /// <summary>
    /// Number of matches the new selector found.
    /// </summary>
    public int MatchCount { get; set; }
    
    /// <summary>
    /// Whether the website structure has fundamentally changed.
    /// </summary>
    public bool MajorStructureChange { get; set; }
}

// ============================================================================
// Bulk Edit DTOs
// ============================================================================

/// <summary>
/// DTO for bulk operations on multiple watches.
/// </summary>
public class BulkWatchOperationDto
{
    /// <summary>
    /// IDs of watches to operate on.
    /// </summary>
    public required List<string> WatchIds { get; set; }
}

/// <summary>
/// DTO for bulk editing multiple watches.
/// </summary>
public class BulkWatchEditDto : BulkWatchOperationDto
{
    /// <summary>
    /// Tags to add to all selected watches.
    /// </summary>
    public List<string>? AddTags { get; set; }

    /// <summary>
    /// Tags to remove from all selected watches.
    /// </summary>
    public List<string>? RemoveTags { get; set; }

    /// <summary>
    /// Category ID to set (null = don't change, empty string = remove category).
    /// </summary>
    public string? CategoryId { get; set; }

    /// <summary>
    /// Whether to change the category (allows setting null/remove).
    /// </summary>
    public bool ChangeCategoryId { get; set; }

    /// <summary>
    /// Check interval to set (null = don't change).
    /// </summary>
    public TimeSpan? CheckInterval { get; set; }

    /// <summary>
    /// Whether to enable JavaScript fetching (null = don't change).
    /// </summary>
    public bool? UseJavaScript { get; set; }

    /// <summary>
    /// Whether to enable notifications (null = don't change).
    /// </summary>
    public bool? NotificationsEnabled { get; set; }
}

/// <summary>
/// Result of a bulk operation.
/// </summary>
public class BulkOperationResultDto
{
    /// <summary>
    /// Number of watches successfully updated.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of watches that failed to update.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// IDs of watches that failed to update with error messages.
    /// </summary>
    public Dictionary<string, string> Failures { get; set; } = [];
}

/// <summary>
/// How a watch acquires its content.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SourceTypeDto
{
    Url = 0,
    Search = 1
}

/// <summary>
/// DTO for search-based watch configuration.
/// </summary>
public class SearchConfigDto
{
    /// <summary>The search query to execute periodically.</summary>
    public required string Query { get; set; }

    /// <summary>Search provider ID (e.g., "searxng"). Null uses the default.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Search category (e.g., "general", "news").</summary>
    public string? Category { get; set; }

    /// <summary>Language filter (e.g., "en", "cs").</summary>
    public string? Language { get; set; }

    /// <summary>Time range filter (e.g., "day", "week", "month").</summary>
    public string? TimeRange { get; set; }

    /// <summary>Maximum results per check.</summary>
    public int MaxResults { get; set; } = 20;

    /// <summary>Rules for auto-promoting matching search results to standalone watches.</summary>
    public List<AutoPromotionRuleDto> AutoPromotionRules { get; set; } = [];
}

public class AutoPromotionRuleDto
{
    /// <summary>Glob pattern to match against result URLs (e.g., "*github.com/*/releases*").</summary>
    public string? UrlPattern { get; set; }

    /// <summary>Substring to match in result titles (case-insensitive).</summary>
    public string? TitleContains { get; set; }

    /// <summary>Whether this rule is active.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Optional CSS selector for the promoted watch.</summary>
    public string? CssSelector { get; set; }
}

