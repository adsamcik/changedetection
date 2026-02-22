using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Computes optimal notification thresholds from user feedback data.
/// </summary>
public interface ITrustAutopilot
{
    /// <summary>
    /// Computes an optimal MinRelevanceForNotification threshold based on feedback history.
    /// Returns null if insufficient feedback data.
    /// </summary>
    TrustRecommendation? ComputeRecommendation(IReadOnlyList<ChangeEvent> events, float currentThreshold);
}

/// <summary>
/// Recommendation from the trust autopilot for threshold adjustment.
/// </summary>
public record TrustRecommendation(
    float RecommendedThreshold,
    float CurrentThreshold,
    int SampleSize,
    double EstimatedPrecision,
    double EstimatedRecall,
    string Reason);
