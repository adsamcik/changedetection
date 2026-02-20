using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Multi-stage pipeline for setting up watches with LLM assistance.
/// Designed to work with smaller LLMs through iterative refinement.
/// </summary>
public interface IWatchSetupPipeline
{
    /// <summary>
    /// Processes user input through the multi-stage pipeline with real-time progress streaming.
    /// This is the preferred method for UI integration as it yields progress at each stage.
    /// </summary>
    IAsyncEnumerable<PipelineProgress> ProcessStreamingAsync(
        string userInput, 
        PipelineOptions? options = null, 
        CancellationToken ct = default);

    /// <summary>
    /// Continues processing with user feedback, streaming progress updates.
    /// </summary>
    IAsyncEnumerable<PipelineProgress> ContinueWithFeedbackStreamingAsync(
        PipelineSession session, 
        string feedback, 
        CancellationToken ct = default);
    
    /// <summary>
    /// Processes user input through the multi-stage pipeline.
    /// Consider using ProcessStreamingAsync for UI integration.
    /// </summary>
    Task<PipelineResult> ProcessAsync(string userInput, PipelineOptions? options = null, CancellationToken ct = default);
    
    /// <summary>
    /// Continues processing with user feedback (e.g., correcting a selector).
    /// Consider using ContinueWithFeedbackStreamingAsync for UI integration.
    /// </summary>
    Task<PipelineResult> ContinueWithFeedbackAsync(PipelineSession session, string feedback, CancellationToken ct = default);

    /// <summary>
    /// Attempts LLM-powered recovery from a failure with 3 phases:
    /// 1. Diagnose: LLM explains failure in ≤50 chars
    /// 2. Retry: Re-execute with diagnostic context
    /// 3. Ask User: Generate clarifying question if retry fails
    /// </summary>
    Task<PipelineResult> RecoverFromFailureAsync(PipelineSession session, PipelineResult failedResult, PipelineOptions options, CancellationToken ct = default);
}

/// <summary>
/// Progress update from the pipeline during streaming execution.
/// </summary>
public class PipelineProgress
{
    /// <summary>
    /// Current pipeline stage.
    /// </summary>
    public required PipelineStage Stage { get; init; }
    
    /// <summary>
    /// Type of progress update.
    /// </summary>
    public required ProgressType Type { get; init; }
    
    /// <summary>
    /// Human-readable summary of current progress.
    /// </summary>
    public required string Summary { get; init; }
    
    /// <summary>
    /// Optional detailed message (e.g., extracted URL, analysis result).
    /// </summary>
    public string? Details { get; init; }
    
    /// <summary>
    /// Current session state (available on all updates).
    /// </summary>
    public PipelineSession? Session { get; init; }
    
    /// <summary>
    /// Final result (only set when Type is Completed or Failed).
    /// </summary>
    public PipelineResult? Result { get; init; }
    
    /// <summary>
    /// Timestamp of this progress update.
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Type of pipeline progress update.
/// </summary>
public enum ProgressType
{
    /// <summary>Stage is starting.</summary>
    Starting,
    /// <summary>Stage is in progress with intermediate update.</summary>
    InProgress,
    /// <summary>AI thinking/reasoning content being streamed.</summary>
    Thinking,
    /// <summary>Stage completed successfully.</summary>
    StageCompleted,
    /// <summary>Pipeline needs user input to continue.</summary>
    NeedsInput,
    /// <summary>Pipeline completed successfully.</summary>
    Completed,
    /// <summary>Pipeline failed.</summary>
    Failed,
    /// <summary>Recovery attempt in progress.</summary>
    Recovery
}

/// <summary>
/// Options for the pipeline execution.
/// </summary>
public class PipelineOptions
{
    /// <summary>
    /// Maximum iterations for refinement loops.
    /// </summary>
    public int MaxIterations { get; set; } = 3;
    
    /// <summary>
    /// Minimum confidence threshold (0-1) to accept a selector.
    /// </summary>
    public float MinConfidence { get; set; } = 0.7f;
    
    /// <summary>
    /// Whether to use JavaScript rendering for fetching.
    /// </summary>
    public bool UseJavaScript { get; set; }
    
    /// <summary>
    /// Timeout for fetching content in seconds.
    /// </summary>
    public int FetchTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maximum recovery attempts before asking user for help.
    /// Each recovery cycle includes: diagnose, retry, then ask user if still failing.
    /// </summary>
    public int MaxRecoveryAttempts { get; set; } = 2;

    /// <summary>
    /// Maximum LLM calls allowed per pipeline execution.
    /// Prevents runaway costs from retry loops or bugs.
    /// </summary>
    public int MaxLlmCalls { get; set; } = 15;
}

/// <summary>
/// Result from the pipeline.
/// </summary>
public class PipelineResult
{
    public bool IsSuccess { get; set; }
    public PipelineStage CurrentStage { get; set; }
    public PipelineSession Session { get; set; } = new();
    
    /// <summary>
    /// Whether user input/feedback is needed to continue.
    /// </summary>
    public bool NeedsUserInput { get; set; }
    
    /// <summary>
    /// Questions or prompts for the user.
    /// </summary>
    public List<string> UserPrompts { get; set; } = [];
    
    /// <summary>
    /// Suggested options for the user to choose from.
    /// </summary>
    public List<SelectorOption> SuggestedOptions { get; set; } = [];
    
    /// <summary>
    /// Final watch configuration if pipeline completed successfully.
    /// </summary>
    public WatchConfiguration? FinalConfiguration { get; set; }
    
    /// <summary>
    /// Error message if pipeline failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Summary of what was determined at each stage.
    /// </summary>
    public string? Summary { get; set; }
}

/// <summary>
/// Pipeline execution stages.
/// </summary>
public enum PipelineStage
{
    UrlExtraction,
    ContentFetching,
    ContentAnalysis,
    SelectorGeneration,
    SelectorValidation,
    Confirmation,
    Complete,
    Failed
}

/// <summary>
/// Session state for the pipeline (allows continuation).
/// </summary>
public class PipelineSession
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string OriginalInput { get; set; } = string.Empty;
    
    /// <summary>
    /// The natural language intent extracted from the input (without URLs).
    /// E.g., "I want to watch for events on that page"
    /// </summary>
    public string UserIntent { get; set; } = string.Empty;
    
    // Stage 1: URL Extraction
    public List<ExtractedUrl> ExtractedUrls { get; set; } = [];
    public ExtractedUrl? SelectedUrl { get; set; }
    
    // Stage 2: Content Fetching
    public FetchedContent? FetchedContent { get; set; }
    
    // Stage 3: Content Analysis
    public ContentAnalysis? ContentAnalysis { get; set; }
    
    // Stage 4: Selector Generation
    public List<GeneratedSelector> GeneratedSelectors { get; set; } = [];
    
    // Stage 5: Selector Validation
    public List<SelectorValidation> ValidationResults { get; set; } = [];
    public GeneratedSelector? BestSelector { get; set; }
    
    // Stage 6: Schema Discovery (for list-type content)
    /// <summary>
    /// Schema discovered by LLM for structured object extraction.
    /// Populated when ContentType is EventList, ProductListing, Table, etc.
    /// </summary>
    public DiscoveredSchema? DiscoveredSchema { get; set; }
    
    /// <summary>
    /// Whether schema extraction should be enabled for this watch.
    /// Auto-set based on content type, can be overridden by user.
    /// </summary>
    public bool? SchemaEnabled { get; set; }
    
    // Iteration tracking
    public int CurrentIteration { get; set; }
    public List<string> IterationHistory { get; set; } = [];

    /// <summary>
    /// Number of recovery attempts made in the current session.
    /// </summary>
    public int RecoveryAttempts { get; set; }

    /// <summary>
    /// Last error that triggered recovery.
    /// </summary>
    public string? LastRecoveryError { get; set; }

    /// <summary>
    /// Diagnostic context from recovery attempts (used for retry prompts).
    /// </summary>
    public string? RecoveryDiagnosticContext { get; set; }

    /// <summary>
    /// Count of LLM calls made in this session (for cost control).
    /// </summary>
    public int LlmCallCount { get; set; }

    /// <summary>
    /// Records an LLM call. Throws if limit exceeded.
    /// </summary>
    public void RecordLlmCall(int maxCalls)
    {
        LlmCallCount++;
        if (LlmCallCount > maxCalls)
        {
            throw new InvalidOperationException($"LLM call limit exceeded ({maxCalls}). Pipeline terminated to prevent runaway costs.");
        }
    }
}

/// <summary>
/// A URL extracted from user input.
/// </summary>
public class ExtractedUrl
{
    public required string Url { get; set; }
    public required string NormalizedUrl { get; set; }
    public string? Context { get; set; } // Surrounding text for context
    public bool IsValid { get; set; }
}

/// <summary>
/// Content fetched from a URL.
/// </summary>
public class FetchedContent
{
    public required string Url { get; set; }
    public string? Html { get; set; }
    public string? CleanedHtml { get; set; }
    public string? TextContent { get; set; }
    public string? Title { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public long FetchDurationMs { get; set; }
    public bool UsedJavaScript { get; set; }
}

/// <summary>
/// LLM analysis of the page content.
/// </summary>
public class ContentAnalysis
{
    /// <summary>
    /// What the page is about.
    /// </summary>
    public string? PageDescription { get; set; }
    
    /// <summary>
    /// What the user likely wants to monitor.
    /// </summary>
    public string? UserIntent { get; set; }
    
    /// <summary>
    /// Type of content to monitor.
    /// </summary>
    public ContentType ContentType { get; set; }
    
    /// <summary>
    /// Identified sections/regions on the page.
    /// </summary>
    public List<PageSection> IdentifiedSections { get; set; } = [];
    
    /// <summary>
    /// Recommended monitoring approach.
    /// </summary>
    public MonitoringApproach RecommendedApproach { get; set; }
    
    /// <summary>
    /// Confidence in the analysis (0-1).
    /// </summary>
    public float Confidence { get; set; }
    
    /// <summary>
    /// Keywords extracted by LLM from user intent for creating filter rules.
    /// E.g., for "notify when tour comes to Prague" → ["Prague"].
    /// </summary>
    public List<string> FilterKeywords { get; set; } = [];
}

/// <summary>
/// Type of content detected.
/// </summary>
public enum ContentType
{
    Unknown,
    NewsList,
    EventList,
    ProductListing,
    PriceInfo,
    Article,
    Table,
    Feed,
    Calendar,
    StatusPage,
    Other
}

/// <summary>
/// Recommended approach for monitoring.
/// </summary>
public enum MonitoringApproach
{
    FullPage,
    SpecificSelector,
    MultipleSelectors,
    TextPattern,
    StructuredData
}

/// <summary>
/// A section identified on the page.
/// </summary>
public class PageSection
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? SuggestedSelector { get; set; }
    public string? SampleContent { get; set; }
    public bool IsLikelyTarget { get; set; }
}

/// <summary>
/// A generated selector for monitoring.
/// </summary>
public class GeneratedSelector
{
    public required string Selector { get; set; }
    public SelectorType Type { get; set; }
    public string? Description { get; set; }
    public string? Reasoning { get; set; }
    public float Confidence { get; set; }
    public int Priority { get; set; }
}

/// <summary>
/// Type of selector.
/// </summary>
public enum SelectorType
{
    CssSelector,
    XPath,
    TextPattern,
    JsonPath
}

/// <summary>
/// Validation result for a selector.
/// </summary>
public class SelectorValidation
{
    public required GeneratedSelector Selector { get; set; }
    public bool IsValid { get; set; }
    public int MatchCount { get; set; }
    public string? ExtractedSample { get; set; }
    public string? ValidationMessage { get; set; }
    public float MatchQuality { get; set; } // 0-1, how well it matches expected content
}

/// <summary>
/// Option presented to user for selection.
/// </summary>
public class SelectorOption
{
    public required string Label { get; set; }
    public required string Value { get; set; }
    public string? Preview { get; set; }
    public float Confidence { get; set; }
    public bool IsRecommended { get; set; }
}

/// <summary>
/// Final watch configuration after pipeline completes.
/// </summary>
public class WatchConfiguration
{
    public required string Url { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? CssSelector { get; set; }
    public string? XPathSelector { get; set; }
    public string? TextPattern { get; set; }
    public bool UseJavaScript { get; set; }
    public TimeSpan? CheckInterval { get; set; }
    public List<string> Tags { get; set; } = [];
    public float Confidence { get; set; }
    
    /// <summary>
    /// Whether structured object extraction is enabled.
    /// </summary>
    public bool SchemaEnabled { get; set; }
    
    /// <summary>
    /// Schema for extracting structured objects from the page.
    /// </summary>
    public ExtractionSchema? Schema { get; set; }
    
    /// <summary>
    /// Filter rules derived from user intent (e.g., "notify when Prague appears").
    /// </summary>
    public List<FilterRule> FilterRules { get; set; } = [];
}
