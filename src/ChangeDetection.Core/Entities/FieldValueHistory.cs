using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Historical record of a field value for time-series analysis.
/// Enables charting, trend analysis, and statistical queries for stock/price tracking.
/// </summary>
public class FieldValueHistory : IOwnedEntity
{
    /// <summary>
    /// Unique identifier.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Owner of this record.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;

    /// <summary>
    /// The watch this history belongs to.
    /// </summary>
    public Guid WatchedSiteId { get; set; }

    /// <summary>
    /// Identity key of the object this field belongs to.
    /// For single-value tracking (non-list), use a constant like "_single".
    /// </summary>
    public required string ObjectIdentity { get; set; }

    /// <summary>
    /// Name of the field being tracked.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// When this value was captured.
    /// </summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The snapshot ID this value came from.
    /// </summary>
    public Guid SnapshotId { get; set; }

    /// <summary>
    /// Raw string value as extracted.
    /// </summary>
    public string? RawValue { get; set; }

    /// <summary>
    /// Parsed numeric value.
    /// </summary>
    public double? NumericValue { get; set; }

    /// <summary>
    /// Data type of the field.
    /// </summary>
    public FieldType FieldType { get; set; }

    /// <summary>
    /// Currency code if applicable.
    /// </summary>
    public string? CurrencyCode { get; set; }

    /// <summary>
    /// Unit label if applicable.
    /// </summary>
    public string? Unit { get; set; }

    // ========== Change Metrics ==========

    /// <summary>
    /// Change from previous value.
    /// </summary>
    public double? ChangeFromPrevious { get; set; }

    /// <summary>
    /// Percentage change from previous value.
    /// </summary>
    public double? PercentChangeFromPrevious { get; set; }

    /// <summary>
    /// Change direction from previous value.
    /// </summary>
    public ChangeDirection Direction { get; set; } = ChangeDirection.Unknown;

    // ========== Statistical Context ==========

    /// <summary>
    /// Running minimum up to this point.
    /// </summary>
    public double? RunningMin { get; set; }

    /// <summary>
    /// Running maximum up to this point.
    /// </summary>
    public double? RunningMax { get; set; }

    /// <summary>
    /// Running average up to this point.
    /// </summary>
    public double? RunningAverage { get; set; }

    /// <summary>
    /// Standard deviation of values up to this point.
    /// </summary>
    public double? StandardDeviation { get; set; }

    /// <summary>
    /// Number of values in the running statistics.
    /// </summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// Whether this value is an outlier (> 2 standard deviations from mean).
    /// </summary>
    public bool IsOutlier { get; set; }

    /// <summary>
    /// Number of standard deviations from the mean (z-score).
    /// </summary>
    public double? ZScore { get; set; }
}

/// <summary>
/// Aggregated statistics for a field's historical values.
/// </summary>
public class FieldStatistics
{
    /// <summary>
    /// The watch this statistics belong to.
    /// </summary>
    public Guid WatchedSiteId { get; set; }

    /// <summary>
    /// Object identity key.
    /// </summary>
    public required string ObjectIdentity { get; set; }

    /// <summary>
    /// Field name.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Total number of recorded values.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Minimum value observed.
    /// </summary>
    public double? Min { get; set; }

    /// <summary>
    /// When the minimum was observed.
    /// </summary>
    public DateTime? MinAt { get; set; }

    /// <summary>
    /// Maximum value observed.
    /// </summary>
    public double? Max { get; set; }

    /// <summary>
    /// When the maximum was observed.
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
    /// Variance.
    /// </summary>
    public double? Variance { get; set; }

    /// <summary>
    /// First value recorded.
    /// </summary>
    public double? FirstValue { get; set; }

    /// <summary>
    /// When the first value was recorded.
    /// </summary>
    public DateTime? FirstValueAt { get; set; }

    /// <summary>
    /// Most recent value.
    /// </summary>
    public double? LastValue { get; set; }

    /// <summary>
    /// When the most recent value was recorded.
    /// </summary>
    public DateTime? LastValueAt { get; set; }

    /// <summary>
    /// Overall change from first to last value.
    /// </summary>
    public double? TotalChange { get; set; }

    /// <summary>
    /// Overall percentage change from first to last value.
    /// </summary>
    public double? TotalChangePercent { get; set; }

    /// <summary>
    /// Overall trend direction.
    /// </summary>
    public TrendDirection Trend { get; set; } = TrendDirection.Unknown;

    /// <summary>
    /// Trend strength (0-1, based on linear regression R²).
    /// </summary>
    public double? TrendStrength { get; set; }

    /// <summary>
    /// Volatility score (0-1, based on coefficient of variation).
    /// </summary>
    public double? Volatility { get; set; }

    /// <summary>
    /// 52-week (or available period) high.
    /// </summary>
    public double? PeriodHigh { get; set; }

    /// <summary>
    /// When the period high was reached.
    /// </summary>
    public DateTime? PeriodHighAt { get; set; }

    /// <summary>
    /// 52-week (or available period) low.
    /// </summary>
    public double? PeriodLow { get; set; }

    /// <summary>
    /// When the period low was reached.
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
/// Summary of price/value changes over a time period.
/// </summary>
public class PeriodChangeSummary
{
    /// <summary>
    /// Period label (e.g., "1D", "1W", "1M", "3M", "1Y").
    /// </summary>
    public required string Period { get; set; }

    /// <summary>
    /// Start of the period.
    /// </summary>
    public DateTime PeriodStart { get; set; }

    /// <summary>
    /// End of the period.
    /// </summary>
    public DateTime PeriodEnd { get; set; }

    /// <summary>
    /// Value at the start of the period.
    /// </summary>
    public double? StartValue { get; set; }

    /// <summary>
    /// Value at the end of the period.
    /// </summary>
    public double? EndValue { get; set; }

    /// <summary>
    /// Absolute change over the period.
    /// </summary>
    public double? AbsoluteChange { get; set; }

    /// <summary>
    /// Percentage change over the period.
    /// </summary>
    public double? PercentChange { get; set; }

    /// <summary>
    /// Highest value during the period.
    /// </summary>
    public double? High { get; set; }

    /// <summary>
    /// Lowest value during the period.
    /// </summary>
    public double? Low { get; set; }

    /// <summary>
    /// Number of data points in the period.
    /// </summary>
    public int DataPointCount { get; set; }
}
