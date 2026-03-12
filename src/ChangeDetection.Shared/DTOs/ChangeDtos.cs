namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for listing changes in the UI.
/// </summary>
public class ChangeListItemDto
{
    public string Id { get; set; } = "";
    public string WatchId { get; set; } = "";
    public string? WatchTitle { get; set; }
    public DateTime DetectedAt { get; set; }
    public string Summary { get; set; } = "";
    public string Importance { get; set; } = "Low";
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public bool IsViewed { get; set; }
    public bool IsNotified { get; set; }
    
    /// <summary>
    /// Whether this change has object-level diff data (schema-based tracking).
    /// </summary>
    public bool HasObjectDiff { get; set; }
    
    /// <summary>
    /// Quick counts for object-level changes.
    /// </summary>
    public int ObjectsAdded { get; set; }
    public int ObjectsRemoved { get; set; }
    public int ObjectsModified { get; set; }
    
    /// <summary>Relevance score (0-1) from profile matching.</summary>
    public float? RelevanceScore { get; set; }
    
    /// <summary>LLM-generated relevance reason for the change.</summary>
    public string? RelevanceReason { get; set; }
    
    /// <summary>Profile match dimensions for job-watch changes (JSON).</summary>
    public string? MatchDimensionsJson { get; set; }

    /// <summary>Structured extracted entities or listings for enriched result cards (JSON).</summary>
    public string? ExtractedEntitiesJson { get; set; }

    /// <summary>User feedback status (None, Helpful, FalsePositive, Irrelevant, Missed).</summary>
    public string Feedback { get; set; } = "None";
}

/// <summary>
/// DTO for detailed change view with diff.
/// </summary>
public class ChangeDetailDto
{
    public string Id { get; set; } = "";
    public string WatchId { get; set; } = "";
    public string? WatchTitle { get; set; }
    public string? WatchUrl { get; set; }
    public DateTime DetectedAt { get; set; }
    public string Summary { get; set; } = "";
    public string? DiffText { get; set; }
    public string? DiffHtml { get; set; }
    public string Importance { get; set; } = "Low";
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public bool IsViewed { get; set; }
    public SnapshotInfoDto? PreviousSnapshot { get; set; }
    public SnapshotInfoDto? CurrentSnapshot { get; set; }
    
    // ========== Object-Level Diff Data ==========
    
    /// <summary>
    /// Whether this change has object-level diff data (schema-based tracking).
    /// </summary>
    public bool HasObjectDiff { get; set; }
    
    /// <summary>
    /// Object-level diff result when schema extraction is enabled.
    /// </summary>
    public ObjectDiffDetailDto? ObjectDiff { get; set; }
    
    /// <summary>
    /// The schema used for extraction (for displaying field metadata).
    /// </summary>
    public ExtractionSchemaDto? Schema { get; set; }
    
    // ========== Profile Match Data ==========
    
    /// <summary>Profile match dimensions for job-watch changes (JSON).</summary>
    public string? MatchDimensionsJson { get; set; }

    /// <summary>LLM-generated relevance reason for the change.</summary>
    public string? RelevanceReason { get; set; }

    /// <summary>Relevance score (0-1) from profile matching.</summary>
    public float? RelevanceScore { get; set; }

    /// <summary>Structured extracted entities or listings for enriched displays (JSON).</summary>
    public string? ExtractedEntitiesJson { get; set; }

    /// <summary>User feedback status (None, Helpful, FalsePositive, Irrelevant, Missed).</summary>
    public string Feedback { get; set; } = "None";
}

/// <summary>
/// Detailed object diff for change visualization.
/// </summary>
public class ObjectDiffDetailDto
{
    /// <summary>
    /// Objects that were added (not in previous snapshot).
    /// </summary>
    public List<ExtractedObjectDetailDto> AddedObjects { get; set; } = [];
    
    /// <summary>
    /// Objects that were removed (not in current snapshot).
    /// </summary>
    public List<ExtractedObjectDetailDto> RemovedObjects { get; set; } = [];
    
    /// <summary>
    /// Objects that exist in both but have changed fields.
    /// </summary>
    public List<ObjectModificationDto> ModifiedObjects { get; set; } = [];
    
    /// <summary>
    /// Whether any objects had ambiguous identity matches.
    /// </summary>
    public bool HasAmbiguousIdentities { get; set; }
    
    /// <summary>
    /// Details about ambiguous matches.
    /// </summary>
    public List<string> AmbiguityDetails { get; set; } = [];
    
    /// <summary>
    /// Total objects in current snapshot.
    /// </summary>
    public int TotalCurrentObjects { get; set; }
    
    /// <summary>
    /// Total objects in previous snapshot.
    /// </summary>
    public int TotalPreviousObjects { get; set; }
}

/// <summary>
/// Extracted object with all field values.
/// </summary>
public class ExtractedObjectDetailDto
{
    /// <summary>
    /// Identity key for matching.
    /// </summary>
    public string? IdentityKey { get; set; }
    
    /// <summary>
    /// Display label (computed from identity fields).
    /// </summary>
    public string? DisplayLabel { get; set; }
    
    /// <summary>
    /// All field values.
    /// </summary>
    public List<FieldValueDto> Fields { get; set; } = [];
    
    /// <summary>
    /// Position in the list (0-based).
    /// </summary>
    public int Index { get; set; }
}

/// <summary>
/// A field name-value pair with metadata.
/// </summary>
public class FieldValueDto
{
    public required string Name { get; set; }
    public string? Value { get; set; }
    public required string Type { get; set; }
    
    /// <summary>
    /// Formatted display value (e.g., "$19.99" instead of "19.99").
    /// </summary>
    public string? FormattedValue { get; set; }
    
    /// <summary>
    /// Parsed numeric value for Number/Currency/Percentage types.
    /// </summary>
    public double? NumericValue { get; set; }
}

/// <summary>
/// An object modification with field-level changes.
/// </summary>
public class ObjectModificationDto
{
    /// <summary>
    /// Identity key of the modified object.
    /// </summary>
    public required string IdentityKey { get; set; }
    
    /// <summary>
    /// Display label for the object.
    /// </summary>
    public string? DisplayLabel { get; set; }
    
    /// <summary>
    /// Individual field changes.
    /// </summary>
    public List<FieldChangeDetailDto> FieldChanges { get; set; } = [];
    
    /// <summary>
    /// LLM-scored importance of the overall change.
    /// </summary>
    public string? Importance { get; set; }
    
    /// <summary>
    /// LLM explanation.
    /// </summary>
    public string? ImportanceReason { get; set; }
}

/// <summary>
/// A single field change with rich display data.
/// </summary>
public class FieldChangeDetailDto
{
    /// <summary>
    /// Name of the changed field.
    /// </summary>
    public required string FieldName { get; set; }
    
    /// <summary>
    /// Field type for formatting.
    /// </summary>
    public required string FieldType { get; set; }
    
    /// <summary>
    /// Previous raw value.
    /// </summary>
    public string? OldValue { get; set; }
    
    /// <summary>
    /// Current raw value.
    /// </summary>
    public string? NewValue { get; set; }
    
    /// <summary>
    /// Formatted previous value.
    /// </summary>
    public string? OldFormattedValue { get; set; }
    
    /// <summary>
    /// Formatted current value.
    /// </summary>
    public string? NewFormattedValue { get; set; }
    
    /// <summary>
    /// Previous numeric value (for Number/Currency/Percentage).
    /// </summary>
    public double? OldNumericValue { get; set; }
    
    /// <summary>
    /// Current numeric value (for Number/Currency/Percentage).
    /// </summary>
    public double? NewNumericValue { get; set; }
    
    /// <summary>
    /// Numeric change (NewNumericValue - OldNumericValue).
    /// </summary>
    public double? NumericChange { get; set; }
    
    /// <summary>
    /// Percentage change ((New - Old) / Old * 100).
    /// </summary>
    public double? PercentageChange { get; set; }
    
    /// <summary>
    /// Whether this is an increase (positive change).
    /// </summary>
    public bool? IsIncrease { get; set; }
    
    /// <summary>
    /// Currency code if applicable.
    /// </summary>
    public string? CurrencyCode { get; set; }
    
    /// <summary>
    /// Unit label if applicable.
    /// </summary>
    public string? Unit { get; set; }
    
    /// <summary>
    /// LLM-scored importance of this specific change.
    /// </summary>
    public string? Importance { get; set; }
    
    /// <summary>
    /// LLM explanation.
    /// </summary>
    public string? ImportanceReason { get; set; }

    // ========== Extended Numeric Tracking (for stock/price monitoring) ==========

    /// <summary>
    /// Direction of the change: Increased, Decreased, Unchanged, Unknown.
    /// </summary>
    public string Direction { get; set; } = "Unknown";

    /// <summary>
    /// Change compared to baseline value.
    /// </summary>
    public double? ChangeFromBaseline { get; set; }

    /// <summary>
    /// Percentage change from baseline.
    /// </summary>
    public double? PercentageFromBaseline { get; set; }

    /// <summary>
    /// Whether this is a new historical minimum.
    /// </summary>
    public bool IsNewMinimum { get; set; }

    /// <summary>
    /// Whether this is a new historical maximum.
    /// </summary>
    public bool IsNewMaximum { get; set; }

    /// <summary>
    /// Alerts triggered by this change.
    /// </summary>
    public List<TriggeredAlertDto> TriggeredAlerts { get; set; } = [];

    /// <summary>
    /// Trend direction: Up, Down, Stable, Volatile, Unknown.
    /// </summary>
    public string Trend { get; set; } = "Unknown";

    /// <summary>
    /// Number of consecutive changes in the same direction.
    /// </summary>
    public int ConsecutiveDirectionCount { get; set; }
}

/// <summary>
/// DTO for a triggered alert.
/// </summary>
public class TriggeredAlertDto
{
    /// <summary>
    /// ID of the alert threshold.
    /// </summary>
    public string ThresholdId { get; set; } = "";

    /// <summary>
    /// Alert name.
    /// </summary>
    public string? AlertName { get; set; }

    /// <summary>
    /// Condition type that triggered.
    /// </summary>
    public string ConditionType { get; set; } = "";

    /// <summary>
    /// Threshold value that was crossed.
    /// </summary>
    public double ThresholdValue { get; set; }

    /// <summary>
    /// Actual value that triggered.
    /// </summary>
    public double ActualValue { get; set; }

    /// <summary>
    /// When triggered.
    /// </summary>
    public DateTime TriggeredAt { get; set; }

    /// <summary>
    /// Notification message.
    /// </summary>
    public string? NotificationMessage { get; set; }

    /// <summary>
    /// Importance level.
    /// </summary>
    public string? Importance { get; set; }
}

/// <summary>
/// Historical data point for field charting.
/// </summary>
public class FieldHistoryPointDto
{
    /// <summary>
    /// When this value was captured.
    /// </summary>
    public DateTime CapturedAt { get; set; }
    
    /// <summary>
    /// Raw value.
    /// </summary>
    public string? Value { get; set; }
    
    /// <summary>
    /// Parsed numeric value.
    /// </summary>
    public double? NumericValue { get; set; }
    
    /// <summary>
    /// Formatted display value.
    /// </summary>
    public string? FormattedValue { get; set; }
}

/// <summary>
/// Field history for charting.
/// </summary>
public class FieldHistoryDto
{
    /// <summary>
    /// Object identity key.
    /// </summary>
    public required string ObjectIdentity { get; set; }
    
    /// <summary>
    /// Field name.
    /// </summary>
    public required string FieldName { get; set; }
    
    /// <summary>
    /// Field type.
    /// </summary>
    public required string FieldType { get; set; }
    
    /// <summary>
    /// Currency code if applicable.
    /// </summary>
    public string? CurrencyCode { get; set; }
    
    /// <summary>
    /// Unit label if applicable.
    /// </summary>
    public string? Unit { get; set; }
    
    /// <summary>
    /// Historical data points.
    /// </summary>
    public List<FieldHistoryPointDto> DataPoints { get; set; } = [];
    
    /// <summary>
    /// Minimum value in dataset.
    /// </summary>
    public double? MinValue { get; set; }
    
    /// <summary>
    /// Maximum value in dataset.
    /// </summary>
    public double? MaxValue { get; set; }
    
    /// <summary>
    /// Average value.
    /// </summary>
    public double? AverageValue { get; set; }
    
    /// <summary>
    /// Overall trend: "up", "down", "stable".
    /// </summary>
    public string? Trend { get; set; }

    /// <summary>
    /// Standard deviation.
    /// </summary>
    public double? StandardDeviation { get; set; }

    /// <summary>
    /// Total change from first to last value.
    /// </summary>
    public double? TotalChange { get; set; }

    /// <summary>
    /// Total percentage change.
    /// </summary>
    public double? TotalChangePercent { get; set; }

    /// <summary>
    /// Trend strength (0-1).
    /// </summary>
    public double? TrendStrength { get; set; }

    /// <summary>
    /// Volatility score (0-1).
    /// </summary>
    public double? Volatility { get; set; }

    /// <summary>
    /// Period change summaries.
    /// </summary>
    public List<PeriodChangeSummaryDto> PeriodSummaries { get; set; } = [];
}

/// <summary>
/// DTO for field statistics.
/// </summary>
public class FieldStatisticsDto
{
    /// <summary>
    /// Object identity key.
    /// </summary>
    public required string ObjectIdentity { get; set; }

    /// <summary>
    /// Field name.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Total count of values.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Minimum value.
    /// </summary>
    public double? Min { get; set; }

    /// <summary>
    /// When minimum occurred.
    /// </summary>
    public DateTime? MinAt { get; set; }

    /// <summary>
    /// Maximum value.
    /// </summary>
    public double? Max { get; set; }

    /// <summary>
    /// When maximum occurred.
    /// </summary>
    public DateTime? MaxAt { get; set; }

    /// <summary>
    /// Average value.
    /// </summary>
    public double? Average { get; set; }

    /// <summary>
    /// Median value.
    /// </summary>
    public double? Median { get; set; }

    /// <summary>
    /// Standard deviation.
    /// </summary>
    public double? StandardDeviation { get; set; }

    /// <summary>
    /// First value.
    /// </summary>
    public double? FirstValue { get; set; }

    /// <summary>
    /// When first recorded.
    /// </summary>
    public DateTime? FirstValueAt { get; set; }

    /// <summary>
    /// Last (current) value.
    /// </summary>
    public double? LastValue { get; set; }

    /// <summary>
    /// When last recorded.
    /// </summary>
    public DateTime? LastValueAt { get; set; }

    /// <summary>
    /// Total change.
    /// </summary>
    public double? TotalChange { get; set; }

    /// <summary>
    /// Total percentage change.
    /// </summary>
    public double? TotalChangePercent { get; set; }

    /// <summary>
    /// Trend direction.
    /// </summary>
    public string Trend { get; set; } = "Unknown";

    /// <summary>
    /// Trend strength (0-1).
    /// </summary>
    public double? TrendStrength { get; set; }

    /// <summary>
    /// Volatility score (0-1).
    /// </summary>
    public double? Volatility { get; set; }

    /// <summary>
    /// 52-week or period high.
    /// </summary>
    public double? PeriodHigh { get; set; }

    /// <summary>
    /// When period high occurred.
    /// </summary>
    public DateTime? PeriodHighAt { get; set; }

    /// <summary>
    /// 52-week or period low.
    /// </summary>
    public double? PeriodLow { get; set; }

    /// <summary>
    /// When period low occurred.
    /// </summary>
    public DateTime? PeriodLowAt { get; set; }

    /// <summary>
    /// Percentage from period high.
    /// </summary>
    public double? PercentFromHigh { get; set; }

    /// <summary>
    /// Percentage from period low.
    /// </summary>
    public double? PercentFromLow { get; set; }
}

/// <summary>
/// DTO for period change summary.
/// </summary>
public class PeriodChangeSummaryDto
{
    /// <summary>
    /// Period label (1D, 1W, 1M, 3M, 1Y, All).
    /// </summary>
    public required string Period { get; set; }

    /// <summary>
    /// Period start date.
    /// </summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>
    /// Period end date.
    /// </summary>
    public DateTime PeriodEnd { get; set; }

    /// <summary>
    /// Value at start.
    /// </summary>
    public double? StartValue { get; set; }

    /// <summary>
    /// Value at end.
    /// </summary>
    public double? EndValue { get; set; }

    /// <summary>
    /// Absolute change.
    /// </summary>
    public double? AbsoluteChange { get; set; }

    /// <summary>
    /// Percentage change.
    /// </summary>
    public double? PercentChange { get; set; }

    /// <summary>
    /// Highest value in period.
    /// </summary>
    public double? High { get; set; }

    /// <summary>
    /// Lowest value in period.
    /// </summary>
    public double? Low { get; set; }

    /// <summary>
    /// Number of data points.
    /// </summary>
    public int DataPointCount { get; set; }
}

/// <summary>
/// DTO for tracked field info.
/// </summary>
public class TrackedFieldInfoDto
{
    /// <summary>
    /// Object identity.
    /// </summary>
    public required string ObjectIdentity { get; set; }

    /// <summary>
    /// Field name.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Field type.
    /// </summary>
    public required string FieldType { get; set; }

    /// <summary>
    /// Currency code.
    /// </summary>
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Unit label.
    /// </summary>
    public string? Unit { get; set; }

    /// <summary>
    /// Number of recorded values.
    /// </summary>
    public int ValueCount { get; set; }

    /// <summary>
    /// First value timestamp.
    /// </summary>
    public DateTime? FirstValueAt { get; set; }

    /// <summary>
    /// Last value timestamp.
    /// </summary>
    public DateTime? LastValueAt { get; set; }

    /// <summary>
    /// Current value.
    /// </summary>
    public double? CurrentValue { get; set; }

    /// <summary>
    /// Current trend.
    /// </summary>
    public string Trend { get; set; } = "Unknown";

    /// <summary>
    /// Whether alerts are configured.
    /// </summary>
    public bool HasAlerts { get; set; }

    /// <summary>
    /// Number of alerts.
    /// </summary>
    public int AlertCount { get; set; }
}

/// <summary>
/// DTO for snapshot info in change details.
/// </summary>
public class SnapshotInfoDto
{
    public string Id { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public string Content { get; set; } = "";
    
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
}
