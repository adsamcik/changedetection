using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Service for evaluating filter rules against extracted objects and changes.
/// Returns actions to apply based on matched conditions.
/// </summary>
public interface IFilterEvaluationService
{
    /// <summary>
    /// Evaluates filter rules against a diff result.
    /// </summary>
    /// <param name="diffResult">The object diff result.</param>
    /// <param name="rules">Filter rules to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Evaluation result with matched rules and actions to apply.</returns>
    Task<FilterEvaluationResult> EvaluateAsync(
        ObjectDiffResult diffResult,
        IReadOnlyList<FilterRule> rules,
        CancellationToken ct = default);

    /// <summary>
    /// Evaluates filter rules against a single extracted object.
    /// </summary>
    /// <param name="obj">The extracted object.</param>
    /// <param name="changeType">Type of change (added, removed, modified).</param>
    /// <param name="rules">Filter rules to evaluate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Actions to apply for this object.</returns>
    Task<List<AppliedFilterAction>> EvaluateObjectAsync(
        ExtractedObject obj,
        ChangeType changeType,
        IReadOnlyList<FilterRule> rules,
        CancellationToken ct = default);
}

/// <summary>
/// Result of evaluating filter rules.
/// </summary>
public class FilterEvaluationResult
{
    /// <summary>
    /// All actions to apply based on matched rules.
    /// </summary>
    public List<AppliedFilterAction> Actions { get; set; } = [];

    /// <summary>
    /// Whether any rule suppressed notifications.
    /// </summary>
    public bool SuppressNotification { get; set; }

    /// <summary>
    /// Tags to add from filter rules.
    /// </summary>
    public List<string> TagsToAdd { get; set; } = [];

    /// <summary>
    /// Importance override from filter rules (highest priority wins).
    /// </summary>
    public ChangeImportance? ImportanceOverride { get; set; }

    /// <summary>
    /// Notification channels to route to.
    /// </summary>
    public List<string> RouteToChannels { get; set; } = [];

    /// <summary>
    /// Objects that matched at least one rule.
    /// </summary>
    public List<FilteredObjectResult> FilteredObjects { get; set; } = [];

    /// <summary>
    /// Objects flagged for review.
    /// </summary>
    public List<ExtractedObject> ObjectsRequiringReview { get; set; } = [];

    /// <summary>
    /// Objects highlighted by rules.
    /// </summary>
    public List<ExtractedObject> HighlightedObjects { get; set; } = [];
}

/// <summary>
/// Result of filtering a single object.
/// </summary>
public class FilteredObjectResult
{
    /// <summary>
    /// The object that was filtered.
    /// </summary>
    public required ExtractedObject Object { get; set; }

    /// <summary>
    /// Rules that matched this object.
    /// </summary>
    public List<Guid> MatchedRuleIds { get; set; } = [];

    /// <summary>
    /// Actions applied to this object.
    /// </summary>
    public List<AppliedFilterAction> AppliedActions { get; set; } = [];
}
