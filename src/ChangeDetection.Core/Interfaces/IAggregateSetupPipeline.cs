using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Pipeline that sets up an aggregate watch group by orchestrating multiple
/// calls to IWatchSetupPipeline (one per site) and aligning schemas across sites.
/// This is a pure overlay — it calls existing pipelines, never modifies them.
/// </summary>
public interface IAggregateSetupPipeline
{
    /// <summary>
    /// Streams progress as the aggregate pipeline sets up watches for each URL.
    /// Internally calls IWatchSetupPipeline.ProcessStreamingAsync once per URL.
    /// </summary>
    IAsyncEnumerable<AggregateSetupProgress> SetupGroupStreamingAsync(
        AggregateSetupRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Non-streaming version for programmatic use.
    /// </summary>
    Task<AggregateSetupResult> SetupGroupAsync(
        AggregateSetupRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// Request to set up an aggregate watch group across multiple URLs.
/// </summary>
public class AggregateSetupRequest
{
    /// <summary>
    /// What the user wants to track (e.g., "PlayStation 5 price").
    /// </summary>
    public required string UserIntent { get; set; }

    /// <summary>
    /// The URLs to monitor (one per site).
    /// </summary>
    public required List<string> Urls { get; set; }

    /// <summary>
    /// Optional group name (auto-generated from intent if not provided).
    /// </summary>
    public string? GroupName { get; set; }

    /// <summary>
    /// Hint for the primary field to aggregate (e.g., "price").
    /// If null, the LLM schema-matching agent will infer it.
    /// </summary>
    public string? FieldHint { get; set; }

    /// <summary>
    /// Options forwarded to each per-site IWatchSetupPipeline call.
    /// </summary>
    public PipelineOptions? PipelineOptions { get; set; }
}

/// <summary>
/// Stages of the aggregate setup pipeline.
/// </summary>
public enum AggregateSetupStage
{
    Started,
    SettingUpWatch,
    WatchSetupComplete,
    WatchSetupFailed,
    AligningSchemas,
    SuggestingAggregation,
    Complete,
    Failed
}

/// <summary>
/// Progress update from the aggregate setup pipeline.
/// </summary>
public class AggregateSetupProgress
{
    public required AggregateSetupStage Stage { get; init; }
    public string? Message { get; init; }
    public string? Url { get; init; }
    public int CompletedCount { get; init; }
    public int TotalCount { get; init; }
    public float? Confidence { get; init; }

    /// <summary>
    /// Per-site pipeline progress (forwarded from IWatchSetupPipeline).
    /// </summary>
    public PipelineProgress? InnerProgress { get; init; }
}

/// <summary>
/// Result of the aggregate setup pipeline.
/// </summary>
public class AggregateSetupResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid GroupId { get; set; }
    public List<Guid> WatchIds { get; set; } = [];
    public List<Guid> FailedUrls { get; set; } = [];
    public List<AggregateFieldSuggestion> SuggestedFields { get; set; } = [];
}

/// <summary>
/// LLM-suggested aggregation field configuration.
/// </summary>
public class AggregateFieldSuggestion
{
    public required string FieldName { get; set; }
    public required string SuggestedFunction { get; set; }
    public string? Reasoning { get; set; }
    public float Confidence { get; set; }
}
