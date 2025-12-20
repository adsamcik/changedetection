namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// LLM-powered content enrichment service for enhancing fetched content with metadata.
/// Extracts entities, topics, sentiment, and structured insights from raw content.
/// </summary>
public interface IContentEnricher
{
    /// <summary>
    /// Enriches fetched content with LLM-extracted metadata.
    /// </summary>
    /// <param name="request">The enrichment request with content to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Enrichment result with extracted metadata.</returns>
    Task<ContentEnrichmentResult> EnrichContentAsync(
        ContentEnrichmentRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Performs a quick content classification without full enrichment.
    /// Useful for determining if full enrichment is worthwhile.
    /// </summary>
    /// <param name="content">Text content to classify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Quick classification result.</returns>
    Task<QuickClassificationResult> QuickClassifyAsync(
        string content,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a content fingerprint for similarity comparison.
    /// Useful for detecting semantic duplicates or near-duplicates.
    /// </summary>
    /// <param name="content">Text content to fingerprint.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Content fingerprint.</returns>
    Task<ContentFingerprint> GenerateFingerprintAsync(
        string content,
        CancellationToken ct = default);
}

/// <summary>
/// Request for content enrichment.
/// </summary>
public class ContentEnrichmentRequest
{
    /// <summary>
    /// The text content to enrich.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// URL source of the content.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Page title if available.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// User's monitoring intent for context.
    /// </summary>
    public string? UserIntent { get; init; }

    /// <summary>
    /// Watch ID for usage tracking.
    /// </summary>
    public Guid? WatchId { get; init; }

    /// <summary>
    /// Whether to extract named entities.
    /// </summary>
    public bool ExtractEntities { get; init; } = true;

    /// <summary>
    /// Whether to identify topics and themes.
    /// </summary>
    public bool IdentifyTopics { get; init; } = true;

    /// <summary>
    /// Whether to analyze sentiment.
    /// </summary>
    public bool AnalyzeSentiment { get; init; } = true;

    /// <summary>
    /// Whether to extract structured data (dates, prices, quantities).
    /// </summary>
    public bool ExtractStructuredData { get; init; } = true;

    /// <summary>
    /// Whether to generate a content summary.
    /// </summary>
    public bool GenerateSummary { get; init; } = true;

    /// <summary>
    /// Maximum content length to process (truncated if longer).
    /// </summary>
    public int MaxContentLength { get; init; } = 5000;
}

/// <summary>
/// Result of content enrichment.
/// </summary>
public class ContentEnrichmentResult
{
    /// <summary>
    /// Whether enrichment succeeded.
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Concise summary of the content.
    /// </summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Primary content type detected.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Primary language detected.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Named entities extracted from content.
    /// </summary>
    public List<ContentEntity> Entities { get; init; } = [];

    /// <summary>
    /// Topics and themes identified.
    /// </summary>
    public List<ContentTopic> Topics { get; init; } = [];

    /// <summary>
    /// Overall sentiment of the content.
    /// </summary>
    public ContentSentiment? Sentiment { get; init; }

    /// <summary>
    /// Structured data extracted (dates, prices, quantities).
    /// </summary>
    public List<StructuredDataItem> StructuredData { get; init; } = [];

    /// <summary>
    /// Key phrases or keywords from the content.
    /// </summary>
    public List<string> KeyPhrases { get; init; } = [];

    /// <summary>
    /// Reading level estimate.
    /// </summary>
    public string? ReadingLevel { get; init; }

    /// <summary>
    /// Confidence in the enrichment.
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    /// Token usage for this enrichment.
    /// </summary>
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
}

/// <summary>
/// A named entity extracted from content.
/// </summary>
public class ContentEntity
{
    /// <summary>
    /// Entity type (Person, Organization, Location, Date, Product, Price, etc.).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// The entity text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Normalized form of the entity (e.g., full date format).
    /// </summary>
    public string? NormalizedValue { get; init; }

    /// <summary>
    /// Number of occurrences in the content.
    /// </summary>
    public int Count { get; init; } = 1;

    /// <summary>
    /// Confidence in the extraction.
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    /// Whether this is a prominent/important entity.
    /// </summary>
    public bool IsProminent { get; init; }
}

/// <summary>
/// A topic or theme identified in content.
/// </summary>
public class ContentTopic
{
    /// <summary>
    /// Topic name or label.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Relevance score (0-1).
    /// </summary>
    public float Relevance { get; init; }

    /// <summary>
    /// Category the topic belongs to.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Keywords associated with this topic.
    /// </summary>
    public List<string> Keywords { get; init; } = [];
}

/// <summary>
/// Sentiment analysis result for content.
/// </summary>
public class ContentSentiment
{
    /// <summary>
    /// Overall sentiment (Positive, Neutral, Negative, Mixed).
    /// </summary>
    public required string Overall { get; init; }

    /// <summary>
    /// Sentiment score (-1 to 1).
    /// </summary>
    public float Score { get; init; }

    /// <summary>
    /// Confidence in the sentiment analysis.
    /// </summary>
    public float Confidence { get; init; }

    /// <summary>
    /// Dominant emotion if detectable (Joy, Sadness, Anger, Fear, Surprise, etc.).
    /// </summary>
    public string? DominantEmotion { get; init; }
}

/// <summary>
/// Structured data item extracted from content.
/// </summary>
public class StructuredDataItem
{
    /// <summary>
    /// Data type (Date, Price, Quantity, Duration, Percentage, etc.).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Raw text as it appears in content.
    /// </summary>
    public required string RawText { get; init; }

    /// <summary>
    /// Normalized/parsed value.
    /// </summary>
    public required string NormalizedValue { get; init; }

    /// <summary>
    /// Label or context for this data.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Unit if applicable (USD, kg, hours, etc.).
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// Confidence in the extraction.
    /// </summary>
    public float Confidence { get; init; }
}

/// <summary>
/// Quick classification result for content.
/// </summary>
public class QuickClassificationResult
{
    /// <summary>
    /// Primary content type.
    /// </summary>
    public required string ContentType { get; init; }

    /// <summary>
    /// Primary language.
    /// </summary>
    public required string Language { get; init; }

    /// <summary>
    /// Whether content appears to contain structured data.
    /// </summary>
    public bool HasStructuredData { get; init; }

    /// <summary>
    /// Whether content appears to be time-sensitive.
    /// </summary>
    public bool IsTimeSensitive { get; init; }

    /// <summary>
    /// Suggested enrichment options based on content type.
    /// </summary>
    public List<string> SuggestedEnrichments { get; init; } = [];

    /// <summary>
    /// Confidence in the classification.
    /// </summary>
    public float Confidence { get; init; }
}

/// <summary>
/// Content fingerprint for similarity comparison.
/// </summary>
public class ContentFingerprint
{
    /// <summary>
    /// Semantic hash of the content.
    /// </summary>
    public required string SemanticHash { get; init; }

    /// <summary>
    /// Key topics for comparison.
    /// </summary>
    public List<string> KeyTopics { get; init; } = [];

    /// <summary>
    /// Key entities for comparison.
    /// </summary>
    public List<string> KeyEntities { get; init; } = [];

    /// <summary>
    /// Content structure signature.
    /// </summary>
    public string? StructureSignature { get; init; }

    /// <summary>
    /// Compares similarity with another fingerprint.
    /// </summary>
    public float CompareSimilarity(ContentFingerprint other)
    {
        if (other == null) return 0f;

        var topicOverlap = KeyTopics.Intersect(other.KeyTopics).Count() / 
            (float)Math.Max(1, Math.Max(KeyTopics.Count, other.KeyTopics.Count));

        var entityOverlap = KeyEntities.Intersect(other.KeyEntities).Count() /
            (float)Math.Max(1, Math.Max(KeyEntities.Count, other.KeyEntities.Count));

        return (topicOverlap + entityOverlap) / 2f;
    }
}
