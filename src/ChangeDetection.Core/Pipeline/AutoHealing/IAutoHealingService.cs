namespace ChangeDetection.Core.Pipeline.AutoHealing;

/// <summary>
/// Service that attempts to automatically heal failed pipeline blocks
/// through a progressive 3-layer approach.
/// </summary>
public interface IAutoHealingService
{
    /// <summary>
    /// Attempt to heal a failed block. Returns updated BlockDefinition if successful.
    /// </summary>
    Task<HealingResult> AttemptHealAsync(
        HealingContext context, CancellationToken ct = default);
}

/// <summary>
/// Context for a healing attempt, containing all information about the failure.
/// </summary>
public record HealingContext
{
    /// <summary>The watch that owns the failing pipeline.</summary>
    public required Guid WatchId { get; init; }

    /// <summary>Instance ID of the block that failed (e.g., "filter-1").</summary>
    public required string BlockInstanceId { get; init; }

    /// <summary>Block type discriminator (e.g., "Filter", "ExtractSchema").</summary>
    public required string BlockType { get; init; }

    /// <summary>Error message from the failed block execution.</summary>
    public required string ErrorMessage { get; init; }

    /// <summary>Number of consecutive failures for this block.</summary>
    public required int ConsecutiveFailures { get; init; }

    /// <summary>The full pipeline definition containing the failing block.</summary>
    public required PipelineDefinition Pipeline { get; init; }

    /// <summary>Current HTML of the page (if available).</summary>
    public string? CurrentHtml { get; init; }

    /// <summary>HTML snapshot from when the watch was first set up.</summary>
    public string? SetupTimeHtml { get; init; }

    /// <summary>Latest successful HTML snapshot from a prior run, if available.</summary>
    public string? LatestSuccessfulHtml { get; init; }

    /// <summary>Service provider for resolving services during healing (e.g., IRepository, INotificationService).</summary>
    public IServiceProvider? Services { get; init; }
}

/// <summary>
/// Result of a healing attempt.
/// </summary>
public record HealingResult
{
    /// <summary>Outcome of the healing attempt.</summary>
    public required HealingOutcome Outcome { get; init; }

    /// <summary>Human-readable description of what happened.</summary>
    public string? Message { get; init; }

    /// <summary>Updated block definition if healing succeeded (Layer 1).</summary>
    public BlockDefinition? UpdatedBlock { get; init; }

    /// <summary>Updated pipeline definition if healing required pipeline-level changes (Layer 2).</summary>
    public PipelineDefinition? UpdatedPipeline { get; init; }
}

/// <summary>
/// Outcome of an auto-healing attempt.
/// </summary>
public enum HealingOutcome
{
    /// <summary>Block config updated, retry should work.</summary>
    Healed,

    /// <summary>Problem identified and fixed at pipeline level.</summary>
    DiagnosedFixable,

    /// <summary>Can't auto-fix, user notification needed.</summary>
    RequiresUser,

    /// <summary>Below failure threshold, just retry normally.</summary>
    NoActionNeeded
}

/// <summary>
/// Configurable thresholds for triggering each healing layer.
/// </summary>
public record HealingThresholds
{
    /// <summary>Consecutive failures before Layer 1 activates.</summary>
    public int Layer1Threshold { get; init; } = 3;

    /// <summary>Maximum Layer 1 attempts before escalating.</summary>
    public int Layer1MaxAttempts { get; init; } = 2;

    /// <summary>Consecutive failures before Layer 2 activates.</summary>
    public int Layer2Threshold { get; init; } = 6;

    /// <summary>Maximum Layer 2 attempts before escalating to user.</summary>
    public int Layer2MaxAttempts { get; init; } = 1;
}
