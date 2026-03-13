using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Evaluates LLM scoring dimensions to determine the appropriate alert level
/// for a tracked item. Maps PASS/FAIL/STRETCH/UNKNOWN across all dimensions
/// to HIGH/MEDIUM/SILENT/INFO alert levels.
/// </summary>
public interface IAlertPolicyService
{
    /// <summary>
    /// Evaluate dimensions from a profile relevance result to determine alert level.
    /// </summary>
    AlertPolicyResult Evaluate(string? dimensionsJson, string? recommendation, DateTime? deadline = null);

    /// <summary>
    /// Determine the alert level for a removed item (stale detection).
    /// </summary>
    AlertPolicyResult EvaluateRemoval(TrackedItem item);
}
