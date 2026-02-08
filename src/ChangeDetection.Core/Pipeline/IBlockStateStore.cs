using System.Text.Json;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Persists and retrieves block execution state, keyed by (WatchId, BlockInstanceId).
/// Enables comparison blocks to access previous outputs.
/// </summary>
public interface IBlockStateStore
{
    Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default);
    Task SaveOutputAsync(string watchId, string blockInstanceId, JsonElement output, CancellationToken ct = default);
    Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(string watchId, string blockInstanceId, int maxResults = 10, CancellationToken ct = default);
}

/// <summary>
/// A snapshot of a single block execution for history queries.
/// </summary>
public record BlockExecutionSnapshot
{
    public required string WatchId { get; init; }
    public required string BlockInstanceId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required JsonElement Output { get; init; }
    public long? DurationMs { get; init; }
}
