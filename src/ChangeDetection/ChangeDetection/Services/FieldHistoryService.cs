using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services;

/// <summary>
/// Service for managing field value history and computing statistics.
/// Supports stock/price tracking, charting, and trend analysis.
/// </summary>
public class FieldHistoryService(
    IRepository<FieldValueHistory> repository,
    IAlertThresholdEvaluator alertEvaluator,
    ILogger<FieldHistoryService> logger) : IFieldHistoryService
{
    /// <inheritdoc />
    public async Task RecordValueAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        string? rawValue,
        double? numericValue,
        FieldType fieldType,
        Guid snapshotId,
        string? currencyCode = null,
        string? unit = null,
        CancellationToken ct = default)
    {
        // Get previous value for change calculation
        var previousValue = await GetLatestValueAsync(watchId, objectIdentity, fieldName, ct);

        var entry = new FieldValueHistory
        {
            WatchedSiteId = watchId,
            ObjectIdentity = objectIdentity,
            FieldName = fieldName,
            RawValue = rawValue,
            NumericValue = numericValue,
            FieldType = fieldType,
            SnapshotId = snapshotId,
            CurrencyCode = currencyCode,
            Unit = unit,
            CapturedAt = DateTime.UtcNow
        };

        // Calculate change metrics if we have numeric values
        if (numericValue.HasValue && previousValue?.NumericValue.HasValue == true)
        {
            var change = numericValue.Value - previousValue.NumericValue.Value;
            entry.ChangeFromPrevious = change;

            if (previousValue.NumericValue.Value != 0)
            {
                entry.PercentChangeFromPrevious = (change / previousValue.NumericValue.Value) * 100;
            }

            entry.Direction = change switch
            {
                > 0 => ChangeDirection.Increased,
                < 0 => ChangeDirection.Decreased,
                _ => ChangeDirection.Unchanged
            };

            // Update running min/max
            entry.RunningMin = Math.Min(numericValue.Value, previousValue.RunningMin ?? numericValue.Value);
            entry.RunningMax = Math.Max(numericValue.Value, previousValue.RunningMax ?? numericValue.Value);
        }
        else if (numericValue.HasValue)
        {
            entry.RunningMin = numericValue.Value;
            entry.RunningMax = numericValue.Value;
            entry.Direction = ChangeDirection.Unknown;
        }

        await repository.InsertAsync(entry, ct);

        logger.LogDebug(
            "Recorded field history: {WatchId}/{ObjectIdentity}/{FieldName} = {Value}",
            watchId, objectIdentity, fieldName, numericValue ?? (object?)rawValue ?? "null");
    }

    /// <inheritdoc />
    public async Task RecordValuesAsync(
        Guid watchId,
        Guid snapshotId,
        IEnumerable<FieldValueRecord> values,
        CancellationToken ct = default)
    {
        foreach (var value in values)
        {
            await RecordValueAsync(
                watchId,
                value.ObjectIdentity,
                value.FieldName,
                value.RawValue,
                value.NumericValue,
                value.FieldType,
                snapshotId,
                value.CurrencyCode,
                value.Unit,
                ct);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldValueHistory>> GetHistoryAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        DateTime? from = null,
        DateTime? to = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        var all = await repository.FindAsync(
            h => h.WatchedSiteId == watchId &&
                 h.ObjectIdentity == objectIdentity &&
                 h.FieldName == fieldName,
            ct);

        IEnumerable<FieldValueHistory> filtered = all;

        if (from.HasValue)
            filtered = filtered.Where(h => h.CapturedAt >= from.Value);

        if (to.HasValue)
            filtered = filtered.Where(h => h.CapturedAt <= to.Value);

        filtered = filtered.OrderByDescending(h => h.CapturedAt);

        if (limit.HasValue)
            filtered = filtered.Take(limit.Value);

        return filtered.ToList();
    }

    /// <inheritdoc />
    public async Task<FieldStatistics?> GetStatisticsAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default)
    {
        var history = await GetHistoryAsync(watchId, objectIdentity, fieldName, from, to, ct: ct);

        if (history.Count == 0)
            return null;

        var numericValues = history
            .Where(h => h.NumericValue.HasValue)
            .Select(h => h.NumericValue!.Value)
            .ToList();

        if (numericValues.Count == 0)
            return null;

        var mean = numericValues.Average();
        var variance = numericValues.Sum(v => Math.Pow(v - mean, 2)) / numericValues.Count;
        var stdDev = Math.Sqrt(variance);

        // Calculate trend direction based on recent values
        var recentValues = numericValues.Take(5).ToList();
        var trend = TrendDirection.Stable;
        if (recentValues.Count >= 3)
        {
            var recentAvg = recentValues.Take(3).Average();
            var olderAvg = recentValues.Skip(2).Take(3).Average();
            if (recentAvg > olderAvg * 1.01) trend = TrendDirection.Up;
            else if (recentAvg < olderAvg * 0.99) trend = TrendDirection.Down;
        }

        return new FieldStatistics
        {
            WatchedSiteId = watchId,
            ObjectIdentity = objectIdentity,
            FieldName = fieldName,
            Count = numericValues.Count,
            FirstValueAt = history.MinBy(h => h.CapturedAt)?.CapturedAt,
            LastValueAt = history.MaxBy(h => h.CapturedAt)?.CapturedAt,
            FirstValue = history.MinBy(h => h.CapturedAt)?.NumericValue,
            LastValue = history.MaxBy(h => h.CapturedAt)?.NumericValue,
            Min = numericValues.Min(),
            Max = numericValues.Max(),
            Average = mean,
            Median = numericValues.OrderBy(v => v).Skip(numericValues.Count / 2).First(),
            StandardDeviation = stdDev,
            Variance = variance,
            Trend = trend
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FieldStatistics>> GetMultipleStatisticsAsync(
        Guid watchId,
        IEnumerable<(string ObjectIdentity, string FieldName)> fields,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default)
    {
        var results = new List<FieldStatistics>();

        foreach (var (objectIdentity, fieldName) in fields)
        {
            var stats = await GetStatisticsAsync(watchId, objectIdentity, fieldName, from, to, ct);
            if (stats != null)
                results.Add(stats);
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<FieldValueHistory?> GetLatestValueAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        CancellationToken ct = default)
    {
        var history = await GetHistoryAsync(watchId, objectIdentity, fieldName, limit: 1, ct: ct);
        return history.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PeriodChangeSummary>> GetPeriodSummariesAsync(
        Guid watchId,
        string objectIdentity,
        string fieldName,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var periods = new[]
        {
            (Name: "1D", Duration: TimeSpan.FromDays(1)),
            (Name: "1W", Duration: TimeSpan.FromDays(7)),
            (Name: "1M", Duration: TimeSpan.FromDays(30)),
            (Name: "3M", Duration: TimeSpan.FromDays(90)),
            (Name: "6M", Duration: TimeSpan.FromDays(180)),
            (Name: "1Y", Duration: TimeSpan.FromDays(365))
        };

        var summaries = new List<PeriodChangeSummary>();
        var latest = await GetLatestValueAsync(watchId, objectIdentity, fieldName, ct);

        if (latest?.NumericValue == null)
            return summaries;

        foreach (var (name, duration) in periods)
        {
            var fromDate = now - duration;
            var history = await GetHistoryAsync(watchId, objectIdentity, fieldName, from: fromDate, ct: ct);

            var oldest = history.MinBy(h => h.CapturedAt);
            if (oldest?.NumericValue == null)
                continue;

            var change = latest.NumericValue.Value - oldest.NumericValue.Value;
            var percentChange = oldest.NumericValue.Value != 0
                ? (change / oldest.NumericValue.Value) * 100
                : 0;

            var numericValues = history
                .Where(h => h.NumericValue.HasValue)
                .Select(h => h.NumericValue!.Value)
                .ToList();

            summaries.Add(new PeriodChangeSummary
            {
                Period = name,
                PeriodStart = fromDate,
                PeriodEnd = now,
                StartValue = oldest.NumericValue.Value,
                EndValue = latest.NumericValue.Value,
                AbsoluteChange = change,
                PercentChange = percentChange,
                High = numericValues.Count > 0 ? numericValues.Max() : latest.NumericValue.Value,
                Low = numericValues.Count > 0 ? numericValues.Min() : latest.NumericValue.Value,
                DataPointCount = history.Count
            });
        }

        return summaries;
    }

    /// <inheritdoc />
    public FieldChangeMetrics CalculateChange(
        double? previousValue,
        double? currentValue,
        FieldStatistics? statistics = null)
    {
        if (!previousValue.HasValue || !currentValue.HasValue)
        {
            return new FieldChangeMetrics
            {
                Direction = ChangeDirection.Unknown
            };
        }

        var change = currentValue.Value - previousValue.Value;
        double? percentChange = previousValue.Value != 0
            ? (change / previousValue.Value) * 100
            : null;

        double? zScore = null;
        TrendDirection trend = TrendDirection.Unknown;
        bool isNewMin = false;
        bool isNewMax = false;
        bool isOutlier = false;

        if (statistics != null)
        {
            isNewMin = currentValue.Value < statistics.Min;
            isNewMax = currentValue.Value > statistics.Max;

            if (statistics.StandardDeviation > 0 && statistics.Average.HasValue)
            {
                zScore = (currentValue.Value - statistics.Average.Value) / statistics.StandardDeviation.Value;
                isOutlier = Math.Abs(zScore.Value) > 2.0; // More than 2 std devs
            }

            trend = statistics.Trend;
        }

        return new FieldChangeMetrics
        {
            AbsoluteChange = change,
            PercentageChange = percentChange,
            Direction = change switch
            {
                > 0 => ChangeDirection.Increased,
                < 0 => ChangeDirection.Decreased,
                _ => ChangeDirection.Unchanged
            },
            IsNewMinimum = isNewMin,
            IsNewMaximum = isNewMax,
            IsOutlier = isOutlier,
            ZScore = zScore,
            Trend = trend
        };
    }

    /// <inheritdoc />
    public async Task<List<TriggeredAlert>> EvaluateAlertsAsync(
        SchemaField field,
        double? oldValue,
        double? newValue,
        FieldStatistics? statistics = null,
        CancellationToken ct = default)
    {
        var alerts = new List<TriggeredAlert>();

        if (!newValue.HasValue || field.AlertThresholds.Count == 0)
            return alerts;

        // Use the AlertThresholdEvaluator which handles all threshold types
        var result = alertEvaluator.Evaluate(field, oldValue, newValue.Value, field.BaselineValue);

        // Convert TriggeredThreshold to TriggeredAlert
        foreach (var triggered in result.TriggeredThresholds)
        {
            alerts.Add(new TriggeredAlert
            {
                ThresholdId = triggered.Threshold.Id,
                AlertName = triggered.Threshold.Name,
                ConditionType = triggered.Threshold.ConditionType,
                ThresholdValue = triggered.Threshold.Value,
                ActualValue = newValue.Value,
                TriggeredAt = DateTime.UtcNow,
                NotificationMessage = triggered.Message,
                Importance = triggered.Threshold.ImportanceOverride
            });

            // Record that the threshold was triggered (updates cooldown, trigger count)
            alertEvaluator.RecordTrigger(triggered.Threshold);
        }

        return await Task.FromResult(alerts);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TrackedFieldInfo>> GetTrackedFieldsAsync(
        Guid watchId,
        CancellationToken ct = default)
    {
        var allHistory = await repository.FindAsync(
            h => h.WatchedSiteId == watchId,
            ct);

        var grouped = allHistory
            .GroupBy(h => (h.ObjectIdentity, h.FieldName))
            .Select(g =>
            {
                var latest = g.MaxBy(h => h.CapturedAt);
                var oldest = g.MinBy(h => h.CapturedAt);
                var numericValues = g
                    .Where(h => h.NumericValue.HasValue)
                    .Select(h => h.NumericValue!.Value)
                    .ToList();

                // Calculate trend from last 5 values
                var recent = g.OrderByDescending(h => h.CapturedAt).Take(5).ToList();
                var trend = TrendDirection.Stable;
                if (recent.Count >= 3)
                {
                    var upCount = recent.Count(h => h.Direction == ChangeDirection.Increased);
                    var downCount = recent.Count(h => h.Direction == ChangeDirection.Decreased);
                    if (upCount > downCount + 1) trend = TrendDirection.Up;
                    else if (downCount > upCount + 1) trend = TrendDirection.Down;
                }

                return new TrackedFieldInfo
                {
                    ObjectIdentity = g.Key.ObjectIdentity,
                    FieldName = g.Key.FieldName,
                    FieldType = latest?.FieldType ?? FieldType.String,
                    CurrencyCode = latest?.CurrencyCode,
                    Unit = latest?.Unit,
                    ValueCount = g.Count(),
                    FirstValueAt = oldest?.CapturedAt,
                    LastValueAt = latest?.CapturedAt,
                    CurrentValue = latest?.NumericValue,
                    Trend = trend,
                    HasAlerts = false, // Would need schema access to determine
                    AlertCount = 0
                };
            })
            .ToList();

        return grouped;
    }

    /// <inheritdoc />
    public async Task UpdateBaselineAsync(
        Guid watchId,
        string fieldName,
        double baselineValue,
        CancellationToken ct = default)
    {
        // This would typically update the schema field's baseline value
        // For now, we just log this - the schema update would be done via WatchService
        logger.LogInformation(
            "Baseline update requested for {WatchId}/{FieldName}: {Value}",
            watchId, fieldName, baselineValue);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<int> PruneHistoryAsync(
        Guid watchId,
        TimeSpan retentionPeriod,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;
        var toDelete = (await repository.FindAsync(
            h => h.WatchedSiteId == watchId && h.CapturedAt < cutoff,
            ct)).ToList();

        foreach (var entry in toDelete)
        {
            await repository.DeleteAsync(entry.Id, ct);
        }

        logger.LogInformation(
            "Pruned {Count} history entries older than {Cutoff} for watch {WatchId}",
            toDelete.Count, cutoff, watchId);

        return toDelete.Count;
    }

    /// <inheritdoc />
    public async Task<int> PruneAllHistoryAsync(
        TimeSpan retentionPeriod,
        CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - retentionPeriod;
        var toDelete = (await repository.FindAsync(
            h => h.CapturedAt < cutoff,
            ct)).ToList();

        foreach (var entry in toDelete)
        {
            await repository.DeleteAsync(entry.Id, ct);
        }

        logger.LogInformation(
            "Pruned {Count} history entries older than {Cutoff} globally",
            toDelete.Count, cutoff);

        return toDelete.Count;
    }

    private static Dictionary<int, double> CalculatePercentiles(List<double> values)
    {
        if (values.Count == 0)
            return [];

        var sorted = values.OrderBy(v => v).ToList();
        var percentiles = new Dictionary<int, double>();

        foreach (var p in new[] { 10, 25, 50, 75, 90 })
        {
            var index = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
            index = Math.Max(0, Math.Min(sorted.Count - 1, index));
            percentiles[p] = sorted[index];
        }

        return percentiles;
    }
}
