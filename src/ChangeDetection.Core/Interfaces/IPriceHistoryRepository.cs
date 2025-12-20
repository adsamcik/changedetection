using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Repository for storing and querying price/value history entries.
/// Stored in a separate LiteDB collection for efficient time-series operations.
/// </summary>
public interface IPriceHistoryRepository
{
    /// <summary>
    /// Adds a new price history entry.
    /// </summary>
    Task<PriceHistoryEntry> AddAsync(PriceHistoryEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Adds multiple price history entries in a batch.
    /// </summary>
    Task AddRangeAsync(IEnumerable<PriceHistoryEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Gets the most recent entry for a watch and field.
    /// </summary>
    Task<PriceHistoryEntry?> GetLatestAsync(
        Guid watchId,
        string fieldName,
        string? objectIdentityKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets price history for a watch within a time range.
    /// </summary>
    Task<IReadOnlyList<PriceHistoryEntry>> GetHistoryAsync(
        Guid watchId,
        string fieldName,
        DateTime? from = null,
        DateTime? to = null,
        string? objectIdentityKey = null,
        int? limit = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all history entries for a watch (all fields).
    /// </summary>
    Task<IReadOnlyList<PriceHistoryEntry>> GetAllForWatchAsync(
        Guid watchId,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the minimum and maximum values recorded for a field.
    /// </summary>
    Task<(decimal? Min, DateTime? MinAt, decimal? Max, DateTime? MaxAt)> GetMinMaxAsync(
        Guid watchId,
        string fieldName,
        string? objectIdentityKey = null,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes all history entries for a watch.
    /// Called when a watch is deleted.
    /// </summary>
    Task DeleteForWatchAsync(Guid watchId, CancellationToken ct = default);

    /// <summary>
    /// Deletes history entries older than the specified date.
    /// Used for data retention cleanup.
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default);

    /// <summary>
    /// Gets aggregated data points for charting (downsampled for large ranges).
    /// Returns one point per interval (hour/day/week depending on range).
    /// </summary>
    Task<IReadOnlyList<PriceHistoryDataPoint>> GetChartDataAsync(
        Guid watchId,
        string fieldName,
        DateTime from,
        DateTime to,
        ChartInterval interval = ChartInterval.Auto,
        string? objectIdentityKey = null,
        CancellationToken ct = default);
}

/// <summary>
/// A data point for charting, potentially aggregated from multiple entries.
/// </summary>
public record PriceHistoryDataPoint(
    DateTime Timestamp,
    decimal Value,
    decimal? Min,
    decimal? Max,
    int SampleCount);

/// <summary>
/// Interval for chart data aggregation.
/// </summary>
public enum ChartInterval
{
    /// <summary>Automatically choose based on time range.</summary>
    Auto,

    /// <summary>One point per hour.</summary>
    Hourly,

    /// <summary>One point per day.</summary>
    Daily,

    /// <summary>One point per week.</summary>
    Weekly,

    /// <summary>One point per month.</summary>
    Monthly,

    /// <summary>Raw data points (no aggregation).</summary>
    Raw
}
