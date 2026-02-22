namespace ChangeDetection.Core.Entities;

/// <summary>
/// Record of LLM usage for cost tracking.
/// </summary>
public class LlmUsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Which provider was used.
    /// </summary>
    public Guid ProviderId { get; set; }
    
    /// <summary>
    /// Name of the provider at time of use.
    /// </summary>
    public required string ProviderName { get; set; }
    
    /// <summary>
    /// Model used.
    /// </summary>
    public required string Model { get; set; }
    
    /// <summary>
    /// What the LLM was used for.
    /// </summary>
    public LlmUsageType UsageType { get; set; }
    
    /// <summary>
    /// Related watch ID if applicable.
    /// </summary>
    public Guid? WatchedSiteId { get; set; }
    
    /// <summary>
    /// Number of input tokens.
    /// </summary>
    public int InputTokens { get; set; }
    
    /// <summary>
    /// Number of output tokens.
    /// </summary>
    public int OutputTokens { get; set; }
    
    /// <summary>
    /// Calculated cost for this usage.
    /// </summary>
    public decimal Cost { get; set; }
    
    /// <summary>
    /// Duration of the request in milliseconds.
    /// </summary>
    public long DurationMs { get; set; }
    
    /// <summary>
    /// Whether the request was successful.
    /// </summary>
    public bool IsSuccess { get; set; }
    
    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// When this usage occurred.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public enum LlmUsageType
{
    IntentClassification,
    EntityExtraction,
    Validation,
    ChangeSummary,
    NotificationGeneration,
    ContentAnalysis,
    SelectorGeneration,
    WatchSetup,
    SchemaDiscovery,
    ObjectExtraction,
    ImportanceScoring,
    ErrorResolution,
    /// <summary>
    /// LLM-powered semantic summarization of detected changes.
    /// </summary>
    SemanticSummary,
    /// <summary>
    /// Scoring relevance of changes against user's monitoring intent.
    /// </summary>
    RelevanceScoring,
    /// <summary>
    /// Entity and metadata enrichment during content ingestion.
    /// </summary>
    EntityEnrichment,
    /// <summary>
    /// Anomaly detection by comparing against historical patterns.
    /// </summary>
    AnomalyDetection,
    /// <summary>
    /// Content type and category classification.
    /// </summary>
    ContentClassification,
    /// <summary>
    /// Sentiment analysis of content changes.
    /// </summary>
    SentimentAnalysis,
    /// <summary>
    /// LLM-powered pipeline optimization review using a large model.
    /// </summary>
    PipelineOptimization,
    /// <summary>
    /// Adversarial testing — generating synthetic mutations to stress-test pipelines.
    /// </summary>
    AdversarialTest,
    Other
}
