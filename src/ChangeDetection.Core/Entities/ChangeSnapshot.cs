namespace ChangeDetection.Core.Entities;

/// <summary>
/// A snapshot of website content at a point in time.
/// </summary>
public class ChangeSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
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
    /// Optional screenshot as base64 or file path.
    /// </summary>
    public string? ScreenshotPath { get; set; }
    
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
}
