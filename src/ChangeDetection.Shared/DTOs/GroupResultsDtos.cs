namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// Unified results view aggregating items across all watches in a group.
/// </summary>
public class GroupResultsDto
{
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string? GroupIcon { get; set; }
    public int TotalWatches { get; set; }
    public int HealthyWatches { get; set; }
    public int TotalItems { get; set; }
    public int NewItems { get; set; }
    public DateTime? LastChecked { get; set; }
    public List<GroupResultItemDto> Items { get; set; } = [];

    /// <summary>
    /// Summary of outreach-friendly watches in this group.
    /// Null if no watches have been scanned.
    /// </summary>
    public GroupOutreachSummaryDto? OutreachSummary { get; set; }
}

/// <summary>
/// A single aggregated item from a group watch's extracted objects.
/// </summary>
public class GroupResultItemDto
{
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public string? Company { get; set; }
    public string? Location { get; set; }
    public string Source { get; set; } = "";
    public string SourceWatchId { get; set; } = "";
    public List<string> Sources { get; set; } = [];
    public List<string> SourceNames { get; set; } = [];
    public List<string> SourceWatchIds { get; set; } = [];
    public bool IsMultiSource { get; set; }
    public float? RelevanceScore { get; set; }
    public DateTime FirstSeen { get; set; }
    public bool IsNew { get; set; }

    /// <summary>
    /// All extracted fields for this item (beyond the well-known ones above).
    /// </summary>
    public Dictionary<string, string> ExtraFields { get; set; } = [];
}

/// <summary>
/// Outreach-friendly companies detected in a group's watches.
/// </summary>
public class GroupOutreachDto
{
    public bool IsOutreachFriendly { get; set; }
    public List<OutreachSignalDto> Signals { get; set; } = [];
    public float OverallScore { get; set; }
}

/// <summary>
/// A single outreach signal detected on a company's careers page.
/// </summary>
public class OutreachSignalDto
{
    public string Type { get; set; } = "";
    public string Evidence { get; set; } = "";
    public float Confidence { get; set; }
}

/// <summary>
/// Summary of outreach-friendly watches within a group, displayed in the results page.
/// </summary>
public class GroupOutreachSummaryDto
{
    public int OutreachFriendlyCount { get; set; }
    public List<OutreachWatchDto> OutreachWatches { get; set; } = [];
}

/// <summary>
/// A single watch that was detected as outreach-friendly.
/// </summary>
public class OutreachWatchDto
{
    public string WatchId { get; set; } = "";
    public string WatchName { get; set; } = "";
    public string? Company { get; set; }
    public float OverallScore { get; set; }
    public List<OutreachSignalDto> Signals { get; set; } = [];
}

/// <summary>
/// Export format for outreach-friendly companies, suitable for LaTeX report generation.
/// </summary>
public class OutreachExportDto
{
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public DateTime ExportedAt { get; set; }
    public List<OutreachExportCompanyDto> Companies { get; set; } = [];
}

/// <summary>
/// A single outreach-friendly company in the export.
/// </summary>
public class OutreachExportCompanyDto
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public float Score { get; set; }
    public List<string> Signals { get; set; } = [];
    public string OutreachChannel { get; set; } = "";
}
