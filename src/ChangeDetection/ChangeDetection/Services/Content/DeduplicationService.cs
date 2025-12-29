using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Content;

/// <summary>
/// Service for detecting and handling duplicate content during ingestion.
/// Combines hash-based and semantic fingerprint comparison to identify duplicates.
/// </summary>
public class DeduplicationService(
    IContentEnricher contentEnricher,
    ILogger<DeduplicationService> logger) : IDeduplicationService
{
    /// <inheritdoc />
    public async Task<DeduplicationResult> CheckForDuplicateAsync(
        DeduplicationRequest request,
        CancellationToken ct = default)
    {
        logger.LogDebug(
            "Checking for duplicate content for watch {WatchId}",
            request.WatchId);

        // Step 1: Quick hash comparison (fastest check)
        if (!string.IsNullOrEmpty(request.PreviousContentHash) &&
            request.NewContentHash == request.PreviousContentHash)
        {
            logger.LogDebug(
                "Exact hash match detected for watch {WatchId}",
                request.WatchId);
            return DeduplicationResult.ExactHashMatch();
        }

        // Step 2: If semantic comparison is disabled or no previous fingerprint, content is unique
        if (!request.UseSemanticComparison || request.PreviousFingerprint == null)
        {
            logger.LogDebug(
                "No previous fingerprint or semantic comparison disabled for watch {WatchId}",
                request.WatchId);

            // Generate fingerprint for future comparisons if semantic comparison is enabled
            ContentFingerprint? newFingerprint = null;
            if (request.UseSemanticComparison)
            {
                newFingerprint = await GenerateFingerprintAsync(request.NewContent, ct);
            }

            return DeduplicationResult.NotDuplicate(newFingerprint);
        }

        // Step 3: Semantic fingerprint comparison
        try
        {
            var newFingerprint = await GenerateFingerprintAsync(request.NewContent, ct);
            if (newFingerprint == null)
            {
                logger.LogWarning(
                    "Failed to generate fingerprint for watch {WatchId}, treating as unique",
                    request.WatchId);
                return DeduplicationResult.NotDuplicate();
            }

            var similarity = newFingerprint.CompareSimilarity(request.PreviousFingerprint);

            logger.LogDebug(
                "Semantic similarity for watch {WatchId}: {Similarity:P2} (threshold: {Threshold:P2})",
                request.WatchId, similarity, request.SimilarityThreshold);

            if (similarity >= request.SimilarityThreshold)
            {
                logger.LogInformation(
                    "Semantic duplicate detected for watch {WatchId}: {Similarity:P0} similarity",
                    request.WatchId, similarity);
                return DeduplicationResult.SemanticMatch(similarity, newFingerprint);
            }

            return DeduplicationResult.NotDuplicate(newFingerprint, similarity);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Semantic comparison failed for watch {WatchId}, treating as unique",
                request.WatchId);
            return DeduplicationResult.NotDuplicate();
        }
    }

    /// <inheritdoc />
    public async Task<ContentFingerprint?> GenerateFingerprintAsync(
        string content,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            logger.LogDebug("Empty content provided for fingerprinting");
            return null;
        }

        try
        {
            return await contentEnricher.GenerateFingerprintAsync(content, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate content fingerprint");
            return null;
        }
    }
}
