namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// Health summary of the most recent pipeline execution for a watch.
/// </summary>
public class PipelineHealthDto
{
    /// <summary>Whether the pipeline completed without critical failures.</summary>
    public bool Success { get; set; }

    /// <summary>Whether any analysis-tier blocks were skipped due to failures.</summary>
    public bool IsDegraded { get; set; }

    /// <summary>Total execution time in milliseconds.</summary>
    public long ExecutionDurationMs { get; set; }

    /// <summary>When the pipeline was last executed.</summary>
    public DateTime? ExecutedAt { get; set; }

    /// <summary>Pipeline-level error message (null on success).</summary>
    public string? Error { get; set; }

    /// <summary>Total number of blocks in the pipeline.</summary>
    public int BlockCount { get; set; }

    /// <summary>Blocks that completed successfully.</summary>
    public int CompletedCount { get; set; }

    /// <summary>Blocks that failed.</summary>
    public int FailedCount { get; set; }

    /// <summary>Blocks that were skipped.</summary>
    public int SkippedCount { get; set; }

    /// <summary>Per-block execution status details.</summary>
    public List<PipelineBlockStatusDto> Blocks { get; set; } = [];
}

/// <summary>
/// Execution status of a single pipeline block.
/// </summary>
public class PipelineBlockStatusDto
{
    /// <summary>Block instance ID within the pipeline.</summary>
    public string BlockId { get; set; } = "";

    /// <summary>Block type (e.g. Navigate, ExtractSchema, ListDiff, Output).</summary>
    public string BlockType { get; set; } = "";

    /// <summary>Execution status: Completed, Failed, Skipped, Baseline.</summary>
    public string Status { get; set; } = "";

    /// <summary>Block execution time in milliseconds (null if skipped).</summary>
    public long? DurationMs { get; set; }

    /// <summary>Size of block output in characters (null if no output).</summary>
    public int? OutputSizeChars { get; set; }

    /// <summary>Error message (only for failed blocks).</summary>
    public string? Error { get; set; }

    /// <summary>Whether the block result was served from cache.</summary>
    public bool CacheHit { get; set; }
}

/// <summary>
/// Per-portal pipeline health indicator for the group results view.
/// </summary>
public class PortalPipelineHealthDto
{
    /// <summary>The watch (portal) ID.</summary>
    public string WatchId { get; set; } = "";

    /// <summary>Whether the last pipeline run succeeded.</summary>
    public bool Success { get; set; }

    /// <summary>Whether the pipeline is in a degraded state.</summary>
    public bool IsDegraded { get; set; }

    /// <summary>Number of items extracted in the latest snapshot.</summary>
    public int ItemCount { get; set; }

    /// <summary>When the pipeline was last executed.</summary>
    public DateTime? LastExecuted { get; set; }

    /// <summary>Number of blocks that completed successfully.</summary>
    public int CompletedBlocks { get; set; }

    /// <summary>Number of blocks that failed.</summary>
    public int FailedBlocks { get; set; }

    /// <summary>Pipeline-level error (null on success).</summary>
    public string? Error { get; set; }
}
