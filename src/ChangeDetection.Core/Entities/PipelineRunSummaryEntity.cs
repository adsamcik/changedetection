namespace ChangeDetection.Core.Entities;

/// <summary>
/// Persisted summary of the most recent pipeline execution for a watch.
/// One record per watch, overwritten on each run.
/// </summary>
public class PipelineRunSummaryEntity
{
    /// <summary>Primary key — the watch ID (one summary per watch).</summary>
    public required string WatchId { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public bool Success { get; set; }
    public bool IsDegraded { get; set; }
    public long ExecutionDurationMs { get; set; }
    public string? Error { get; set; }

    /// <summary>JSON-serialized list of <see cref="PipelineBlockSummary"/>.</summary>
    public string BlockSummariesJson { get; set; } = "[]";
}

/// <summary>
/// Lightweight per-block status captured from a pipeline run.
/// Serialized as JSON inside <see cref="PipelineRunSummaryEntity.BlockSummariesJson"/>.
/// </summary>
public class PipelineBlockSummary
{
    public string BlockId { get; set; } = "";
    public string BlockType { get; set; } = "";
    public string Status { get; set; } = "";
    public long? DurationMs { get; set; }
    public int? OutputSizeChars { get; set; }
    public string? Error { get; set; }
    public bool CacheHit { get; set; }
}
