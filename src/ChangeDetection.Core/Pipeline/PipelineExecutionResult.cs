using System.Text.Json;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Result of executing an entire pipeline run.
/// </summary>
public record PipelineExecutionResult
{
    /// <summary>Did the pipeline complete without Infrastructure/Extraction failures?</summary>
    public required bool Success { get; init; }

    /// <summary>Per-block results keyed by block instance ID.</summary>
    public required IReadOnlyDictionary<string, BlockResult> BlockResults { get; init; }

    /// <summary>The final Output block's data (convenience accessor).</summary>
    public JsonElement? OutputData { get; init; }

    /// <summary>Pipeline-level error message if aborted.</summary>
    public string? Error { get; init; }

    /// <summary>Total execution time in milliseconds.</summary>
    public required long ExecutionDurationMs { get; init; }

    /// <summary>True if this was a first run (baseline capture).</summary>
    public required bool WasBaseline { get; init; }

    /// <summary>True if any Analysis-tier block was skipped due to failure.</summary>
    public required bool IsDegraded { get; init; }

    /// <summary>Blocks that were skipped (downstream of failed/condition blocks).</summary>
    public required IReadOnlyList<string> SkippedBlockIds { get; init; }
}
