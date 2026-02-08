using System.Collections.Concurrent;

namespace ChangeDetection.Services.AutoHealing;

/// <summary>
/// Tracks consecutive failures per block for triggering healing layers.
/// </summary>
public interface IFailureTracker
{
    /// <summary>Records a failure and returns the new consecutive failure count.</summary>
    Task<int> RecordFailureAsync(Guid watchId, string blockInstanceId, string error, CancellationToken ct = default);

    /// <summary>Resets the failure count (called after a successful execution).</summary>
    Task ResetFailuresAsync(Guid watchId, string blockInstanceId, CancellationToken ct = default);

    /// <summary>Gets the current consecutive failure count.</summary>
    Task<int> GetConsecutiveFailuresAsync(Guid watchId, string blockInstanceId, CancellationToken ct = default);
}

/// <summary>
/// In-memory failure tracking using ConcurrentDictionary.
/// Thread-safe for concurrent pipeline executions.
/// </summary>
public class FailureTracker : IFailureTracker
{
    private readonly ConcurrentDictionary<string, FailureEntry> _failures = new();

    private static string Key(Guid watchId, string blockId) => $"{watchId}:{blockId}";

    public Task<int> RecordFailureAsync(Guid watchId, string blockInstanceId, string error, CancellationToken ct = default)
    {
        var key = Key(watchId, blockInstanceId);
        var entry = _failures.AddOrUpdate(key,
            _ => new FailureEntry(1, error, DateTime.UtcNow),
            (_, existing) => existing with { Count = existing.Count + 1, LastError = error, LastFailure = DateTime.UtcNow });
        return Task.FromResult(entry.Count);
    }

    public Task ResetFailuresAsync(Guid watchId, string blockInstanceId, CancellationToken ct = default)
    {
        _failures.TryRemove(Key(watchId, blockInstanceId), out _);
        return Task.CompletedTask;
    }

    public Task<int> GetConsecutiveFailuresAsync(Guid watchId, string blockInstanceId, CancellationToken ct = default)
    {
        if (_failures.TryGetValue(Key(watchId, blockInstanceId), out var entry))
            return Task.FromResult(entry.Count);
        return Task.FromResult(0);
    }

    /// <summary>Removes entries older than the specified TTL. Call periodically to prevent unbounded growth.</summary>
    public int CleanupStaleEntries(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var kvp in _failures)
        {
            if (kvp.Value.LastFailure < cutoff)
            {
                if (_failures.TryRemove(kvp.Key, out _))
                    removed++;
            }
        }
        return removed;
    }

    private sealed record FailureEntry(int Count, string LastError, DateTime LastFailure);
}
