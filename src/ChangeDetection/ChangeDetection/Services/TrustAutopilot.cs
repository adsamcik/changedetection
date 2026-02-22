using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services;

/// <summary>
/// Computes optimal notification thresholds by analyzing user feedback patterns.
/// Uses feedback on past events to find a threshold that maximizes precision
/// while maintaining acceptable recall (F1-score optimization).
/// </summary>
public class TrustAutopilot : ITrustAutopilot
{
    private const int MinSampleSize = 10;
    private const float ThresholdStep = 0.05f;
    private const float MinThreshold = 0.1f;
    private const float MaxThreshold = 0.9f;

    public TrustRecommendation? ComputeRecommendation(
        IReadOnlyList<ChangeEvent> events, float currentThreshold)
    {
        var scored = events
            .Where(e => e.Feedback != UserFeedback.None && e.RelevanceScore.HasValue)
            .ToList();

        if (scored.Count < MinSampleSize)
            return null;

        var bestF1 = 0.0;
        var bestThreshold = currentThreshold;
        var bestPrecision = 0.0;
        var bestRecall = 0.0;

        for (var t = MinThreshold; t <= MaxThreshold; t += ThresholdStep)
        {
            var (precision, recall) = ComputeMetrics(scored, t);
            var f1 = precision + recall > 0
                ? 2 * precision * recall / (precision + recall)
                : 0.0;

            if (f1 > bestF1)
            {
                bestF1 = f1;
                bestThreshold = t;
                bestPrecision = precision;
                bestRecall = recall;
            }
        }

        var roundedThreshold = MathF.Round(bestThreshold / ThresholdStep) * ThresholdStep;

        if (MathF.Abs(roundedThreshold - currentThreshold) < ThresholdStep)
            return null; // No meaningful change

        var direction = roundedThreshold > currentThreshold ? "raise" : "lower";
        return new TrustRecommendation(
            roundedThreshold, currentThreshold, scored.Count,
            bestPrecision, bestRecall,
            $"Recommend {direction} threshold from {currentThreshold:F2} to {roundedThreshold:F2} based on {scored.Count} feedback samples (F1={bestF1:F2})");
    }

    internal static (double Precision, double Recall) ComputeMetrics(
        IReadOnlyList<ChangeEvent> events, float threshold)
    {
        var tp = 0; // Helpful events above threshold
        var fp = 0; // FalsePositive/Irrelevant events above threshold
        var fn = 0; // Helpful events below threshold (would be missed)

        foreach (var e in events)
        {
            var aboveThreshold = e.RelevanceScore!.Value >= threshold;
            var isPositive = e.Feedback == UserFeedback.Helpful;
            var isNegative = e.Feedback is UserFeedback.FalsePositive or UserFeedback.Irrelevant;

            if (aboveThreshold && isPositive) tp++;
            else if (aboveThreshold && isNegative) fp++;
            else if (!aboveThreshold && isPositive) fn++;
        }

        var precision = tp + fp > 0 ? (double)tp / (tp + fp) : 0.0;
        var recall = tp + fn > 0 ? (double)tp / (tp + fn) : 0.0;
        return (precision, recall);
    }
}
