using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a website being monitored for changes.
/// </summary>
public class WatchedSite : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The ID of the user who owns this watch.
    /// Guid.Empty represents the default single-user mode owner.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;
    
    /// <summary>
    /// The URL to monitor. Required for SourceType.Url watches.
    /// For SourceType.Search, this may be empty or contain a display label.
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// How this watch acquires content. Defaults to Url (standard HTTP fetching).
    /// </summary>
    public SourceType SourceType { get; set; } = SourceType.Url;

    /// <summary>
    /// Configuration for search-based watches (SourceType.Search).
    /// Null for URL-based watches.
    /// </summary>
    public SearchConfig? SearchConfig { get; set; }

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
    /// For adaptive mode, this is the current calculated interval.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// Schedule settings controlling fixed vs adaptive check intervals.
    /// </summary>
    public CheckScheduleSettings ScheduleSettings { get; set; } = new();
    
    /// <summary>
    /// Exponential moving average of time between detected changes.
    /// Used to calculate adaptive check intervals.
    /// Null if not enough data has been collected yet.
    /// </summary>
    public TimeSpan? AverageChangeInterval { get; set; }
    
    /// <summary>
    /// When the check interval was last adjusted (adaptive mode only).
    /// </summary>
    public DateTime? LastIntervalAdjustment { get; set; }
    
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
    /// Optional membership in a WatchGroup for aggregate monitoring.
    /// </summary>
    public Guid? GroupId { get; set; }
    
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
    /// Monthly LLM budget in USD. Null means unlimited.
    /// When exceeded, LLM blocks are skipped with a degraded flag.
    /// </summary>
    public decimal? MonthlyLlmBudget { get; set; }
    
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
    
    /// <summary>
    /// Whether automatic error resolution via LLM is enabled.
    /// When enabled, the system will attempt to fix selector issues automatically.
    /// </summary>
    public bool AutoErrorResolutionEnabled { get; set; } = true;
    
    /// <summary>
    /// Number of auto-resolution attempts made for the current error.
    /// Reset to 0 when a check succeeds.
    /// </summary>
    public int AutoResolutionAttempts { get; set; }
    
    /// <summary>
    /// Maximum number of auto-resolution attempts before requiring user intervention.
    /// </summary>
    public int MaxAutoResolutionAttempts { get; set; } = 3;
    
    /// <summary>
    /// Last resolution diagnosis message.
    /// </summary>
    public string? LastResolutionDiagnosis { get; set; }
    
    /// <summary>
    /// When the last auto-resolution was attempted.
    /// </summary>
    public DateTime? LastResolutionAttempt { get; set; }
    
    /// <summary>
    /// History of selector changes from auto-resolution.
    /// Stores previous selectors for rollback capability.
    /// </summary>
    public List<SelectorHistoryEntry> SelectorHistory { get; set; } = [];

    /// <summary>
    /// Settings for LLM-powered content analysis and enrichment.
    /// </summary>
    public LlmAnalysisSettings AnalysisSettings { get; set; } = new();

    /// <summary>
    /// User's original intent when setting up the watch.
    /// Used for relevance scoring of changes.
    /// </summary>
    public string? UserIntent { get; set; }

    /// <summary>
    /// JSON-serialized pipeline definition for the composable block system.
    /// Null for legacy watches that haven't been migrated.
    /// </summary>
    public string? PipelineDefinitionJson { get; set; }

    /// <summary>
    /// Per-watch retention override in days. Null uses global default.
    /// Capped by AppSettings.MaxRetentionDays.
    /// </summary>
    public int? RetentionDays { get; set; }

    /// <summary>
    /// Whether the user acknowledged the robots.txt status.
    /// </summary>
    public bool RobotsTxtAcknowledged { get; set; }

    /// <summary>
    /// Cached robots.txt compliance status for this watch's URL.
    /// </summary>
    public RobotsTxtStatus? RobotsTxtStatus { get; set; }

    /// <summary>
    /// HTML snapshot captured at setup time, used by Layer 2 auto-healing
    /// to compare against current page structure.
    /// </summary>
    public string? SetupTimeHtml { get; set; }
}

/// <summary>
/// Settings for LLM-powered analysis and enrichment during ingestion.
/// </summary>
public class LlmAnalysisSettings
{
    /// <summary>
    /// Whether to enable LLM-powered semantic analysis of detected changes.
    /// </summary>
    public bool EnableChangeAnalysis { get; set; } = true;

    /// <summary>
    /// Whether to enable LLM-powered content enrichment during fetch.
    /// </summary>
    public bool EnableContentEnrichment { get; set; }

    /// <summary>
    /// Whether to generate semantic summaries of changes.
    /// </summary>
    public bool GenerateSemanticSummary { get; set; } = true;

    /// <summary>
    /// Whether to calculate relevance scores based on user intent.
    /// </summary>
    public bool CalculateRelevance { get; set; } = true;

    /// <summary>
    /// Whether to extract entities from content and changes.
    /// </summary>
    public bool ExtractEntities { get; set; } = true;

    /// <summary>
    /// Whether to analyze sentiment changes.
    /// </summary>
    public bool AnalyzeSentiment { get; set; }

    /// <summary>
    /// Whether to detect anomalies by comparing against historical patterns.
    /// Requires sufficient historical data.
    /// </summary>
    public bool DetectAnomalies { get; set; }

    /// <summary>
    /// Whether to categorize changes semantically.
    /// </summary>
    public bool CategorizeChanges { get; set; } = true;

    /// <summary>
    /// Whether to generate suggested actions based on changes.
    /// </summary>
    public bool GenerateSuggestedActions { get; set; }

    /// <summary>
    /// Minimum relevance score (0-1) to trigger notifications.
    /// If set, low-relevance changes won't trigger notifications.
    /// </summary>
    public float? MinRelevanceForNotification { get; set; }

    /// <summary>
    /// Whether to only notify on high-importance changes when LLM analysis is enabled.
    /// </summary>
    public bool OnlyNotifyHighImportance { get; set; }

    // ========== Deduplication Settings ==========

    /// <summary>
    /// Whether to enable semantic deduplication using content fingerprinting.
    /// Prevents creating change events for semantically identical content.
    /// </summary>
    public bool EnableSemanticDeduplication { get; set; }

    /// <summary>
    /// Similarity threshold (0.0 to 1.0) for semantic deduplication.
    /// Content with similarity above this threshold is considered a duplicate.
    /// Default is 0.95 (95% similar).
    /// </summary>
    public float SemanticSimilarityThreshold { get; set; } = 0.95f;
}

/// <summary>
/// Historical record of a selector change.
/// </summary>
public class SelectorHistoryEntry
{
    /// <summary>
    /// When the selector was changed.
    /// </summary>
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Previous CSS selector value.
    /// </summary>
    public string? PreviousCssSelector { get; set; }
    
    /// <summary>
    /// Previous XPath selector value.
    /// </summary>
    public string? PreviousXPathSelector { get; set; }
    
    /// <summary>
    /// Reason for the change (e.g., "Auto-resolution", "User update").
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

public enum WatchStatus
{
    Active,
    Paused,
    Checking,
    Error
}
