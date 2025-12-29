namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for detecting and handling duplicate content during ingestion.
/// Prevents creating duplicate snapshots when content is semantically unchanged.
/// </summary>
public interface IDeduplicationService
{
    /// <summary>
    /// Checks if the new content is a duplicate of the previous content.
    /// Uses both hash-based and semantic comparison to detect duplicates.
    /// </summary>
    /// <param name="request">The deduplication check request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result indicating whether the content is a duplicate.</returns>
    Task<DeduplicationResult> CheckForDuplicateAsync(
        DeduplicationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Generates a content fingerprint for future comparison.
    /// </summary>
    /// <param name="content">The text content to fingerprint.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The content fingerprint.</returns>
    Task<ContentFingerprint?> GenerateFingerprintAsync(
        string content,
        CancellationToken ct = default);
}

/// <summary>
/// Request for deduplication check.
/// </summary>
public class DeduplicationRequest
{
    /// <summary>
    /// The new content to check for duplication.
    /// </summary>
    public required string NewContent { get; init; }

    /// <summary>
    /// Hash of the new content (for quick comparison).
    /// </summary>
    public required string NewContentHash { get; init; }

    /// <summary>
    /// Hash of the previous content (if available).
    /// </summary>
    public string? PreviousContentHash { get; init; }

    /// <summary>
    /// Fingerprint of the previous content (for semantic comparison).
    /// </summary>
    public ContentFingerprint? PreviousFingerprint { get; init; }

    /// <summary>
    /// Watch ID for logging and metrics.
    /// </summary>
    public Guid WatchId { get; init; }

    /// <summary>
    /// Similarity threshold for semantic deduplication (0.0 to 1.0).
    /// Content with similarity above this threshold is considered a duplicate.
    /// </summary>
    public float SimilarityThreshold { get; init; } = 0.95f;

    /// <summary>
    /// Whether to use semantic fingerprinting (LLM-powered) in addition to hash comparison.
    /// </summary>
    public bool UseSemanticComparison { get; init; } = true;
}

/// <summary>
/// Result of deduplication check.
/// </summary>
public class DeduplicationResult
{
    /// <summary>
    /// Whether the content is considered a duplicate.
    /// </summary>
    public required bool IsDuplicate { get; init; }

    /// <summary>
    /// The type of duplicate detection that matched.
    /// </summary>
    public DuplicateType DuplicateType { get; init; }

    /// <summary>
    /// Similarity score if semantic comparison was used (0.0 to 1.0).
    /// </summary>
    public float? SimilarityScore { get; init; }

    /// <summary>
    /// Fingerprint of the new content (for caching).
    /// </summary>
    public ContentFingerprint? NewFingerprint { get; init; }

    /// <summary>
    /// Reason for the deduplication decision.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Creates a result indicating content is not a duplicate.
    /// </summary>
    public static DeduplicationResult NotDuplicate(
        ContentFingerprint? fingerprint = null,
        float? similarityScore = null) => new()
    {
        IsDuplicate = false,
        DuplicateType = DuplicateType.None,
        NewFingerprint = fingerprint,
        SimilarityScore = similarityScore,
        Reason = "Content is unique"
    };

    /// <summary>
    /// Creates a result indicating exact hash match.
    /// </summary>
    public static DeduplicationResult ExactHashMatch() => new()
    {
        IsDuplicate = true,
        DuplicateType = DuplicateType.ExactHash,
        SimilarityScore = 1.0f,
        Reason = "Exact content hash match"
    };

    /// <summary>
    /// Creates a result indicating semantic similarity above threshold.
    /// </summary>
    public static DeduplicationResult SemanticMatch(
        float similarityScore,
        ContentFingerprint? fingerprint = null) => new()
    {
        IsDuplicate = true,
        DuplicateType = DuplicateType.SemanticSimilarity,
        SimilarityScore = similarityScore,
        NewFingerprint = fingerprint,
        Reason = $"Semantic similarity ({similarityScore:P0}) exceeds threshold"
    };
}

/// <summary>
/// Type of duplicate detection.
/// </summary>
public enum DuplicateType
{
    /// <summary>
    /// Content is not a duplicate.
    /// </summary>
    None,

    /// <summary>
    /// Exact hash match (identical content).
    /// </summary>
    ExactHash,

    /// <summary>
    /// Semantic similarity above threshold.
    /// </summary>
    SemanticSimilarity
}
