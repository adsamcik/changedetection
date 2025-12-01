namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for listing watches in a dashboard view.
/// </summary>
public class WatchListItemDto
{
    public string Id { get; set; } = "";
    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? CssSelector { get; set; }
    public TimeSpan CheckInterval { get; set; }
    public DateTime? LastCheck { get; set; }
    public string Status { get; set; } = "Idle";
    public bool IsEnabled { get; set; } = true;
    public int ChangeCount { get; set; }
    public bool HasRecentChanges { get; set; }
}

/// <summary>
/// DTO for detailed watch view.
/// </summary>
public class WatchDetailDto
{
    public string Id { get; set; } = "";
    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? CssSelector { get; set; }
    public string? XpathSelector { get; set; }
    public List<string> IgnorePatterns { get; set; } = new();
    public TimeSpan CheckInterval { get; set; }
    public DateTime? LastCheck { get; set; }
    public DateTime? NextCheck { get; set; }
    public string Status { get; set; } = "Idle";
    public bool IsEnabled { get; set; } = true;
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public FetchSettingsDto? FetchSettings { get; set; }
    public NotificationSettingsDto? NotificationSettings { get; set; }
    public SnapshotDto? LatestSnapshot { get; set; }
}

/// <summary>
/// DTO for creating a new watch.
/// </summary>
public class WatchCreateDto
{
    public required string Url { get; set; }
    public string? Title { get; set; }
    public string? CssSelector { get; set; }
    public string? XpathSelector { get; set; }
    public List<string> IgnorePatterns { get; set; } = new();
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(1);
    public bool IsEnabled { get; set; } = true;
    public FetchSettingsDto FetchSettings { get; set; } = new();
    public NotificationSettingsDto NotificationSettings { get; set; } = new();
}

/// <summary>
/// DTO for fetch settings.
/// </summary>
public class FetchSettingsDto
{
    public bool UseJavaScript { get; set; }
    public string? WaitForSelector { get; set; }
    public int WaitTimeMs { get; set; }
    public bool CaptureScreenshot { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, string> CustomHeaders { get; set; } = new();
}

/// <summary>
/// DTO for notification settings.
/// </summary>
public class NotificationSettingsDto
{
    public bool EmailEnabled { get; set; }
    public List<string> EmailRecipients { get; set; } = new();
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
    public string MinimumImportanceToNotify { get; set; } = "Medium";
}

/// <summary>
/// DTO for content snapshots.
/// </summary>
public class SnapshotDto
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    public int ContentLength { get; set; }
    public string ContentHash { get; set; } = "";
    public DateTime CapturedAt { get; set; }
    public string? ScreenshotPath { get; set; }
}
