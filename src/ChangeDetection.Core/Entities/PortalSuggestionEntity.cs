using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

public class PortalSuggestionEntity : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; } = Guid.Empty;
    public required string Url { get; set; }
    public required string Domain { get; set; }
    public string? DetectedPlatform { get; set; }
    public required string Reason { get; set; }
    public Guid SourceWatchId { get; set; }
    public Guid? GroupId { get; set; }
    public SuggestionStatus Status { get; set; } = SuggestionStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum SuggestionStatus
{
    Pending,
    Accepted,
    Dismissed
}
