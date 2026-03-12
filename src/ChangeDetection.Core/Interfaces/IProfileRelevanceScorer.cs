using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Scores change relevance against a structured analysis profile.
/// Implementations are domain-specific (e.g., job matching, real estate, stock monitoring).
/// ChangeAnalyzer delegates to registered scorers when a profile is present.
/// </summary>
public interface IProfileRelevanceScorer
{
    /// <summary>
    /// Whether this scorer can handle the given profile JSON.
    /// Implementations should check for domain-specific keys.
    /// </summary>
    bool CanScore(string analysisProfileJson);

    /// <summary>
    /// Score the change against the analysis profile.
    /// Returns overall score (0-1), human-readable reason, and optional dimensions JSON.
    /// </summary>
    Task<ProfileRelevanceResult> ScoreAsync(
        ChangeAnalysisRequest request,
        string? semanticSummary,
        CancellationToken ct);
}

/// <summary>
/// Result of profile-based relevance scoring.
/// </summary>
public record ProfileRelevanceResult(
    float Score,
    string? Reason,
    string? DimensionsJson,
    string? ExtractedEntitiesJson = null,
    string? BriefSummary = null);
