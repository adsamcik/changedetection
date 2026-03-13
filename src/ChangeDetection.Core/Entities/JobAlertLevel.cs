namespace ChangeDetection.Core.Entities;

/// <summary>
/// Alert level for job watch notifications, determined by profile match quality.
/// </summary>
public enum JobAlertLevel
{
    /// <summary>All checks pass — push notification with full details.</summary>
    High,

    /// <summary>Most checks pass, 1-2 STRETCH — alert with gap notes.</summary>
    Medium,

    /// <summary>Any hard FAIL (PhD, dealbreaker, location) — log only, no notification.</summary>
    Silent,

    /// <summary>Informational update (listing removed, status change).</summary>
    Info
}

/// <summary>
/// Result of alert policy evaluation for a job listing.
/// </summary>
public class JobAlertPolicyResult
{
    public required JobAlertLevel AlertLevel { get; init; }
    public required string Reason { get; init; }

    /// <summary>Per-dimension status breakdown used to determine the alert level.</summary>
    public Dictionary<string, DimensionStatus> Dimensions { get; init; } = [];

    /// <summary>Whether any deadline urgency was applied.</summary>
    public bool UrgencyApplied { get; init; }

    /// <summary>Original alert level before urgency escalation.</summary>
    public JobAlertLevel? PreUrgencyLevel { get; init; }

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
