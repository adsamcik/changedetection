using System.Text.Json;

namespace ChangeDetection.Core.Pipeline;

/// <summary>
/// Status of a block's execution within a pipeline run.
/// </summary>
public enum BlockExecutionStatus
{
    Completed,
    Failed,
    Skipped,
    Baseline
}

/// <summary>
/// Wrapper for a pipeline block's execution output.
/// Use static factory methods to create instances.
/// </summary>
public record BlockResult
{
    public required bool Success { get; init; }
    public JsonElement? Output { get; init; }
    public string? Error { get; init; }
    public required BlockExecutionStatus Status { get; init; }
    public string? SkipReason { get; init; }
    public bool CacheHit { get; init; }

    /// <summary>Creates a successful result with output data.</summary>
    public static BlockResult Succeeded(JsonElement output) => new()
    {
        Success = true,
        Output = output,
        Status = BlockExecutionStatus.Completed
    };

    /// <summary>Creates a failed result with an error message.</summary>
    public static BlockResult Failed(string error) => new()
    {
        Success = false,
        Error = error,
        Status = BlockExecutionStatus.Failed
    };

    /// <summary>Creates a skipped result with a reason.</summary>
    public static BlockResult Skip(string reason) => new()
    {
        Success = true,
        Status = BlockExecutionStatus.Skipped,
        SkipReason = reason
    };

    /// <summary>Creates a baseline capture result for first-run scenarios.</summary>
    public static BlockResult BaselineCapture(JsonElement output) => new()
    {
        Success = true,
        Output = output,
        Status = BlockExecutionStatus.Baseline
    };

    /// <summary>Creates a successful completed result from cached output.</summary>
    public static BlockResult CachedResult(JsonElement output) => new()
    {
        Success = true,
        Output = output,
        Status = BlockExecutionStatus.Completed,
        CacheHit = true
    };
}
