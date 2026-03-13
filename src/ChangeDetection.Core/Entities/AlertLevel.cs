namespace ChangeDetection.Core.Entities;

/// <summary>
/// Alert level for tracked item notifications, determined by profile match quality.
/// Used across all watch types: job monitoring, real estate, academic papers, etc.
/// </summary>
public enum AlertLevel
{
    /// <summary>All checks pass — push notification with full details.</summary>
    High,

    /// <summary>Most checks pass, 1-2 STRETCH — alert with gap notes.</summary>
    Medium,

    /// <summary>Any hard FAIL — log only, no notification.</summary>
    Silent,

    /// <summary>Informational update (item removed, status change).</summary>
    Info
}

/// <summary>
/// Result of alert policy evaluation for a tracked item.
/// </summary>
public class AlertPolicyResult
{
    public required AlertLevel AlertLevel { get; init; }
    public required string Reason { get; init; }

    /// <summary>Per-dimension status breakdown used to determine the alert level.</summary>
    public Dictionary<string, DimensionStatus> Dimensions { get; init; } = [];

    /// <summary>Whether any deadline urgency was applied.</summary>
    public bool UrgencyApplied { get; init; }

    /// <summary>Original alert level before urgency escalation.</summary>
    public AlertLevel? PreUrgencyLevel { get; init; }

    /// <summary>Number of days until deadline, if known.</summary>
    public int? DaysUntilDeadline { get; init; }
}

/// <summary>
/// Status of a single evaluation dimension.
/// </summary>
public class DimensionStatus
{
    public required string Status { get; init; } // PASS, FAIL, STRETCH, UNKNOWN
    public float Score { get; init; }
    public string? Reason { get; init; }
}
