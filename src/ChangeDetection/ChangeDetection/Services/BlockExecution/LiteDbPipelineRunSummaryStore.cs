using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Persistence;
using LiteDB;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// LiteDB-backed store for the most recent pipeline run summary per watch.
/// </summary>
public class LiteDbPipelineRunSummaryStore : IPipelineRunSummaryStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILiteCollection<PipelineRunSummaryEntity> _collection;

    public LiteDbPipelineRunSummaryStore(LiteDbContext context)
    {
        EnsureMapperConfigured();
        _collection = context.Database.GetCollection<PipelineRunSummaryEntity>("pipelinerunsummaries");
        _collection.EnsureIndex(x => x.WatchId, unique: true);
    }

    private static bool _mapperConfigured;
    private static readonly object _mapperLock = new();

    private static void EnsureMapperConfigured()
    {
        if (_mapperConfigured) return;
        lock (_mapperLock)
        {
            if (_mapperConfigured) return;
            BsonMapper.Global.Entity<PipelineRunSummaryEntity>()
                .Id(x => x.WatchId)
                .Field(x => x.Timestamp, "Timestamp")
                .Field(x => x.Success, "Success")
                .Field(x => x.IsDegraded, "IsDegraded")
                .Field(x => x.ExecutionDurationMs, "ExecutionDurationMs")
                .Field(x => x.Error, "Error")
                .Field(x => x.BlockSummariesJson, "BlockSummariesJson");
            _mapperConfigured = true;
        }
    }

    public Task SaveAsync(PipelineRunSummaryEntity summary, CancellationToken ct = default)
    {
        return WrapWithLockAsync(() => _collection.Upsert(summary), ct);
    }

    public Task<PipelineRunSummaryEntity?> GetAsync(string watchId, CancellationToken ct = default)
    {
        return WrapWithLockAsync(() => (PipelineRunSummaryEntity?)_collection.FindById(watchId), ct);
    }

    public Task<IReadOnlyDictionary<string, PipelineRunSummaryEntity>> GetBatchAsync(
        IEnumerable<string> watchIds, CancellationToken ct = default)
    {
        return WrapWithLockAsync<IReadOnlyDictionary<string, PipelineRunSummaryEntity>>(() =>
        {
            var ids = watchIds.ToHashSet();
            return _collection.Find(x => ids.Contains(x.WatchId))
                .ToDictionary(x => x.WatchId);
        }, ct);
    }

    private async Task WrapWithLockAsync(Action action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            action();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<T> WrapWithLockAsync<T>(Func<T> func, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return func();
        }
        finally
        {
            _lock.Release();
        }
    }
}
