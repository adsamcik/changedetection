namespace ChangeDetection.Shared.Dtos;

public class PortalSuggestionDto
{
    public string Id { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string? DetectedPlatform { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string SourceWatchId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class PortalSuggestionAcceptResultDto
{
    public string SuggestionId { get; set; } = string.Empty;
    public string WatchId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}
