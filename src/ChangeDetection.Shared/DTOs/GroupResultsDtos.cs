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
