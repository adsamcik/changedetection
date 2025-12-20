using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for managing field value history and computing statistics.
/// Supports stock/price tracking, charting, and trend analysis.
/// </summary>
public interface IFieldHistoryService
{
    /// <summary>
    /// Records a field value to history.
    /// </summary>
    Task RecordValueAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        string? rawValue,
        double? numericValue,
        FieldType fieldType,
        Guid snapshotId,
        string? currencyCode = null,
        string? unit = null,
        CancellationToken ct = default);

    /// <summary>
    /// Records multiple field values in a batch.
    /// </summary>
    Task RecordValuesAsync(
        Guid watchId,
        Guid snapshotId,
        IEnumerable<FieldValueRecord> values,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the history for a specific field.
    /// </summary>
    Task<IReadOnlyList<FieldValueHistory>> GetHistoryAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets aggregated statistics for a field.
    /// </summary>
    Task<FieldStatistics?> GetStatisticsAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets statistics for multiple fields in a single call.
    /// </summary>
    Task<IReadOnlyList<FieldStatistics>> GetMultipleStatisticsAsync(
        Guid watchId,
        IEnumerable<(string ObjectIdentity, string FieldName)> fields,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the most recent value for a field.
    /// </summary>
    Task<FieldValueHistory?> GetLatestValueAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        CancellationToken ct = default);

    /// <summary>
    /// Gets period change summaries (1D, 1W, 1M, 3M, 1Y, etc.).
    /// </summary>
    Task<IReadOnlyList<PeriodChangeSummary>> GetPeriodSummariesAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        CancellationToken ct = default);

    /// <summary>
    /// Calculates the change metrics between two values.
    /// </summary>
    FieldChangeMetrics CalculateChange(
        double? previousValue,
        double? currentValue,
        FieldStatistics? statistics = null);

    /// <summary>
    /// Evaluates alert thresholds against a field change.
    /// </summary>
    Task<List<TriggeredAlert>> EvaluateAlertsAsync(
        SchemaField field,
        double? oldValue,
        double? newValue,
        FieldStatistics? statistics = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all fields with history for a watch.
    /// </summary>
    Task<IReadOnlyList<TrackedFieldInfo>> GetTrackedFieldsAsync(
        Guid watchId,
        CancellationToken ct = default);

    /// <summary>
    /// Updates the baseline value for a field.
    /// </summary>
    Task UpdateBaselineAsync(
        Guid watchId,
        string fieldName,
        double baselineValue,
        CancellationToken ct = default);

    /// <summary>
    /// Prunes old history records based on retention policy.
    /// </summary>
    Task<int> PruneHistoryAsync(
        Guid watchId,
        TimeSpan retentionPeriod,
        CancellationToken ct = default);

    /// <summary>
    /// Prunes history for all watches based on retention policy.
    /// </summary>
    Task<int> PruneAllHistoryAsync(
        TimeSpan retentionPeriod,
        CancellationToken ct = default);
}

/// <summary>
/// Record for batch field value recording.
/// </summary>
public class FieldValueRecord
{
    /// <summary>
    /// Object identity key.
    /// </summary>
    public required string ObjectIdentity { get; init; }

    /// <summary>
    /// Field name.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Raw string value.
    /// </summary>
    public string? RawValue { get; init; }

    /// <summary>
    /// Parsed numeric value.
    /// </summary>
    public double? NumericValue { get; init; }

    /// <summary>
    /// Field data type.
    /// </summary>
    public FieldType FieldType { get; init; }

    /// <summary>
    /// Currency code if applicable.
    /// </summary>
    public string? CurrencyCode { get; init; }

    /// <summary>
    /// Unit label if applicable.
    /// </summary>
    public string? Unit { get; init; }
}

/// <summary>
/// Calculated change metrics between two numeric values.
/// </summary>
public class FieldChangeMetrics
{
    /// <summary>
    /// Absolute change (new - old).
    /// </summary>
    public double? AbsoluteChange { get; init; }

    /// <summary>
    /// Percentage change from old value.
    /// </summary>
    public double? PercentageChange { get; init; }

    /// <summary>
    /// Direction of the change.
    /// </summary>
    public ChangeDirection Direction { get; init; }

    /// <summary>
    /// Whether this is a new historical minimum.
    /// </summary>
    public bool IsNewMinimum { get; init; }

    /// <summary>
    /// Whether this is a new historical maximum.
    /// </summary>
    public bool IsNewMaximum { get; init; }

    /// <summary>
    /// Whether this value is an outlier.
    /// </summary>
    public bool IsOutlier { get; init; }

    /// <summary>
    /// Z-score (standard deviations from mean).
    /// </summary>
    public double? ZScore { get; init; }

    /// <summary>
    /// Current trend direction.
    /// </summary>
    public TrendDirection Trend { get; init; }

    /// <summary>
    /// Number of consecutive changes in the same direction.
    /// </summary>
    public int ConsecutiveDirectionCount { get; init; }

    /// <summary>
    /// Change from baseline value.
    /// </summary>
    public double? ChangeFromBaseline { get; init; }

    /// <summary>
    /// Percentage change from baseline.
    /// </summary>
    public double? PercentageFromBaseline { get; init; }
}

/// <summary>
/// Information about a tracked field.
/// </summary>
public class TrackedFieldInfo
{
    /// <summary>
    /// Object identity key.
    /// </summary>
    public required string ObjectIdentity { get; init; }

    /// <summary>
    /// Field name.
    /// </summary>
    public required string FieldName { get; init; }

    /// <summary>
    /// Field data type.
    /// </summary>
    public FieldType FieldType { get; init; }

    /// <summary>
    /// Currency code if applicable.
    /// </summary>
    public string? CurrencyCode { get; init; }

    /// <summary>
    /// Unit label if applicable.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// Total number of recorded values.
    /// </summary>
    public int ValueCount { get; init; }

    /// <summary>
    /// When the first value was recorded.
    /// </summary>
    public DateTime? FirstValueAt { get; init; }

    /// <summary>
    /// When the most recent value was recorded.
    /// </summary>
    public DateTime? LastValueAt { get; init; }

    /// <summary>
    /// Current (most recent) value.
    /// </summary>
    public double? CurrentValue { get; init; }

    /// <summary>
    /// Current trend direction.
    /// </summary>
    public TrendDirection Trend { get; init; }

    /// <summary>
    /// Whether alerts are configured for this field.
    /// </summary>
    public bool HasAlerts { get; init; }

    /// <summary>
    /// Number of active alert thresholds.
    /// </summary>
    public int AlertCount { get; init; }
}
