using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// A snapshot of website content at a point in time.
/// </summary>
public class ChangeSnapshot : IOwnedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// The ID of the user who owns this snapshot.
    /// Guid.Empty represents the default single-user mode owner.
    /// </summary>
    public Guid OwnerId { get; set; } = Guid.Empty;
    
    /// <summary>
    /// The watch this snapshot belongs to.
    /// </summary>
    public Guid WatchedSiteId { get; set; }
    
    /// <summary>
    /// When the content was captured.
    /// </summary>
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Hash of the content for quick comparison.
    /// </summary>
    public required string ContentHash { get; set; }
    
    /// <summary>
    /// The extracted content (text or HTML depending on settings).
    /// </summary>
    public required string Content { get; set; }
    
    /// <summary>
    /// Path to the full page or viewport screenshot.
    /// </summary>
    public string? ScreenshotPath { get; set; }

    /// <summary>
    /// Path to the element-specific screenshot (cropped to the monitored element).
    /// </summary>
    public string? ElementScreenshotPath { get; set; }

    /// <summary>
    /// Bounding box of the monitored element in the screenshot (JSON serialized).
    /// Format: {"x": 0, "y": 0, "width": 100, "height": 100}
    /// </summary>
    public string? ElementBoundingBoxJson { get; set; }
    
    /// <summary>
    /// HTTP status code from the fetch.
    /// </summary>
    public int HttpStatusCode { get; set; }
    
    /// <summary>
    /// Time taken to fetch the content in milliseconds.
    /// </summary>
    public long FetchDurationMs { get; set; }
    
    /// <summary>
    /// Size of the content in bytes.
    /// </summary>
    public long ContentSizeBytes { get; set; }

    /// <summary>
    /// JSON-serialized array of extracted objects when schema extraction is enabled.
    /// Null if schema extraction is not enabled or failed.
    /// </summary>
    public string? ExtractedObjectsJson { get; set; }

    /// <summary>
    /// Version of the schema used for extraction.
    /// Used to detect schema drift.
    /// </summary>
    public int? SchemaVersion { get; set; }

    /// <summary>
    /// Whether the page structure no longer matches the schema.
    /// </summary>
    public bool SchemaDriftDetected { get; set; }

    /// <summary>
    /// Error message if object extraction failed.
    /// No fallback to text diff - extraction failures are explicit.
    /// </summary>
    public string? ExtractionError { get; set; }

    /// <summary>
    /// Warnings about objects with ambiguous/duplicate identity values.
    /// </summary>
    public List<string> AmbiguousIdentityWarnings { get; set; } = [];

    // ========== LLM-Powered Content Enrichment ==========

    /// <summary>
    /// LLM-generated concise summary of the content.
    /// </summary>
    public string? ContentSummary { get; set; }

    /// <summary>
    /// Primary content type detected (Article, ProductPage, EventPage, etc.).
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    /// Primary language detected.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Named entities extracted from content (JSON serialized).
    /// </summary>
    public string? EntitiesJson { get; set; }

    /// <summary>
    /// Topics and themes identified (JSON serialized).
    /// </summary>
    public string? TopicsJson { get; set; }

    /// <summary>
    /// Sentiment analysis result (JSON serialized).
    /// </summary>
    public string? SentimentJson { get; set; }

    /// <summary>
    /// Structured data extracted (dates, prices, quantities) (JSON serialized).
    /// </summary>
    public string? StructuredDataJson { get; set; }

    /// <summary>
    /// Key phrases or keywords from the content (JSON serialized).
    /// </summary>
    public string? KeyPhrasesJson { get; set; }

    /// <summary>
    /// Content fingerprint for similarity comparison (JSON serialized).
    /// </summary>
    public string? ContentFingerprintJson { get; set; }

    /// <summary>
    /// Whether LLM enrichment was performed on this snapshot.
    /// </summary>
    public bool HasLlmEnrichment { get; set; }

    /// <summary>
    /// Confidence in the enrichment analysis.
    /// </summary>
    public float? EnrichmentConfidence { get; set; }

    // ========== PII Redaction ==========

    /// <summary>
    /// Number of PII items redacted from content before storage.
    /// </summary>
    public int PiiRedactionsApplied { get; set; }

    /// <summary>
    /// Types of PII detected and redacted (e.g. "Email,Phone,CreditCard").
    /// </summary>
    public string? PiiTypesRedacted { get; set; }
}
