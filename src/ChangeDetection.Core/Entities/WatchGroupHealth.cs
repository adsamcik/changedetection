namespace ChangeDetection.Core.Entities;

/// <summary>
/// Aggregated health information for all watches in a group.
/// </summary>
public class WatchGroupHealth
{
    public Guid GroupId { get; set; }
    public int TotalWatches { get; set; }
    public int Healthy { get; set; }
    public int Degraded { get; set; }
    public int Errored { get; set; }
    public List<WatchHealthEntry> Watches { get; set; } = [];
}

/// <summary>
/// Health information for a single watch in a group.
/// </summary>
public class WatchHealthEntry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public WatchHealthStatus Status { get; set; }
    public DateTime? LastChecked { get; set; }
    public int ItemCount { get; set; }
    public int PipelineBlocks { get; set; }
    public int ConsecutiveErrors { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Coarse health buckets for group dashboards.
/// </summary>
public enum WatchHealthStatus
{
    Healthy,
    Degraded,
    Errored
}
