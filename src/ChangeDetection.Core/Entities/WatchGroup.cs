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
