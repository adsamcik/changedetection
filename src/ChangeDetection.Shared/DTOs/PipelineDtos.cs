namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// Detailed pipeline information for the advanced pipeline editor.
/// </summary>
public class WatchPipelineDto
{
    public string WatchId { get; set; } = "";
    public string WatchName { get; set; } = "";
    public string WatchUrl { get; set; } = "";
    public bool HasPipeline { get; set; }
    public string PipelineJson { get; set; } = "";
    public int BlockCount { get; set; }
    public double EstimatedExecutionTimeMs { get; set; }
    public string LastRunStatus { get; set; } = "";
    public DateTime? LastChecked { get; set; }
    public string? LastError { get; set; }
    public bool IsPipelineValid { get; set; }
    public List<string> ValidationErrors { get; set; } = [];
    public List<string> ValidationWarnings { get; set; } = [];
    public List<WatchPipelineBlockDto> Blocks { get; set; } = [];
    public List<WatchPipelineConnectionDto> Connections { get; set; } = [];
    public string RebuildWithAiUrl { get; set; } = "/setup";
}

/// <summary>
/// Pipeline block summary used by the editor UI.
/// </summary>
public class WatchPipelineBlockDto
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Category { get; set; } = "";
    public string Icon { get; set; } = "";
    public int ExecutionOrder { get; set; }
    public string ConfigJson { get; set; } = "{}";
    public Dictionary<string, string> KeyConfigValues { get; set; } = [];
}

/// <summary>
/// Connection summary used by the editor UI.
/// </summary>
public class WatchPipelineConnectionDto
{
    public string FromBlockId { get; set; } = "";
    public string FromPort { get; set; } = "";
    public string ToBlockId { get; set; } = "";
    public string ToPort { get; set; } = "";
}

// ============================================================================
// Schema Discovery DTOs
// ============================================================================

/// <summary>
/// Schema discovered by LLM during setup.
/// Presented to user for confirmation/refinement.
/// </summary>
public class DiscoveredSchemaDto
{
    /// <summary>
    /// CSS or XPath selector for the repeating item container.
    /// </summary>
    public required string ItemSelector { get; set; }

    /// <summary>
    /// Discovered fields with their selectors and types.
    /// </summary>
    public List<DiscoveredFieldDto> Fields { get; set; } = [];

    /// <summary>
    /// Fields inferred to uniquely identify objects (for diff matching).
    /// </summary>
    public List<string> InferredIdentityFields { get; set; } = [];

    /// <summary>
    /// Overall confidence in the schema discovery (0-1).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// Number of items found on the page matching the schema.
    /// </summary>
    public int SampleItemCount { get; set; }

    /// <summary>
    /// LLM's explanation of the discovered structure.
    /// </summary>
    public string? Explanation { get; set; }

    /// <summary>
    /// Content type detected (EventList, ProductListing, etc.).
    /// </summary>
    public string? ContentType { get; set; }
}

/// <summary>
/// A field discovered during schema discovery.
/// </summary>
public class DiscoveredFieldDto
{
    /// <summary>
    /// Suggested field name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Inferred field type: String, Date, Url, Number, Image, Html.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Selector relative to item container.
    /// </summary>
    public required string Selector { get; set; }

    /// <summary>
    /// Whether the field appears in all items (required).
    /// </summary>
    public bool IsRequired { get; set; }

    /// <summary>
    /// Whether LLM suggests this as an identity field.
    /// </summary>
    public bool IsIdentityField { get; set; }

    /// <summary>
    /// Sample values extracted from the page.
    /// </summary>
    public List<string> SampleValues { get; set; } = [];

    /// <summary>
    /// Confidence in this field's detection (0-1).
    /// </summary>
    public double Confidence { get; set; }

    /// <summary>
    /// LLM's explanation for this field.
    /// </summary>
    public string? Reasoning { get; set; }
}

/// <summary>
/// User's response to schema discovery for refinement.
/// </summary>
public class SchemaRefinementRequest
{
    /// <summary>
    /// Session ID.
    /// </summary>
    public required Guid SessionId { get; set; }

    /// <summary>
    /// User-edited fields (can rename, change types, toggle identity).
    /// </summary>
    public List<SchemaFieldEditDto> Fields { get; set; } = [];

    /// <summary>
    /// Fields to exclude from extraction.
    /// </summary>
    public List<string> ExcludedFieldNames { get; set; } = [];

    /// <summary>
    /// User-specified identity field names (overrides LLM inference).
    /// </summary>
    public List<string>? IdentityFieldOverrides { get; set; }

    /// <summary>
    /// Whether to enable schema extraction for this watch.
    /// </summary>
    public bool EnableSchema { get; set; } = true;
}

/// <summary>
/// User edit to a schema field.
/// </summary>
public class SchemaFieldEditDto
{
    /// <summary>
    /// Original field name from discovery.
    /// </summary>
    public required string OriginalName { get; set; }

    /// <summary>
    /// New name (if renamed).
    /// </summary>
    public string? NewName { get; set; }

    /// <summary>
    /// New type (if changed).
    /// </summary>
    public string? NewType { get; set; }

    /// <summary>
    /// Whether to mark as identity field.
    /// </summary>
    public bool? IsIdentityField { get; set; }

    /// <summary>
    /// Whether the field is required.
    /// </summary>
    public bool? IsRequired { get; set; }
}

// ============================================================================
// Object Extraction DTOs
// ============================================================================

/// <summary>
/// An extracted object from a watched page.
/// </summary>
public class ExtractedObjectDto
{
    /// <summary>
    /// Unique identity string computed from identity fields.
    /// </summary>
    public required string Identity { get; set; }

    /// <summary>
    /// Field name-value pairs.
    /// </summary>
    public Dictionary<string, string> Fields { get; set; } = [];

    /// <summary>
    /// Original HTML of the item (for debugging).
    /// </summary>
    public string? SourceHtml { get; set; }

    /// <summary>
    /// Selector index (position in the list).
    /// </summary>
    public int Index { get; set; }
}

/// <summary>
/// Result of diffing two sets of extracted objects.
/// </summary>
public class ObjectDiffResultDto
{
    /// <summary>
    /// Objects that are new (not in previous snapshot).
    /// </summary>
    public List<ExtractedObjectDto> AddedObjects { get; set; } = [];

    /// <summary>
    /// Objects that were removed (not in current snapshot).
    /// </summary>
    public List<ExtractedObjectDto> RemovedObjects { get; set; } = [];

    /// <summary>
    /// Objects that exist in both but have changed fields.
    /// </summary>
    public List<ObjectChangeDto> ChangedObjects { get; set; } = [];

    /// <summary>
    /// Objects with ambiguous identity (multiple matches possible).
    /// </summary>
    public List<AmbiguousIdentityDto> AmbiguousIdentities { get; set; } = [];

    /// <summary>
    /// Total number of objects in current snapshot.
    /// </summary>
    public int TotalCurrentObjects { get; set; }

    /// <summary>
    /// Total number of objects in previous snapshot.
    /// </summary>
    public int TotalPreviousObjects { get; set; }
}

/// <summary>
/// A change within a single object's fields.
/// </summary>
public class ObjectChangeDto
{
    /// <summary>
    /// The object's identity.
    /// </summary>
    public required string Identity { get; set; }

    /// <summary>
    /// Field-level changes.
    /// </summary>
    public List<FieldChangeDto> FieldChanges { get; set; } = [];

    /// <summary>
    /// LLM-scored importance (0-1) if enabled.
    /// </summary>
    public double? Importance { get; set; }

    /// <summary>
    /// LLM reasoning for importance score.
    /// </summary>
    public string? ImportanceReasoning { get; set; }
}

/// <summary>
/// A change to a single field within an object.
/// </summary>
public class FieldChangeDto
{
    /// <summary>
    /// Name of the changed field.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Previous value.
    /// </summary>
    public string? OldValue { get; set; }

    /// <summary>
    /// New value.
    /// </summary>
    public string? NewValue { get; set; }
}

/// <summary>
/// An ambiguous identity situation requiring user resolution.
/// </summary>
public class AmbiguousIdentityDto
{
    /// <summary>
    /// The identity string that matches multiple objects.
    /// </summary>
    public required string Identity { get; set; }

    /// <summary>
    /// Objects from previous snapshot with this identity.
    /// </summary>
    public List<ExtractedObjectDto> PreviousMatches { get; set; } = [];

    /// <summary>
    /// Objects from current snapshot with this identity.
    /// </summary>
    public List<ExtractedObjectDto> CurrentMatches { get; set; } = [];

    /// <summary>
    /// LLM's suggestion for resolution (if available).
    /// </summary>
    public string? SuggestedResolution { get; set; }
}

/// <summary>
/// User resolution for an ambiguous identity conflict.
/// </summary>
public class IdentityResolutionDto
{
    /// <summary>
    /// The identity string being resolved.
    /// </summary>
    public required string Identity { get; set; }

    /// <summary>
    /// Resolution choice: "treat-as-different", "treat-as-same", "ignore".
    /// </summary>
    public required string Resolution { get; set; }
}

// ============================================================================
// Flow State DTOs (for Setup Flow Page)
// ============================================================================

/// <summary>
/// Status of a flow state entry.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum FlowStateStatus
{
    /// <summary>
    /// Stage has not started yet.
    /// </summary>
    Pending,

    /// <summary>
    /// Stage is currently in progress.
    /// </summary>
    InProgress,

    /// <summary>
    /// AI is thinking/reasoning.
    /// </summary>
    Thinking,

    /// <summary>
    /// Stage completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Stage failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Recovery attempt in progress.
    /// </summary>
    Recovery,

    /// <summary>
    /// Waiting for user input.
    /// </summary>
    Question
}

/// <summary>
/// An option for user selection in a flow (client-side record version).
/// </summary>
public record FlowOption(string Label, string Value, bool IsRecommended = false, string? Preview = null);

/// <summary>
/// A single entry in the setup flow timeline.
/// </summary>
public record FlowStateEntry
{
    /// <summary>
    /// The pipeline stage this entry represents.
    /// </summary>
    public required string Stage { get; init; }

    /// <summary>
    /// Current status of this entry.
    /// </summary>
    public required FlowStateStatus Status { get; init; }

    /// <summary>
    /// Brief summary for display (≤50 chars recommended).
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// When this entry was created.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Whether this is the current/active state in the timeline.
    /// </summary>
    public bool IsCurrentState { get; init; }

    /// <summary>
    /// Optional detailed message (for errors or recovery reasoning).
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// For Question status: the input type marker ([YES/NO], [SELECT:...], [INPUT]).
    /// </summary>
    public string? InputType { get; init; }

    /// <summary>
    /// For Question status with SELECT: the available options with label and value.
    /// </summary>
    public List<FlowOption>? Options { get; init; }
    
    /// <summary>
    /// The ID of the created watch (only set when Stage is "Complete" and watch was successfully created).
    /// </summary>
    public Guid? WatchId { get; init; }
}

/// <summary>
/// DTO for flow state entry streamed to the client.
/// Same as FlowStateEntry but as a class for serialization compatibility.
/// </summary>
public class FlowStateEntryDto
{
    public required string Stage { get; set; }
    public FlowStateStatusDto Status { get; set; }
    public required string Summary { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public bool IsCurrentState { get; set; }
    public string? Details { get; set; }
    public string? InputType { get; set; }
    public List<FlowOptionDto>? Options { get; set; }
    
    /// <summary>
    /// The ID of the created watch (only set when Stage is "Complete" and watch was successfully created).
    /// </summary>
    public Guid? WatchId { get; set; }
}

/// <summary>
/// Status enum for DTOs (mirrors FlowStateStatus).
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum FlowStateStatusDto
{
    Pending,
    InProgress,
    Thinking,
    Completed,
    Failed,
    Recovery,
    Question
}

/// <summary>
/// An option presented to the user in a flow question.
/// </summary>
public class FlowOptionDto
{
    public required string Label { get; set; }
    public required string Value { get; set; }
    public bool IsRecommended { get; set; }
    public string? Preview { get; set; }
}

// ── Composable Setup Pipeline DTOs (client-side mirrors of Core types) ──────

/// <summary>
/// Client-side DTO for SetupProgress streamed from ComposableSetupHub.
/// Mirrors ChangeDetection.Core.Pipeline.Setup.SetupProgress for JSON deserialization.
/// </summary>
public class SetupProgressDto
{
    public SetupPhaseDto Phase { get; set; }
    public SetupProgressTypeDto2 Type { get; set; }
    public required string Message { get; set; }
    public string? Detail { get; set; }
    public string? SessionId { get; set; }
    public ParsedIntentDto? Intent { get; set; }
    public PipelineProposalDto? Proposal { get; set; }
    public Guid? WatchId { get; set; }
    public string? Error { get; set; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SetupPhaseDto
{
    IntentParsing,
    ContentFetching,
    ContentAnalysis,
    Checkpoint1,
    PipelineBuilding,
    DryRun,
    AdversarialTest,
    QcValidation,
    Checkpoint2,
    Saving
}

/// <summary>
/// Progress type for the composable setup flow.
/// Named with suffix to avoid collision with FlowStateStatusDto.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum SetupProgressTypeDto2
{
    Started,
    Thinking,
    Progress,
    CheckpointReached,
    Completed,
    Failed
}

/// <summary>
/// Client-side DTO for parsed intent shown at Checkpoint 1.
/// </summary>
public class ParsedIntentDto
{
    public required string Url { get; set; }
    public required string Intent { get; set; }
    public required string ChangeType { get; set; }
    public string? Summary { get; set; }
    public Dictionary<string, string>? Thresholds { get; set; }
    public string? Frequency { get; set; }
    public string? NotificationPreference { get; set; }
}

/// <summary>
/// Client-side DTO for the pipeline proposal shown at Checkpoint 2.
/// </summary>
public class PipelineProposalDto
{
    public required string HumanSummary { get; set; }
    public DryRunResultDto? DryRun { get; set; }
    public QcResultDto? QcValidation { get; set; }
}

/// <summary>
/// Client-side DTO for dry run results.
/// </summary>
public class DryRunResultDto
{
    public bool Success { get; set; }
    public long ExecutionDurationMs { get; set; }
    public string? Error { get; set; }
    public string? SampleOutput { get; set; }
}

/// <summary>
/// Client-side DTO for QC validation results.
/// </summary>
public class QcResultDto
{
    public bool Valid { get; set; }
    public List<string> Issues { get; set; } = [];
    public List<string> Suggestions { get; set; } = [];
}
