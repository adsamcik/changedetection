namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for listing changes in the UI.
/// </summary>
public class ChangeListItemDto
{
    public string Id { get; set; } = "";
    public string WatchId { get; set; } = "";
    public string? WatchTitle { get; set; }
    public DateTime DetectedAt { get; set; }
    public string Summary { get; set; } = "";
    public string Importance { get; set; } = "Low";
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public bool IsViewed { get; set; }
    public bool IsNotified { get; set; }
}

/// <summary>
/// DTO for detailed change view with diff.
/// </summary>
public class ChangeDetailDto
{
    public string Id { get; set; } = "";
    public string WatchId { get; set; } = "";
    public string? WatchTitle { get; set; }
    public string? WatchUrl { get; set; }
    public DateTime DetectedAt { get; set; }
    public string Summary { get; set; } = "";
    public string? DiffText { get; set; }
    public string? DiffHtml { get; set; }
    public string Importance { get; set; } = "Low";
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public bool IsViewed { get; set; }
    public SnapshotInfoDto? PreviousSnapshot { get; set; }
    public SnapshotInfoDto? CurrentSnapshot { get; set; }
}

/// <summary>
/// DTO for snapshot info in change details.
/// </summary>
public class SnapshotInfoDto
{
    public string Id { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public string Content { get; set; } = "";
    public string? ScreenshotPath { get; set; }
}
