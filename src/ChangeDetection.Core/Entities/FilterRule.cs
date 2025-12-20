namespace ChangeDetection.Core.Entities;

/// <summary>
/// A filter rule that evaluates extracted objects and triggers actions.
/// Rules are per-watch, with potential for template export/import later.
/// </summary>
public class FilterRule
{
    /// <summary>
    /// Unique identifier for this rule.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name for this rule.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Optional description of what this rule does.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Conditions that must be met for this rule to trigger.
    /// </summary>
    public List<FilterCondition> Conditions { get; set; } = [];

    /// <summary>
    /// How conditions are combined (AND = all must match, OR = any must match).
    /// </summary>
    public FilterLogic Logic { get; set; } = FilterLogic.And;

    /// <summary>
    /// Actions to execute when conditions are met.
    /// </summary>
    public List<FilterAction> Actions { get; set; } = [];

    /// <summary>
    /// Whether this rule is enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Priority for rule ordering (higher = evaluated first).
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether to stop evaluating further rules after this one matches.
    /// </summary>
    public bool StopProcessing { get; set; }
}

/// <summary>
/// A single condition within a filter rule.
/// </summary>
public class FilterCondition
{
    /// <summary>
    /// Name of the field to evaluate.
    /// Special values: "$changeType" for item add/remove/modify,
    /// "$fieldChanged" for specific field changes.
    /// </summary>
    public required string FieldName { get; set; }

    /// <summary>
    /// Comparison operator to use.
    /// </summary>
    public FilterOperator Operator { get; set; } = FilterOperator.Contains;

    /// <summary>
    /// Value to compare against.
    /// Interpretation depends on operator and field type.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Whether to negate the condition result.
    /// </summary>
    public bool Negate { get; set; }
}

/// <summary>
/// Comparison operators for filter conditions.
/// </summary>
public enum FilterOperator
{
    /// <summary>Field value exactly equals the specified value.</summary>
    Equals,

    /// <summary>Field value contains the specified substring.</summary>
    Contains,

    /// <summary>Field value is greater than the specified value (for numbers/dates).</summary>
    GreaterThan,

    /// <summary>Field value is less than the specified value (for numbers/dates).</summary>
    LessThan,

    /// <summary>Field value matches the specified regex pattern.</summary>
    Regex,

    /// <summary>Field value is null or empty.</summary>
    IsEmpty,

    /// <summary>Field value is not null and not empty.</summary>
    IsNotEmpty,

    /// <summary>Field value starts with the specified prefix.</summary>
    StartsWith,

    /// <summary>Field value ends with the specified suffix.</summary>
    EndsWith,

    // ========== Numeric Operators (for stock/price tracking) ==========

    /// <summary>Field value is greater than or equal to the specified value.</summary>
    GreaterThanOrEqual,

    /// <summary>Field value is less than or equal to the specified value.</summary>
    LessThanOrEqual,

    /// <summary>Field value is between two values (inclusive). Use comma-separated: "min,max".</summary>
    Between,

    /// <summary>Field value is outside a range. Use comma-separated: "min,max".</summary>
    Outside,

    /// <summary>Field value changed by at least the specified absolute amount.</summary>
    ChangedByAmount,

    /// <summary>Field value changed by at least the specified percentage.</summary>
    ChangedByPercent,

    /// <summary>Field value dropped by at least the specified amount.</summary>
    DroppedByAmount,

    /// <summary>Field value dropped by at least the specified percentage.</summary>
    DroppedByPercent,

    /// <summary>Field value rose by at least the specified amount.</summary>
    RoseByAmount,

    /// <summary>Field value rose by at least the specified percentage.</summary>
    RoseByPercent,

    /// <summary>Field reached a new historical minimum.</summary>
    IsNewMinimum,

    /// <summary>Field reached a new historical maximum.</summary>
    IsNewMaximum,

    /// <summary>Field value is an outlier (> 2 standard deviations from mean).</summary>
    IsOutlier,

    /// <summary>Field trend matches specified direction (Up, Down, Stable, Volatile).</summary>
    TrendIs
}

/// <summary>
/// How multiple conditions are combined in a rule.
/// </summary>
public enum FilterLogic
{
    /// <summary>All conditions must match.</summary>
    And,

    /// <summary>Any condition must match.</summary>
    Or
}

/// <summary>
/// An action to execute when a filter rule matches.
/// </summary>
public class FilterAction
{
    /// <summary>
    /// Type of action to perform.
    /// </summary>
    public FilterActionType Type { get; set; }

    /// <summary>
    /// Parameters for the action, interpretation depends on action type.
    /// Examples:
    /// - SuppressNotification: no parameters needed
    /// - AddTag: "tag" = tag name
    /// - SetImportance: "level" = Low/Medium/High/Critical
    /// - RouteToChannel: "channel" = channel name
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = [];
}

/// <summary>
/// Types of actions that can be triggered by filter rules.
/// </summary>
public enum FilterActionType
{
    /// <summary>Do not send notifications for this change.</summary>
    SuppressNotification,

    /// <summary>Add a tag to the change event.</summary>
    AddTag,

    /// <summary>Override the importance level of the change.</summary>
    SetImportance,

    /// <summary>Route notification to a specific channel.</summary>
    RouteToChannel,

    /// <summary>Highlight this change in the UI.</summary>
    Highlight,

    /// <summary>Mark this change as requiring review.</summary>
    RequireReview,

    // ========== Stock/Price Tracking Actions ==========

    /// <summary>Set a price alert threshold for this field.</summary>
    SetPriceAlert,

    /// <summary>Update the baseline value for percentage calculations.</summary>
    UpdateBaseline,

    /// <summary>Record value to time-series history (even if TrackHistory is false).</summary>
    RecordToHistory,

    /// <summary>Send an immediate notification regardless of other settings.</summary>
    ImmediateNotify,

    /// <summary>Add to watchlist/favorites for quick access.</summary>
    AddToWatchlist,

    /// <summary>Execute a webhook with change data.</summary>
    TriggerWebhook
}

/// <summary>
/// Record of a filter action that was applied to a change.
/// </summary>
public class AppliedFilterAction
{
    /// <summary>
    /// ID of the rule that triggered this action.
    /// </summary>
    public Guid RuleId { get; set; }

    /// <summary>
    /// Name of the rule for display.
    /// </summary>
    public string? RuleName { get; set; }

    /// <summary>
    /// The action that was applied.
    /// </summary>
    public required FilterAction Action { get; set; }

    /// <summary>
    /// When the action was applied.
    /// </summary>
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
}
