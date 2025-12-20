using ChangeDetection.Core.Entities;

namespace ChangeDetection.Core.Interfaces;

/// <summary>
/// Engine for rendering notification templates with placeholder substitution.
/// Supports hardcoded default templates with database overrides.
/// </summary>
public interface INotificationTemplateEngine
{
    /// <summary>
    /// Renders a template string by replacing placeholders with values from the context.
    /// Unknown placeholders are left as literals (graceful fallback).
    /// </summary>
    /// <param name="template">The template string with {Placeholder} syntax.</param>
    /// <param name="context">The context containing values for placeholders.</param>
    /// <returns>The rendered string with placeholders replaced.</returns>
    Task<string> RenderAsync(string template, NotificationContext context, CancellationToken ct = default);

    /// <summary>
    /// Validates placeholders in a template.
    /// Returns warnings for unknown placeholders (which will be rendered as literals).
    /// </summary>
    TemplateValidationResult ValidatePlaceholders(string template);

    /// <summary>
    /// Gets the effective template for a notification type, merging defaults with DB overrides.
    /// </summary>
    /// <param name="type">The notification type.</param>
    /// <param name="customTemplateId">Optional custom template ID to use instead of default.</param>
    Task<NotificationTemplate> GetEffectiveTemplateAsync(
        NotificationTemplateType type,
        Guid? customTemplateId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all available placeholders with descriptions.
    /// Used for template editor UI.
    /// </summary>
    IReadOnlyDictionary<string, string> GetAvailablePlaceholders();
}

/// <summary>
/// Context for rendering notification templates.
/// Contains all data that can be referenced by placeholders.
/// </summary>
public class NotificationContext
{
    /// <summary>
    /// The watch that triggered the notification.
    /// </summary>
    public required WatchedSite Watch { get; init; }

    /// <summary>
    /// The change event (if this is a change notification).
    /// </summary>
    public ChangeEvent? Change { get; init; }

    /// <summary>
    /// The alert evaluation result (if this is an alert notification).
    /// </summary>
    public AlertEvaluationResult? AlertResult { get; init; }

    /// <summary>
    /// The specific field that changed (for field-level alerts).
    /// </summary>
    public SchemaField? Field { get; init; }

    /// <summary>
    /// The threshold that was triggered (for threshold alerts).
    /// </summary>
    public FieldAlertThreshold? TriggeredThreshold { get; init; }

    /// <summary>
    /// Previous value (for change comparisons).
    /// </summary>
    public object? OldValue { get; init; }

    /// <summary>
    /// New/current value.
    /// </summary>
    public object? NewValue { get; init; }

    /// <summary>
    /// Price-specific: previous price value.
    /// </summary>
    public decimal? OldPrice { get; init; }

    /// <summary>
    /// Price-specific: new price value.
    /// </summary>
    public decimal? NewPrice { get; init; }

    /// <summary>
    /// Currency code (e.g., "CZK", "USD").
    /// </summary>
    public string? Currency { get; init; }

    /// <summary>
    /// Stock-specific: previous stock status.
    /// </summary>
    public StockStatus? OldStockStatus { get; init; }

    /// <summary>
    /// Stock-specific: new stock status.
    /// </summary>
    public StockStatus? NewStockStatus { get; init; }

    /// <summary>
    /// Calculated change percentage (if applicable).
    /// </summary>
    public double? ChangePercent { get; init; }

    /// <summary>
    /// Calculated absolute change (if applicable).
    /// </summary>
    public double? ChangeAbsolute { get; init; }

    /// <summary>
    /// Direction of change: "increased", "decreased", or "unchanged".
    /// </summary>
    public string? ChangeDirection { get; init; }

    /// <summary>
    /// Whether to generate LLM summary for {LlmSummary} placeholder.
    /// </summary>
    public bool GenerateLlmSummary { get; init; }

    /// <summary>
    /// Pre-generated LLM summary (if already computed).
    /// </summary>
    public string? LlmSummary { get; init; }

    /// <summary>
    /// Additional custom values that can be referenced in templates.
    /// Key is the placeholder name (without braces).
    /// </summary>
    public Dictionary<string, object?> CustomValues { get; init; } = [];
}

/// <summary>
/// Result of evaluating alert thresholds against a value change.
/// </summary>
public class AlertEvaluationResult
{
    /// <summary>
    /// Whether any thresholds were triggered.
    /// </summary>
    public bool HasTriggeredAlerts => TriggeredThresholds.Count > 0;

    /// <summary>
    /// List of thresholds that were triggered.
    /// </summary>
    public List<TriggeredThreshold> TriggeredThresholds { get; init; } = [];

    /// <summary>
    /// The highest importance level among triggered thresholds.
    /// </summary>
    public ChangeImportance? HighestImportance { get; init; }

    /// <summary>
    /// Combined message from all triggered thresholds.
    /// </summary>
    public string? CombinedMessage { get; init; }
}

/// <summary>
/// Details about a single triggered threshold.
/// </summary>
public class TriggeredThreshold
{
    /// <summary>
    /// The threshold configuration that was triggered.
    /// </summary>
    public required FieldAlertThreshold Threshold { get; init; }

    /// <summary>
    /// The field this threshold belongs to.
    /// </summary>
    public required SchemaField Field { get; init; }

    /// <summary>
    /// Human-readable message describing why this threshold triggered.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// The old value that was compared.
    /// </summary>
    public double? OldValue { get; init; }

    /// <summary>
    /// The new value that triggered the threshold.
    /// </summary>
    public double? NewValue { get; init; }

    /// <summary>
    /// The calculated change (absolute or percentage, depending on condition type).
    /// </summary>
    public double? CalculatedChange { get; init; }
}
