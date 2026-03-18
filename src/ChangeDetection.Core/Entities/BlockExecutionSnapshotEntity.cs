namespace ChangeDetection.Core.Entities;

/// <summary>
/// LiteDB entity for block execution snapshots.
/// Stores the JsonElement output as a JSON string (not as BSON object)
/// because LiteDB's polymorphic deserialization is unreliable for arbitrary JSON structures.
/// </summary>
public class BlockExecutionSnapshotEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string WatchId { get; set; }
    public required string BlockInstanceId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public required string OutputJson { get; set; }
    public string? InputHash { get; set; }
    public string? PipelineDefinitionHash { get; set; }
    public long? DurationMs { get; set; }
}
