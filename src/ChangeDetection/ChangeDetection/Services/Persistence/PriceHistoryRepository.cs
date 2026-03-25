using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using LiteDB;

namespace ChangeDetection.Services.Persistence;

/// <summary>
/// LiteDB repository for price history entries.
/// Optimized for time-series queries and chart data generation.
/// All operations are serialized through <see cref="ThreadSafeLiteDbContext"/>.
/// </summary>
public class PriceHistoryRepository : IPriceHistoryRepository
{
    private readonly ThreadSafeLiteDbContext _safeContext;
    private const string CollectionName = "price_history";

    public PriceHistoryRepository(ThreadSafeLiteDbContext safeContext)
    {
        _safeContext = safeContext;
    }

    private static ILiteCollection<PriceHistoryEntry> Col(ILiteDatabase db) =>
        db.GetCollection<PriceHistoryEntry>(CollectionName);

    public async Task<PriceHistoryEntry> AddAsync(PriceHistoryEntry entry, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        if (entry.Id == Guid.Empty)
            entry.Id = Guid.NewGuid();
        
        if (entry.Timestamp == default)
            entry.Timestamp = DateTime.UtcNow;
        
        await _safeContext.ExecuteAsync(db => { Col(db).Insert(entry); }, ct);
        return entry;
    }

    public async Task AddRangeAsync(IEnumerable<PriceHistoryEntry> entries, CancellationToken ct = default)
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
        
        await _safeContext.ExecuteAsync(db => { Col(db).InsertBulk(entryList); }, ct);
    }

    public async Task<PriceHistoryEntry?> GetLatestAsync(
        Guid watchId,
        string fieldName,
        string? objectIdentityKey = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        return await _safeContext.ExecuteAsync(db =>
        {
            var query = Col(db).Query()
                .Where(x => x.WatchId == watchId && x.FieldName == fieldName);
            
            if (objectIdentityKey != null)
                query = query.Where(x => x.ObjectIdentityKey == objectIdentityKey);
            
            return query
                .OrderByDescending(x => x.Timestamp)
                .Limit(1)
                .FirstOrDefault();
        }, ct);
    }

    public async Task<IReadOnlyList<PriceHistoryEntry>> GetHistoryAsync(
        Guid watchId,
        string fieldName,
        DateTime? from = null,
        DateTime? to = null,
        string? objectIdentityKey = null,
        int? limit = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        return await _safeContext.ExecuteAsync(db =>
        {
            var query = Col(db).Query()
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
            
            return (IReadOnlyList<PriceHistoryEntry>)results;
        }, ct);
    }

    public async Task<IReadOnlyList<PriceHistoryEntry>> GetAllForWatchAsync(
        Guid watchId,
        DateTime? from = null,
        DateTime? to = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        return await _safeContext.ExecuteAsync(db =>
        {
            var query = Col(db).Query()
                .Where(x => x.WatchId == watchId);
            
            if (from.HasValue)
                query = query.Where(x => x.Timestamp >= from.Value);
            
            if (to.HasValue)
                query = query.Where(x => x.Timestamp <= to.Value);
            
            return (IReadOnlyList<PriceHistoryEntry>)query
                .OrderByDescending(x => x.Timestamp)
                .ToList();
        }, ct);
    }

    public async Task<(decimal? Min, DateTime? MinAt, decimal? Max, DateTime? MaxAt)> GetMinMaxAsync(
        Guid watchId,
        string fieldName,
        string? objectIdentityKey = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        return await _safeContext.ExecuteAsync(db =>
        {
            var query = Col(db).Query()
                .Where(x => x.WatchId == watchId && x.FieldName == fieldName);
            
            if (objectIdentityKey != null)
                query = query.Where(x => x.ObjectIdentityKey == objectIdentityKey);
            
            var entries = query.ToList();
            
            if (entries.Count == 0)
                return (default(decimal?), default(DateTime?), default(decimal?), default(DateTime?));
            
            var minEntry = entries.MinBy(x => x.Value);
            var maxEntry = entries.MaxBy(x => x.Value);
            
            return ((decimal?, DateTime?, decimal?, DateTime?))(
                minEntry?.Value,
                minEntry?.Timestamp,
                maxEntry?.Value,
                maxEntry?.Timestamp
            );
        }, ct);
    }

    public async Task DeleteForWatchAsync(Guid watchId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _safeContext.ExecuteAsync(db => { Col(db).DeleteMany(x => x.WatchId == watchId); }, ct);
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _safeContext.ExecuteAsync(db =>
            Col(db).DeleteMany(x => x.Timestamp < cutoffDate), ct);
    }

    public async Task<IReadOnlyList<PriceHistoryDataPoint>> GetChartDataAsync(
        Guid watchId,
        string fieldName,
        DateTime from,
        DateTime to,
        ChartInterval interval = ChartInterval.Auto,
        string? objectIdentityKey = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        
        var entries = await _safeContext.ExecuteAsync(db =>
        {
            var query = Col(db).Query()
                .Where(x => x.WatchId == watchId && 
                            x.FieldName == fieldName && 
                            x.Timestamp >= from && 
                            x.Timestamp <= to);
            
            if (objectIdentityKey != null)
                query = query.Where(x => x.ObjectIdentityKey == objectIdentityKey);
            
            return query
                .OrderBy(x => x.Timestamp)
                .ToList();
        }, ct);
        
        if (entries.Count == 0)
            return [];
        
        // Determine interval if auto
        var actualInterval = interval == ChartInterval.Auto
            ? DetermineOptimalInterval(from, to)
            : interval;
        
        if (actualInterval == ChartInterval.Raw)
        {
            var rawPoints = entries
                .Select(e => new PriceHistoryDataPoint(e.Timestamp, e.Value, null, null, 1))
                .ToList();
            return rawPoints;
        }
        
        return AggregateByInterval(entries, actualInterval);
    }

    private static ChartInterval DetermineOptimalInterval(DateTime from, DateTime to)
    {
        var range = to - from;
        
        return range.TotalDays switch
        {
            <= 2 => ChartInterval.Raw,
            <= 14 => ChartInterval.Hourly,
            <= 90 => ChartInterval.Daily,
            <= 365 => ChartInterval.Weekly,
            _ => ChartInterval.Monthly
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
