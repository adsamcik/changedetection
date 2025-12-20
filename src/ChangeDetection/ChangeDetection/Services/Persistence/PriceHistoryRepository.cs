using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB repository for price history entries.
/// Optimized for time-series queries and chart data generation.
/// </summary>
public class PriceHistoryRepository : IPriceHistoryRepository
{
    private readonly ILiteCollection<PriceHistoryEntry> _collection;
    private const string CollectionName = "price_history";

    public PriceHistoryRepository(LiteDbContext context)
    {
        _collection = context.Database.GetCollection<PriceHistoryEntry>(CollectionName);
        ConfigureIndexes();
    }

    private void ConfigureIndexes()
    {
        // Composite indexes for efficient queries
        _collection.EnsureIndex(x => x.WatchId);
        _collection.EnsureIndex(x => x.FieldName);
        _collection.EnsureIndex(x => x.Timestamp);
        _collection.EnsureIndex(x => x.ObjectIdentityKey);
        
        // Compound index for the most common query pattern
        _collection.EnsureIndex("WatchId_FieldName_Timestamp", 
            BsonExpression.Create("{ WatchId: $.WatchId, FieldName: $.FieldName, Timestamp: $.Timestamp }"));
    }

    public Task<PriceHistoryEntry> AddAsync(PriceHistoryEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        if (entry.Id == Guid.Empty)
            entry.Id = Guid.NewGuid();
        
        if (entry.Timestamp == default)
            entry.Timestamp = DateTime.UtcNow;
        
        _collection.Insert(entry);
        return Task.FromResult(entry);
    }

    public Task AddRangeAsync(IEnumerable<PriceHistoryEntry> entries, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var entryList = entries.ToList();
        foreach (var entry in entryList)
        {
            if (entry.Id == Guid.Empty)
                entry.Id = Guid.NewGuid();
            if (entry.Timestamp == default)
                entry.Timestamp = DateTime.UtcNow;
        }
        
        _collection.InsertBulk(entryList);
        return Task.CompletedTask;
    }

    public Task<PriceHistoryEntry?> GetLatestAsync(
        Guid watchId,
        string fieldName,
        string? objectIdentityKey = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var query = _collection.Query()
            .Where(x => x.WatchId == watchId && x.FieldName == fieldName);
        
        if (objectIdentityKey != null)
            query = query.Where(x => x.ObjectIdentityKey == objectIdentityKey);
        
        var result = query
            .OrderByDescending(x => x.Timestamp)
            .Limit(1)
            .FirstOrDefault();
        
        return Task.FromResult<PriceHistoryEntry?>(result);
    }

    public Task<IReadOnlyList<PriceHistoryEntry>> GetHistoryAsync(
        Guid watchId,
        string fieldName,
        DateTime? from = null,
        DateTime? to = null,
        string? objectIdentityKey = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var query = _collection.Query()
            .Where(x => x.WatchId == watchId && x.FieldName == fieldName);
        
        if (from.HasValue)
            query = query.Where(x => x.Timestamp >= from.Value);
        
        if (to.HasValue)
            query = query.Where(x => x.Timestamp <= to.Value);
        
        if (objectIdentityKey != null)
            query = query.Where(x => x.ObjectIdentityKey == objectIdentityKey);
        
        var orderedQuery = query.OrderByDescending(x => x.Timestamp);
        
        var results = limit.HasValue 
            ? orderedQuery.Limit(limit.Value).ToList()
            : orderedQuery.ToList();
        
        return Task.FromResult<IReadOnlyList<PriceHistoryEntry>>(results);
    }

    public Task<IReadOnlyList<PriceHistoryEntry>> GetAllForWatchAsync(
        Guid watchId,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var query = _collection.Query()
            .Where(x => x.WatchId == watchId);
        
        if (from.HasValue)
            query = query.Where(x => x.Timestamp >= from.Value);
        
        if (to.HasValue)
            query = query.Where(x => x.Timestamp <= to.Value);
        
        var results = query
            .OrderByDescending(x => x.Timestamp)
            .ToList();
        
        return Task.FromResult<IReadOnlyList<PriceHistoryEntry>>(results);
    }

    public Task<(decimal? Min, DateTime? MinAt, decimal? Max, DateTime? MaxAt)> GetMinMaxAsync(
        Guid watchId,
        string fieldName,
        string? objectIdentityKey = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var query = _collection.Query()
            .Where(x => x.WatchId == watchId && x.FieldName == fieldName);
        
        if (objectIdentityKey != null)
            query = query.Where(x => x.ObjectIdentityKey == objectIdentityKey);
        
        var entries = query.ToList();
        
        if (entries.Count == 0)
            return Task.FromResult<(decimal?, DateTime?, decimal?, DateTime?)>((null, null, null, null));
        
        var minEntry = entries.MinBy(x => x.Value);
        var maxEntry = entries.MaxBy(x => x.Value);
        
        return Task.FromResult<(decimal?, DateTime?, decimal?, DateTime?)>((
            minEntry?.Value,
            minEntry?.Timestamp,
            maxEntry?.Value,
            maxEntry?.Timestamp
        ));
    }

    public Task DeleteForWatchAsync(Guid watchId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _collection.DeleteMany(x => x.WatchId == watchId);
        return Task.CompletedTask;
    }

    public Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var count = _collection.DeleteMany(x => x.Timestamp < cutoffDate);
        return Task.FromResult(count);
    }

    public Task<IReadOnlyList<PriceHistoryDataPoint>> GetChartDataAsync(
        Guid watchId,
        string fieldName,
        DateTime from,
        DateTime to,
        ChartInterval interval = ChartInterval.Auto,
        string? objectIdentityKey = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        // Get raw data first
        var query = _collection.Query()
            .Where(x => x.WatchId == watchId && 
                        x.FieldName == fieldName && 
                        x.Timestamp >= from && 
                        x.Timestamp <= to);
        
        if (objectIdentityKey != null)
            query = query.Where(x => x.ObjectIdentityKey == objectIdentityKey);
        
        var entries = query
            .OrderBy(x => x.Timestamp)
            .ToList();
        
        if (entries.Count == 0)
            return Task.FromResult<IReadOnlyList<PriceHistoryDataPoint>>([]);
        
        // Determine interval if auto
        var actualInterval = interval == ChartInterval.Auto
            ? DetermineOptimalInterval(from, to)
            : interval;
        
        if (actualInterval == ChartInterval.Raw)
        {
            // Return raw data points
            var rawPoints = entries
                .Select(e => new PriceHistoryDataPoint(e.Timestamp, e.Value, null, null, 1))
                .ToList();
            return Task.FromResult<IReadOnlyList<PriceHistoryDataPoint>>(rawPoints);
        }
        
        // Aggregate data points by interval
        var aggregatedPoints = AggregateByInterval(entries, actualInterval);
        return Task.FromResult<IReadOnlyList<PriceHistoryDataPoint>>(aggregatedPoints);
    }

    private static ChartInterval DetermineOptimalInterval(DateTime from, DateTime to)
    {
        var range = to - from;
        
        return range.TotalDays switch
        {
            <= 2 => ChartInterval.Raw,      // Up to 2 days: show raw data
            <= 14 => ChartInterval.Hourly,  // Up to 2 weeks: hourly
            <= 90 => ChartInterval.Daily,   // Up to 3 months: daily
            <= 365 => ChartInterval.Weekly, // Up to 1 year: weekly
            _ => ChartInterval.Monthly      // Over 1 year: monthly
        };
    }

    private static List<PriceHistoryDataPoint> AggregateByInterval(
        List<PriceHistoryEntry> entries,
        ChartInterval interval)
    {
        var groups = interval switch
        {
            ChartInterval.Hourly => entries.GroupBy(e => new DateTime(
                e.Timestamp.Year, e.Timestamp.Month, e.Timestamp.Day, e.Timestamp.Hour, 0, 0, DateTimeKind.Utc)),
            
            ChartInterval.Daily => entries.GroupBy(e => new DateTime(
                e.Timestamp.Year, e.Timestamp.Month, e.Timestamp.Day, 0, 0, 0, DateTimeKind.Utc)),
            
            ChartInterval.Weekly => entries.GroupBy(e => 
                new DateTime(e.Timestamp.Year, e.Timestamp.Month, e.Timestamp.Day, 0, 0, 0, DateTimeKind.Utc)
                    .AddDays(-(int)e.Timestamp.DayOfWeek)),
            
            ChartInterval.Monthly => entries.GroupBy(e => new DateTime(
                e.Timestamp.Year, e.Timestamp.Month, 1, 0, 0, 0, DateTimeKind.Utc)),
            
            _ => entries.GroupBy(e => e.Timestamp)
        };

        return groups
            .Select(g =>
            {
                var values = g.Select(e => e.Value).ToList();
                return new PriceHistoryDataPoint(
                    Timestamp: g.Key,
                    Value: values.Average(),
                    Min: values.Min(),
                    Max: values.Max(),
                    SampleCount: values.Count
                );
            })
            .OrderBy(p => p.Timestamp)
            .ToList();
    }
}
