using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Evaluates LLM scoring dimensions to determine the appropriate alert level
/// for a tracked item. Hard-fail dimensions are configurable via TrackingConfig.
/// </summary>
public interface IAlertPolicyService
{
    /// <summary>
    /// Evaluate dimensions to determine alert level.
    /// Uses hardFailDimensions from TrackingConfig when provided, otherwise defaults.
    /// </summary>
    AlertPolicyResult Evaluate(
        string? dimensionsJson,
        string? recommendation,
        DateTime? deadline = null,
        TrackingConfig? config = null);

    /// <summary>
    /// Determine the alert level for a removed item (stale detection).
    /// </summary>
    AlertPolicyResult EvaluateRemoval(TrackedItem item);
}
