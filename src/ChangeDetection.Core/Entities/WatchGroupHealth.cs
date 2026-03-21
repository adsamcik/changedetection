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
    Errored,

    /// <summary>
    /// Watch was created without a pipeline (no template for the platform)
    /// and requires interactive setup before it can check for changes.
    /// </summary>
    SetupNeeded
}

/// <summary>
/// Shared health classification logic for watch entities.
/// Used by dashboard, group results, and health endpoints to ensure consistent counts.
/// </summary>
public static class WatchHealthClassifier
{
    /// <summary>
    /// Classifies a watch into a health bucket using the full set of health signals:
    /// entity status, consecutive failures, and last error message.
    /// </summary>
    public static WatchHealthStatus Classify(
        WatchedSite watch,
        PipelineRunSummaryEntity? latestSummary = null)
    {
        // Watches that were created without a pipeline need interactive setup first
        if (watch.NeedsPipelineSetup)
            return WatchHealthStatus.SetupNeeded;

        if (latestSummary is not null)
        {
            if (!latestSummary.Success)
                return latestSummary.IsDegraded
                    ? WatchHealthStatus.Degraded
                    : WatchHealthStatus.Errored;

            if (latestSummary.IsDegraded)
                return WatchHealthStatus.Degraded;
        }

        if (watch.Status == WatchStatus.Error)
            return WatchHealthStatus.Errored;

        if (watch.ConsecutiveFailures > 0 || !string.IsNullOrWhiteSpace(watch.LastError))
            return WatchHealthStatus.Degraded;

        return watch.Status == WatchStatus.Active
            ? WatchHealthStatus.Healthy
            : WatchHealthStatus.Degraded;
    }
}
