namespace ChangeDetection.Core.Entities;

/// <summary>
/// Defines the schema for extracting structured objects from a webpage.
/// Used for list-type content like events, products, articles, etc.
/// </summary>
public class ExtractionSchema
{
    /// <summary>
    /// CSS or XPath selector that identifies the repeating item container.
    /// Each match represents one object to extract.
    /// </summary>
    public required string ItemSelector { get; set; }

    /// <summary>
    /// Fields to extract from each item.
    /// </summary>
    public List<SchemaField> Fields { get; set; } = [];

    /// <summary>
    /// Names of fields that together uniquely identify an object.
    /// Used for diff matching to detect added/removed/modified items.
    /// LLM-inferred during discovery, user-overridable.
    /// </summary>
    public List<string> IdentityFieldNames { get; set; } = [];

    /// <summary>
    /// Schema version, incremented when schema is modified.
    /// Used to detect schema drift.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// When this schema was discovered/created.
    /// </summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Settings for how object diffs are computed and reported.
    /// </summary>
    public ObjectDiffSettings DiffSettings { get; set; } = new();
}

/// <summary>
/// Defines a single field within an extraction schema.
/// </summary>
public class SchemaField
{
    /// <summary>
    /// Human-readable name for this field (e.g., "Event Title", "Price").
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The data type of this field.
    /// </summary>
    public FieldType Type { get; set; } = FieldType.String;

    /// <summary>
    /// CSS or XPath selector relative to the item container.
    /// </summary>
    public required string Selector { get; set; }

    /// <summary>
    /// Whether this field is required for a valid extraction.
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Whether this field is part of the object's identity for diff matching.
    /// </summary>
    public bool IsIdentityField { get; set; }

    /// <summary>
    /// Sample value extracted during schema discovery (for preview).
    /// </summary>
    public string? SampleValue { get; set; }

    /// <summary>
    /// Confidence score from LLM during discovery (0-1).
    /// </summary>
    public float? Confidence { get; set; }

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
    /// Custom format string for display (e.g., "C2" for currency, "P1" for percentage).
    /// </summary>
    public string? FormatString { get; set; }

    /// <summary>
    /// Whether this field should be tracked for historical charting.
    /// Enabled by default for Number, Currency, and Percentage types.
    /// </summary>
    public bool TrackHistory { get; set; }

    /// <summary>
    /// Unit label for display (e.g., "kg", "miles", "items").
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Possible values for Status/Enum type fields.
    /// </summary>
    public List<string> AllowedValues { get; set; } = [];

    // ========== Numeric Tracking Settings (for stock/price monitoring) ==========

    /// <summary>
    /// Baseline value for comparison (e.g., original price, initial stock level).
    /// Used to calculate percentage changes from a reference point.
    /// </summary>
    public double? BaselineValue { get; set; }

    /// <summary>
    /// When the baseline value was captured.
    /// </summary>
    public DateTime? BaselineSetAt { get; set; }

    /// <summary>
    /// How numeric changes should be tracked and reported.
    /// </summary>
    public NumericTrackingMode TrackingMode { get; set; } = NumericTrackingMode.Both;

    /// <summary>
    /// Minimum absolute change to consider significant.
    /// Changes below this threshold won't trigger notifications.
    /// </summary>
    public double? MinSignificantChange { get; set; }

    /// <summary>
    /// Minimum percentage change to consider significant.
    /// Changes below this threshold won't trigger notifications.
    /// </summary>
    public double? MinSignificantChangePercent { get; set; }

    /// <summary>
    /// Alert thresholds configured for this field.
    /// </summary>
    public List<FieldAlertThreshold> AlertThresholds { get; set; } = [];

    /// <summary>
    /// Whether to calculate and store trend data for this field.
    /// </summary>
    public bool CalculateTrend { get; set; } = true;

    /// <summary>
    /// Number of historical values to use for trend calculation.
    /// </summary>
    public int TrendWindowSize { get; set; } = 10;

    /// <summary>
    /// Whether to track min/max values seen for this field.
    /// </summary>
    public bool TrackMinMax { get; set; } = true;

    /// <summary>
    /// Historical minimum value observed.
    /// </summary>
    public double? HistoricalMin { get; set; }

    /// <summary>
    /// When the historical minimum was observed.
    /// </summary>
    public DateTime? HistoricalMinAt { get; set; }

    /// <summary>
    /// Historical maximum value observed.
    /// </summary>
    public double? HistoricalMax { get; set; }

    /// <summary>
    /// When the historical maximum was observed.
    /// </summary>
    public DateTime? HistoricalMaxAt { get; set; }
}

/// <summary>
/// How numeric field changes should be tracked and reported.
/// </summary>
public enum NumericTrackingMode
{
    /// <summary>Track absolute value changes only.</summary>
    Absolute,

    /// <summary>Track percentage changes only.</summary>
    Percentage,

    /// <summary>Track both absolute and percentage changes.</summary>
    Both
}

/// <summary>
/// An alert threshold for a numeric field.
/// Triggers notifications when the threshold condition is met.
/// </summary>
public class FieldAlertThreshold
{
    /// <summary>
    /// Unique identifier for this threshold.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name for this alert.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Type of threshold condition.
    /// </summary>
    public AlertConditionType ConditionType { get; set; }

    /// <summary>
    /// Primary threshold value.
    /// </summary>
    public double Value { get; set; }

    /// <summary>
    /// Secondary value for range-based conditions (e.g., Between).
    /// </summary>
    public double? SecondaryValue { get; set; }

    /// <summary>
    /// Whether this threshold is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Whether this is a one-time alert (auto-disables after triggering).
    /// </summary>
    public bool OneTime { get; set; }

    /// <summary>
    /// Minimum time between repeated alerts for this threshold.
    /// </summary>
    public TimeSpan? CooldownPeriod { get; set; }

    /// <summary>
    /// When this threshold was last triggered.
    /// </summary>
    public DateTime? LastTriggeredAt { get; set; }

    /// <summary>
    /// Number of times this threshold has been triggered.
    /// </summary>
    public int TriggerCount { get; set; }

    /// <summary>
    /// Custom notification message template.
    /// Supports placeholders: {FieldName}, {OldValue}, {NewValue}, {Change}, {ChangePercent}
    /// </summary>
    public string? NotificationTemplate { get; set; }

    /// <summary>
    /// Importance level to assign when triggered.
    /// </summary>
    public ChangeImportance? ImportanceOverride { get; set; }
}

/// <summary>
/// Types of alert threshold conditions.
/// </summary>
public enum AlertConditionType
{
    /// <summary>Value drops below the threshold.</summary>
    DropsBelow,

    /// <summary>Value rises above the threshold.</summary>
    RisesAbove,

    /// <summary>Value changes by more than threshold amount (absolute).</summary>
    ChangesBy,

    /// <summary>Value changes by more than threshold percentage.</summary>
    ChangesByPercent,

    /// <summary>Value drops by more than threshold percentage.</summary>
    DropsByPercent,

    /// <summary>Value rises by more than threshold percentage.</summary>
    RisesByPercent,

    /// <summary>Value enters a range (between Value and SecondaryValue).</summary>
    EntersRange,

    /// <summary>Value exits a range (was between Value and SecondaryValue, now outside).</summary>
    ExitsRange,

    /// <summary>Value reaches a new historical minimum.</summary>
    NewMinimum,

    /// <summary>Value reaches a new historical maximum.</summary>
    NewMaximum,

    /// <summary>Value returns to within threshold percent of baseline.</summary>
    ReturnsToBaseline,

    /// <summary>Value is at or below a specific target (e.g., target price reached).</summary>
    TargetReached,

    /// <summary>The best source (cheapest/highest) site changed to a different member.</summary>
    RankChanged,

    /// <summary>A site's value diverges significantly from the group consensus (outlier).</summary>
    OutlierDetected,

    /// <summary>A product was confirmed absent/disappeared from a site.</summary>
    SiteAbsent
}

/// <summary>
/// Data types for schema fields.
/// Fixed vocabulary for LLM inference consistency.
/// </summary>
public enum FieldType
{
    /// <summary>Plain text content.</summary>
    String,

    /// <summary>Date or datetime value.</summary>
    Date,

    /// <summary>URL or link.</summary>
    Url,

    /// <summary>Numeric value (integer or decimal).</summary>
    Number,

    /// <summary>Image URL or source.</summary>
    Image,

    /// <summary>Raw HTML content (preserves markup).</summary>
    Html,

    /// <summary>Currency value with symbol (e.g., $19.99, €15.00).</summary>
    Currency,

    /// <summary>Percentage value (e.g., 25%, -5.5%).</summary>
    Percentage,

    /// <summary>Duration/time span (e.g., 2h 30m, 45 minutes).</summary>
    Duration,

    /// <summary>Boolean/checkbox value.</summary>
    Boolean,

    /// <summary>Enumerated/status value from a fixed set.</summary>
    Status
}

/// <summary>
/// Settings for object-level diff computation.
/// </summary>
public class ObjectDiffSettings
{
    /// <summary>
    /// Level of detail for diff detection.
    /// </summary>
    public DiffGranularity Granularity { get; set; } = DiffGranularity.Both;

    /// <summary>
    /// Whether to use LLM to score importance of each change.
    /// </summary>
    public bool EnableImportanceScoring { get; set; } = true;

    /// <summary>
    /// Default importance level when LLM scoring is disabled.
    /// </summary>
    public ChangeImportance DefaultImportance { get; set; } = ChangeImportance.Medium;
}

/// <summary>
/// Granularity of object diff detection.
/// </summary>
public enum DiffGranularity
{
    /// <summary>Only detect added/removed items, ignore field changes.</summary>
    ItemsOnly,

    /// <summary>Only detect field-level changes within existing items.</summary>
    FieldLevel,

    /// <summary>Detect both item-level and field-level changes.</summary>
    Both
}

/// <summary>
/// Result of extracting objects from HTML using a schema.
/// </summary>
public class ObjectExtractionResult
{
    /// <summary>
    /// Successfully extracted objects. Null if extraction failed.
    /// </summary>
    public List<ExtractedObject>? Objects { get; set; }

    /// <summary>
    /// Whether extraction completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if extraction failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Whether the page structure no longer matches the schema.
    /// </summary>
    public bool DriftDetected { get; set; }

    /// <summary>
    /// Warnings about objects with duplicate identity values.
    /// </summary>
    public List<string> AmbiguousIdentityWarnings { get; set; } = [];

    /// <summary>
    /// Non-fatal issues detected during extraction (truncation, missing optional fields, etc.)
    /// </summary>
    public List<string> Issues { get; set; } = [];

    /// <summary>
    /// Total items found on the page before any truncation limits.
    /// Null if not applicable (e.g., LLM-only extraction without item selector).
    /// </summary>
    public int? TotalItemsOnPage { get; set; }

    /// <summary>
    /// Whether extraction results were truncated due to LLM token limits.
    /// </summary>
    public bool WasTruncated { get; set; }
}

/// <summary>
/// A single object extracted from a webpage.
/// </summary>
public class ExtractedObject
{
    /// <summary>
    /// Field values keyed by field name.
    /// </summary>
    public Dictionary<string, string?> Fields { get; set; } = [];

    /// <summary>
    /// Computed identity string from identity fields for matching.
    /// </summary>
    public string? IdentityKey { get; set; }

    /// <summary>
    /// Index of this object in the extraction order (0-based).
    /// </summary>
    public int Index { get; set; }
}

/// <summary>
/// Result of comparing two sets of extracted objects.
/// </summary>
public class ObjectDiffResult
{
    /// <summary>
    /// Objects that appear in current but not previous.
    /// </summary>
    public List<ExtractedObject> AddedItems { get; set; } = [];

    /// <summary>
    /// Objects that appear in previous but not current.
    /// </summary>
    public List<ExtractedObject> RemovedItems { get; set; } = [];

    /// <summary>
    /// Objects that exist in both but have field changes.
    /// </summary>
    public List<ObjectModification> ModifiedItems { get; set; } = [];

    /// <summary>
    /// Whether any objects had ambiguous identity matches.
    /// </summary>
    public bool HasAmbiguousIdentities { get; set; }

    /// <summary>
    /// Details about ambiguous identity conflicts.
    /// </summary>
    public List<string> AmbiguityDetails { get; set; } = [];

    /// <summary>
    /// Whether any changes were detected.
    /// </summary>
    public bool HasChanges => AddedItems.Count > 0 || RemovedItems.Count > 0 || ModifiedItems.Count > 0;
}

/// <summary>
/// Describes changes to a single object's fields.
/// </summary>
public class ObjectModification
{
    /// <summary>
    /// The identity key of the modified object.
    /// </summary>
    public required string IdentityKey { get; set; }

    /// <summary>
    /// The object before changes.
    /// </summary>
    public required ExtractedObject PreviousObject { get; set; }

    /// <summary>
    /// The object after changes.
    /// </summary>
    public required ExtractedObject CurrentObject { get; set; }

    /// <summary>
    /// Individual field changes.
    /// </summary>
    public List<FieldChange> FieldChanges { get; set; } = [];
}

/// <summary>
/// Describes a change to a single field value.
/// </summary>
public class FieldChange
{
    /// <summary>
    /// Name of the changed field.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Previous value (null if field was added).
    /// </summary>
    public string? OldValue { get; set; }

    /// <summary>
    /// Current value (null if field was removed).
    /// </summary>
    public string? NewValue { get; set; }

    /// <summary>
    /// LLM-scored importance of this specific change.
    /// </summary>
    public ChangeImportance? LlmImportance { get; set; }

    /// <summary>
    /// LLM explanation of why this change matters.
    /// </summary>
    public string? ImportanceReason { get; set; }

    // ========== Numeric Change Analysis (for stock/price tracking) ==========

    /// <summary>
    /// The data type of this field.
    /// </summary>
    public FieldType? FieldType { get; set; }

    /// <summary>
    /// Whether this is a numeric field (Number, Currency, or Percentage).
    /// </summary>
    public bool IsNumeric => FieldType is
        Entities.FieldType.Number or
        Entities.FieldType.Currency or
        Entities.FieldType.Percentage;

    /// <summary>
    /// Previous numeric value (parsed from OldValue).
    /// </summary>
    public double? OldNumericValue { get; set; }

    /// <summary>
    /// Current numeric value (parsed from NewValue).
    /// </summary>
    public double? NewNumericValue { get; set; }

    /// <summary>
    /// Absolute change (NewNumericValue - OldNumericValue).
    /// </summary>
    public double? AbsoluteChange { get; set; }

    /// <summary>
    /// Percentage change from old to new value.
    /// Formula: ((NewValue - OldValue) / OldValue) * 100
    /// </summary>
    public double? PercentageChange { get; set; }

    /// <summary>
    /// Direction of the change.
    /// </summary>
    public ChangeDirection Direction { get; set; } = ChangeDirection.Unknown;

    /// <summary>
    /// Currency code if this is a currency field.
    /// </summary>
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Unit label if applicable.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Change compared to baseline value.
    /// </summary>
    public double? ChangeFromBaseline { get; set; }

    /// <summary>
    /// Percentage change from baseline.
    /// </summary>
    public double? PercentageFromBaseline { get; set; }

    /// <summary>
    /// Whether this value is a new historical minimum.
    /// </summary>
    public bool IsNewMinimum { get; set; }

    /// <summary>
    /// Whether this value is a new historical maximum.
    /// </summary>
    public bool IsNewMaximum { get; set; }

    /// <summary>
    /// Alert thresholds that were triggered by this change.
    /// </summary>
    public List<TriggeredAlert> TriggeredAlerts { get; set; } = [];

    /// <summary>
    /// Trend direction based on recent history.
    /// </summary>
    public TrendDirection Trend { get; set; } = TrendDirection.Unknown;

    /// <summary>
    /// Number of consecutive changes in the same direction.
    /// </summary>
    public int ConsecutiveDirectionCount { get; set; }
}

/// <summary>
/// Direction of a numeric change.
/// </summary>
public enum ChangeDirection
{
    Unknown,
    Increased,
    Decreased,
    Unchanged
}

/// <summary>
/// Trend direction over a series of values.
/// </summary>
public enum TrendDirection
{
    Unknown,
    Up,
    Down,
    Stable,
    Volatile
}

/// <summary>
/// Record of an alert that was triggered by a field change.
/// </summary>
public class TriggeredAlert
{
    /// <summary>
    /// ID of the alert threshold that was triggered.
    /// </summary>
    public Guid ThresholdId { get; set; }

    /// <summary>
    /// Name of the alert.
    /// </summary>
    public string? AlertName { get; set; }

    /// <summary>
    /// Type of condition that was met.
    /// </summary>
    public AlertConditionType ConditionType { get; set; }

    /// <summary>
    /// The threshold value that was crossed.
    /// </summary>
    public double ThresholdValue { get; set; }

    /// <summary>
    /// The actual value that triggered the alert.
    /// </summary>
    public double ActualValue { get; set; }

    /// <summary>
    /// When the alert was triggered.
    /// </summary>
    public DateTime TriggeredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Formatted notification message using the template.
    /// </summary>
    public string? NotificationMessage { get; set; }

    /// <summary>
    /// Importance level from the threshold configuration.
    /// </summary>
    public ChangeImportance? Importance { get; set; }
}
