using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Evaluates LLM scoring dimensions to determine the appropriate alert level
/// for a job listing. Maps PASS/FAIL/STRETCH/UNKNOWN across all dimensions
/// to HIGH/MEDIUM/SILENT/INFO alert levels.
/// </summary>
public interface IAlertPolicyService
{
    /// <summary>
    /// Evaluate dimensions from a profile relevance result to determine alert level.
    /// </summary>
    /// <param name="dimensionsJson">Per-dimension scores JSON from ProfileRelevanceResult.</param>
    /// <param name="recommendation">LLM recommendation string (APPLY/REVIEW/SKIP).</param>
    /// <param name="deadline">Parsed deadline date, if available.</param>
    /// <returns>Alert policy result with level and reasoning.</returns>
    JobAlertPolicyResult Evaluate(string? dimensionsJson, string? recommendation, DateTime? deadline = null);

    /// <summary>
    /// Determine the alert level for a removed listing (stale detection).
    /// </summary>
    JobAlertPolicyResult EvaluateRemoval(TrackedListing listing);
}
