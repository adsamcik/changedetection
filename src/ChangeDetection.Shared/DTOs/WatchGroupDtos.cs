namespace ChangeDetection.Shared.Dtos;

public class WatchGroupListItemDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public int MemberCount { get; set; }
    public List<AggregateFieldValueDto> PrimaryFields { get; set; } = [];
    public int ErrorCount { get; set; }
    public DateTime? LastActivity { get; set; }
    public List<string> Tags { get; set; } = [];
}

public class WatchGroupDetailDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? UserIntent { get; set; }
    public List<AggregateFieldConfigDto> AggregateFields { get; set; } = [];
    public List<AggregateAlertDto> AggregateAlerts { get; set; } = [];
    public List<WatchGroupMemberDto> Members { get; set; } = [];
    public AggregateSnapshotDto? LatestSnapshot { get; set; }
    public List<string> Tags { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public class WatchGroupCreateDto
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public List<string> Tags { get; set; } = [];
}

public class WatchGroupUpdateDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public List<string>? Tags { get; set; }
    public List<AggregateFieldConfigDto>? AggregateFields { get; set; }
    public List<AggregateAlertDto>? AggregateAlerts { get; set; }
}

public class WatchGroupMemberDto
{
    public string WatchId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Url { get; set; }
    public string Status { get; set; } = "";
    public DateTime? LastChecked { get; set; }
    public bool HasErrors { get; set; }
}

public class AggregateFieldConfigDto
{
    public string? Id { get; set; }
    public string FieldName { get; set; } = "";
    public string Function { get; set; } = "Min";
    public string? DisplayLabel { get; set; }
    public bool IsPrimary { get; set; }
    public string? Unit { get; set; }
    public string? CurrencyCode { get; set; }
}

public class AggregateAlertDto
{
    public string? Id { get; set; }
    public string FieldName { get; set; } = "";
    public string Function { get; set; } = "Min";
    public string ConditionType { get; set; } = "DropsBelow";
    public double Value { get; set; }
    public double? SecondaryValue { get; set; }
    public bool IsEnabled { get; set; } = true;
    public bool OneTime { get; set; }
    public string? CooldownPeriod { get; set; }
    public string? NotificationTemplate { get; set; }
    public string? ImportanceOverride { get; set; }
}

public class AggregateFieldValueDto
{
    public string FieldName { get; set; } = "";
    public string Function { get; set; } = "";
    public double? Value { get; set; }
    public string? FormattedValue { get; set; }
    public string? BestSourceName { get; set; }
    public List<PerSiteValueDto> PerSiteValues { get; set; } = [];
}

public class PerSiteValueDto
{
    public string WatchId { get; set; } = "";
    public string? WatchName { get; set; }
    public double? Value { get; set; }
    public string? FormattedValue { get; set; }
    public DateTime? LastUpdated { get; set; }
    public string Status { get; set; } = "";
}

public class AggregateSnapshotDto
{
    public string GroupId { get; set; } = "";
    public DateTime ComputedAt { get; set; }
    public List<AggregateFieldValueDto> Fields { get; set; } = [];
    public List<WatchGroupMemberDto> Members { get; set; } = [];
}

public class AggregateAlertResultDto
{
    public string GroupId { get; set; } = "";
    public DateTime EvaluatedAt { get; set; }
    public List<TriggeredAggregateAlertDto> TriggeredAlerts { get; set; } = [];
}

public class TriggeredAggregateAlertDto
{
    public string AlertId { get; set; } = "";
    public string FieldName { get; set; } = "";
    public double? AggregatedValue { get; set; }
    public double ThresholdValue { get; set; }
    public string? Message { get; set; }
    public string Importance { get; set; } = "Medium";
}
