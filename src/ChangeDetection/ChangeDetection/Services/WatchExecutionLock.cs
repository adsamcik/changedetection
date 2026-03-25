using System.Collections.Concurrent;

namespace ChangeDetection.Services;

/// <summary>
/// Prevents concurrent execution of the same watch from both the background service
/// and manual trigger endpoints.
/// </summary>
public interface IWatchExecutionLock
{
    /// <summary>Attempts to acquire the execution lock for a watch. Returns true if acquired.</summary>
    bool TryAcquire(Guid watchId);

    /// <summary>Releases the execution lock for a watch.</summary>
    void Release(Guid watchId);

    /// <summary>Checks whether a watch is currently being executed.</summary>
    bool IsRunning(Guid watchId);
}

/// <summary>
/// Thread-safe singleton implementation backed by a ConcurrentDictionary.
/// </summary>
public class WatchExecutionLock : IWatchExecutionLock
{
    private readonly ConcurrentDictionary<Guid, byte> _runningWatches = new();

    public bool TryAcquire(Guid watchId) => _runningWatches.TryAdd(watchId, 0);

    public void Release(Guid watchId) => _runningWatches.TryRemove(watchId, out _);

    public bool IsRunning(Guid watchId) => _runningWatches.ContainsKey(watchId);
}
