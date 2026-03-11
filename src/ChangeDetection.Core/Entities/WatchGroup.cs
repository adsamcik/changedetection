using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Represents a group of watches for aggregate monitoring.
/// </summary>
public class WatchGroup : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; } = Guid.Empty;
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? UserIntent { get; set; }

    /// <summary>
    /// Structured analysis profile as JSON for LLM-powered evaluation of changes.
    /// Used by ChangeAnalyzer to perform multi-dimensional matching (e.g., candidate
    /// profile for job watches with education, skills, location, salary criteria).
    /// When present, relevance scoring returns per-dimension match assessments.
    /// </summary>
    public string? AnalysisProfileJson { get; set; }

    public List<AggregateFieldConfig> AggregateFields { get; set; } = [];
    public List<AggregateAlert> AggregateAlerts { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum AggregateFunction
{
    Min, Max, Average, Sum, Count, Median, Latest, Range
}

public class AggregateFieldConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FieldName { get; set; }
    public AggregateFunction Function { get; set; } = AggregateFunction.Min;
    public string? DisplayLabel { get; set; }
    public bool IsPrimary { get; set; }
    public string? Unit { get; set; }
    public string? CurrencyCode { get; set; }

    /// <summary>Previous best source for rank-switch detection.</summary>
    public string? PreviousBestSource { get; set; }

    /// <summary>How many consecutive checks the current leader must hold before alerting.</summary>
    public int RankStabilityRequired { get; set; } = 2;

    /// <summary>How many consecutive checks the current leader has held.</summary>
    public int CurrentLeaderHoldCount { get; set; }

    /// <summary>Outlier threshold: flag sites deviating more than this % from group median.</summary>
    public double OutlierThresholdPercent { get; set; } = 20.0;
}

public class AggregateAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FieldName { get; set; }
    public AggregateFunction Function { get; set; } = AggregateFunction.Min;
    public AlertConditionType ConditionType { get; set; } = AlertConditionType.DropsBelow;
    public double Value { get; set; }
    public double? SecondaryValue { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool OneTime { get; set; }
    public TimeSpan? CooldownPeriod { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
    public int TriggerCount { get; set; }
    public string? NotificationTemplate { get; set; }
    public ChangeImportance? ImportanceOverride { get; set; }
}

public class AggregateSnapshot
{
    public Guid GroupId { get; set; }
    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;
    public List<AggregateFieldValue> Fields { get; set; } = [];
    public List<AggregateSnapshotMember> Members { get; set; } = [];
    public List<DataQualityWarning> DataQualityWarnings { get; set; } = [];
    public AbsenceSummary? AbsenceSummary { get; set; }
}

public class AggregateFieldValue
{
    public required string FieldName { get; set; }
    public AggregateFunction Function { get; set; }
    public double? AggregatedValue { get; set; }
    public string? FormattedValue { get; set; }
    public string? BestSourceName { get; set; }
    public List<PerSiteValue> PerSiteValues { get; set; } = [];
}

public class PerSiteValue
{
    public Guid WatchId { get; set; }
    public string? WatchName { get; set; }
    public double? Value { get; set; }
    public string? FormattedValue { get; set; }
    public DateTime? LastUpdated { get; set; }
    public WatchStatus Status { get; set; }

    /// <summary>Availability state for absence detection.</summary>
    public SiteAvailabilityState AvailabilityState { get; set; } = SiteAvailabilityState.Available;

    /// <summary>Number of consecutive check failures for this site.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Whether this reading was quarantined by the sanity guard.</summary>
    public bool IsQuarantined { get; set; }

    /// <summary>Reason for quarantine, if any.</summary>
    public string? QuarantineReason { get; set; }

    /// <summary>Deviation from group median as a percentage (for outlier detection).</summary>
    public double? DeviationFromMedianPercent { get; set; }
}

public class AggregateSnapshotMember
{
    public Guid WatchId { get; set; }
    public string? Name { get; set; }
    public string? Url { get; set; }
    public WatchStatus Status { get; set; }
    public DateTime? LastChecked { get; set; }
    public bool HasErrors { get; set; }
}

public class AggregateAlertResult
{
    public Guid GroupId { get; set; }
    public DateTime EvaluatedAt { get; set; } = DateTime.UtcNow;
    public List<TriggeredAggregateAlert> TriggeredAlerts { get; set; } = [];
}

public class TriggeredAggregateAlert
{
    public Guid AlertId { get; set; }
    public required string FieldName { get; set; }
    public double? AggregatedValue { get; set; }
    public double ThresholdValue { get; set; }
    public string? Message { get; set; }
    public ChangeImportance Importance { get; set; } = ChangeImportance.Medium;
}

/// <summary>Site availability states for absence detection.</summary>
public enum SiteAvailabilityState
{
    Available,
    MissingPending,
    ConfirmedAbsent
}

/// <summary>Summary of site absences in a group.</summary>
public class AbsenceSummary
{
    public int AvailableCount { get; set; }
    public int MissingPendingCount { get; set; }
    public int ConfirmedAbsentCount { get; set; }
    public List<Guid> AbsentWatchIds { get; set; } = [];
}

/// <summary>Data quality warning from the sanity guard.</summary>
public class DataQualityWarning
{
    public Guid WatchId { get; set; }
    public string? WatchName { get; set; }
    public required string FieldName { get; set; }
    public double? ReportedValue { get; set; }
    public double? PreviousValue { get; set; }
    public required string Reason { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}
