using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for extracting structured objects from HTML using a schema.
/// Uses LLM for extraction with no fallbacks - failures are explicit.
/// </summary>
public interface IObjectExtractionService
{
    /// <summary>
    /// Extracts objects from HTML using the provided schema.
    /// </summary>
    /// <param name="html">The HTML content to extract from.</param>
    /// <param name="schema">The schema defining what to extract.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Extraction result with objects or error details.</returns>
    Task<ObjectExtractionResult> ExtractAsync(
        string html,
        ExtractionSchema schema,
        CancellationToken ct = default);

    /// <summary>
    /// Validates that a schema can still extract from the given HTML.
    /// Used for schema drift detection.
    /// </summary>
    /// <param name="html">The HTML content to validate against.</param>
    /// <param name="schema">The schema to validate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if schema is still valid for the HTML structure.</returns>
    Task<SchemaValidationResult> ValidateSchemaAsync(
        string html,
        ExtractionSchema schema,
        CancellationToken ct = default);
}

/// <summary>
/// Result of schema validation against HTML.
/// </summary>
public class SchemaValidationResult
{
    /// <summary>
    /// Whether the schema is still valid for the HTML.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Whether significant drift was detected.
    /// </summary>
    public bool DriftDetected { get; set; }

    /// <summary>
    /// Specific issues found during validation.
    /// </summary>
    public List<string> Issues { get; set; } = [];

    /// <summary>
    /// Number of items found with the item selector.
    /// </summary>
    public int ItemCount { get; set; }

    /// <summary>
    /// Fields that could not be found.
    /// </summary>
    public List<string> MissingFields { get; set; } = [];

    /// <summary>
    /// Confidence in the validation (0-1).
    /// </summary>
    public float Confidence { get; set; }
}
