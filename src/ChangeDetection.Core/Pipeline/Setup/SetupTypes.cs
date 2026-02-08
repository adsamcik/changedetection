namespace ChangeDetection.Core.Pipeline.Setup;

/// <summary>Initial request to set up a watch via the composable pipeline.</summary>
public record SetupRequest
{
    public required string UserInput { get; init; }
    public Guid? OwnerId { get; init; }
}

/// <summary>Progress update streamed during setup.</summary>
public record SetupProgress
{
    public required SetupPhase Phase { get; init; }
    public required SetupProgressType Type { get; init; }
    public required string Message { get; init; }
    public string? Detail { get; init; }

    /// <summary>Set at Checkpoint 1 — parsed intent for user confirmation.</summary>
    public ParsedIntent? Intent { get; init; }

    /// <summary>Set at Checkpoint 2 — assembled pipeline for user approval.</summary>
    public PipelineProposal? Proposal { get; init; }

    /// <summary>Set when setup is complete — the saved watch ID.</summary>
    public Guid? WatchId { get; init; }

    /// <summary>Set when an error occurs.</summary>
    public string? Error { get; init; }
}

public enum SetupPhase
{
    IntentParsing,
    ContentFetching,
    ContentAnalysis,
    Checkpoint1,
    PipelineBuilding,
    DryRun,
    QcValidation,
    Checkpoint2,
    Saving
}

public enum SetupProgressType
{
    Started,
    Thinking,
    Progress,
    CheckpointReached,
    Completed,
    Failed
}

/// <summary>LLM-parsed understanding of user's intent.</summary>
public record ParsedIntent
{
    public required string Url { get; init; }
    public required string Intent { get; init; }
    public required string ChangeType { get; init; }
    public string? Summary { get; init; }
    public Dictionary<string, string>? Thresholds { get; init; }
    public string? Frequency { get; init; }
    public string? NotificationPreference { get; init; }
}

/// <summary>LLM analysis of the fetched page content.</summary>
public record ContentAnalysisResult
{
    public required string ContentType { get; init; }
    public List<string> Regions { get; init; } = [];
    public bool HasPagination { get; init; }
    public bool NeedsJavaScript { get; init; }
    public string? RecommendedSelector { get; init; }
    public string? PageSummary { get; init; }
}

/// <summary>The assembled pipeline proposal shown at Checkpoint 2.</summary>
public record PipelineProposal
{
    public required PipelineDefinition Pipeline { get; init; }
    public required string HumanSummary { get; init; }
    public DryRunResult? DryRun { get; init; }
    public QcResult? QcValidation { get; init; }
}

/// <summary>Results from executing the pipeline as a dry run.</summary>
public record DryRunResult
{
    public bool Success { get; init; }
    public Dictionary<string, object?> BlockOutputs { get; init; } = [];
    public string? Error { get; init; }
    public string? SampleOutput { get; init; }
}

/// <summary>QC validation results from LLM.</summary>
public record QcResult
{
    public bool Valid { get; init; }
    public List<string> Issues { get; init; } = [];
    public List<string> Suggestions { get; init; } = [];
}

/// <summary>In-memory session state for the composable setup flow.</summary>
public class SetupSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public SetupPhase CurrentPhase { get; set; } = SetupPhase.IntentParsing;
    public required string UserInput { get; set; }
    public Guid? OwnerId { get; set; }
    public ParsedIntent? Intent { get; set; }
    public string? FetchedHtml { get; set; }
    public ContentAnalysisResult? ContentAnalysis { get; set; }
    public PipelineDefinition? AssembledPipeline { get; set; }
    public DryRunResult? DryRunResult { get; set; }
    public QcResult? QcResult { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;
    public int LlmCallCount { get; set; }
}
