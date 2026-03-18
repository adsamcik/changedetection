using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Persistence;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// LiteDB-backed implementation of IBlockStateStore.
/// Stores block execution snapshots keyed by (WatchId, BlockInstanceId, RunTimestamp).
/// </summary>
public class LiteDbBlockStateStore(LiteDbContext context, ILogger<LiteDbBlockStateStore> logger) : IBlockStateStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILiteCollection<BlockExecutionSnapshotEntity> _collection = InitCollection(context);

    private static ILiteCollection<BlockExecutionSnapshotEntity> InitCollection(LiteDbContext context)
    {
        EnsureMapperConfigured();
        var collection = context.Database.GetCollection<BlockExecutionSnapshotEntity>("blockexecutionsnapshots");
        collection.EnsureIndex(x => x.WatchId);
        collection.EnsureIndex(x => x.BlockInstanceId);
        collection.EnsureIndex(x => x.Timestamp);
        collection.EnsureIndex(x => x.InputHash);
        collection.EnsureIndex(x => x.PipelineDefinitionHash);
        return collection;
    }

    private static bool _mapperConfigured;
    private static readonly object _mapperLock = new();

    private static void EnsureMapperConfigured()
    {
        if (_mapperConfigured) return;
        lock (_mapperLock)
        {
            if (_mapperConfigured) return;
            BsonMapper.Global.Entity<BlockExecutionSnapshotEntity>()
                .Id(x => x.Id)
                .Field(x => x.WatchId, "WatchId")
                .Field(x => x.BlockInstanceId, "BlockInstanceId")
                .Field(x => x.OutputJson, "OutputJson")
                .Field(x => x.InputHash, "InputHash")
                .Field(x => x.PipelineDefinitionHash, "PipelineDefinitionHash");
            _mapperConfigured = true;
        }
    }

    public async Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entity = _collection.Query()
                .Where(x => x.WatchId == watchId && x.BlockInstanceId == blockInstanceId)
                .OrderByDescending(x => x.Timestamp)
                .Limit(1)
                .FirstOrDefault();

            if (entity is null)
            {
                logger.LogDebug("No previous output found for watch {WatchId}, block {BlockInstanceId}", watchId, blockInstanceId);
                return null;
            }

            using var doc = JsonDocument.Parse(entity.OutputJson);
            return doc.RootElement.Clone();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<JsonElement?> GetCachedOutputAsync(
        string watchId,
        string blockInstanceId,
        string inputHash,
        string pipelineHash,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entity = _collection.Query()
                .Where(x =>
                    x.WatchId == watchId &&
                    x.BlockInstanceId == blockInstanceId &&
                    x.InputHash == inputHash &&
                    x.PipelineDefinitionHash == pipelineHash)
                .OrderByDescending(x => x.Timestamp)
                .Limit(1)
                .FirstOrDefault();

            if (entity is null)
            {
                logger.LogDebug(
                    "No cached output found for watch {WatchId}, block {BlockInstanceId}, input hash {InputHash}, pipeline hash {PipelineHash}",
                    watchId, blockInstanceId, inputHash, pipelineHash);
                return null;
            }

            using var doc = JsonDocument.Parse(entity.OutputJson);
            return doc.RootElement.Clone();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveOutputAsync(
        string watchId,
        string blockInstanceId,
        JsonElement output,
        string? inputHash = null,
        string? pipelineHash = null,
        CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entity = new BlockExecutionSnapshotEntity
            {
                WatchId = watchId,
                BlockInstanceId = blockInstanceId,
                Timestamp = DateTime.UtcNow,
                OutputJson = System.Text.Json.JsonSerializer.Serialize(output),
                InputHash = inputHash,
                PipelineDefinitionHash = pipelineHash
            };

            _collection.Insert(entity);
            logger.LogDebug(
                "Saved output for watch {WatchId}, block {BlockInstanceId}, input hash {InputHash}, pipeline hash {PipelineHash}",
                watchId, blockInstanceId, inputHash, pipelineHash);

            // Prune old snapshots to prevent unbounded growth
            var allSnapshots = _collection.Find(s => s.WatchId == watchId && s.BlockInstanceId == blockInstanceId)
                .OrderByDescending(s => s.Timestamp)
                .Skip(50)
                .ToList();

            foreach (var old in allSnapshots)
            {
                _collection.Delete(old.Id);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(string watchId, string blockInstanceId, int maxResults = 10, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var entities = _collection.Query()
                .Where(x => x.WatchId == watchId && x.BlockInstanceId == blockInstanceId)
                .OrderByDescending(x => x.Timestamp)
                .Limit(maxResults)
                .ToList();

            var snapshots = entities.Select(e =>
            {
                using var doc = JsonDocument.Parse(e.OutputJson);
                return new BlockExecutionSnapshot
                {
                    WatchId = e.WatchId,
                    BlockInstanceId = e.BlockInstanceId,
                    Timestamp = e.Timestamp,
                    Output = doc.RootElement.Clone(),
                    DurationMs = e.DurationMs
                };
            }).ToList();

            return snapshots;
        }
        finally
        {
            _lock.Release();
        }
    }
}
