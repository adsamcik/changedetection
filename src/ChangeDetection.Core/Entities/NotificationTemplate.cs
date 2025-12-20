namespace ChangeDetection.Core.Entities;

/// <summary>
/// Defines a notification template with channel-specific variants.
/// Templates support placeholders that are replaced at render time.
/// </summary>
public class NotificationTemplate
{
    /// <summary>
    /// Unique identifier for this template.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Human-readable name for this template.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The type of notification this template is for.
    /// </summary>
    public NotificationTemplateType Type { get; set; }

    /// <summary>
    /// Whether this is a built-in template (cannot be deleted).
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Whether to generate an LLM summary when {LlmSummary} placeholder is used.
    /// Controls LLM costs - only triggers generation when true.
    /// </summary>
    public bool GenerateLlmSummary { get; set; }

    // ========== Email Templates ==========

    /// <summary>
    /// Email subject line template.
    /// </summary>
    public string? EmailSubjectTemplate { get; set; }

    /// <summary>
    /// Email body HTML template.
    /// </summary>
    public string? EmailBodyHtmlTemplate { get; set; }

    /// <summary>
    /// Email body plain text template (fallback for non-HTML clients).
    /// </summary>
    public string? EmailBodyTextTemplate { get; set; }

    // ========== Discord Templates ==========

    /// <summary>
    /// Discord embed title template.
    /// </summary>
    public string? DiscordTitleTemplate { get; set; }

    /// <summary>
    /// Discord embed body/description template.
    /// </summary>
    public string? DiscordBodyTemplate { get; set; }

    // ========== Webhook Templates ==========

    /// <summary>
    /// Custom JSON payload template for webhooks.
    /// If null, uses default structured payload.
    /// </summary>
    public string? WebhookPayloadTemplate { get; set; }

    /// <summary>
    /// When this template was created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this template was last modified.
    /// </summary>
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>
/// Types of notifications that templates can be applied to.
/// </summary>
public enum NotificationTemplateType
{
    /// <summary>General content change notification.</summary>
    ContentChange = 0,

    /// <summary>Price change alert.</summary>
    PriceAlert = 1,

    /// <summary>Stock availability change alert.</summary>
    StockAlert = 2,

    /// <summary>Numeric threshold triggered alert.</summary>
    ThresholdTriggered = 3,

    /// <summary>New item added to a list.</summary>
    ItemAdded = 4,

    /// <summary>Item removed from a list.</summary>
    ItemRemoved = 5,

    /// <summary>Schema drift detected (page structure changed).</summary>
    SchemaDrift = 6
}

/// <summary>
/// Result of template placeholder validation.
/// </summary>
public class TemplateValidationResult
{
    /// <summary>
    /// Whether the template is valid (no errors, may have warnings).
    /// </summary>
    public bool IsValid { get; set; } = true;

    /// <summary>
    /// Placeholders found in the template that are not recognized.
    /// These will be rendered as literals at runtime.
    /// </summary>
    public List<string> UnknownPlaceholders { get; set; } = [];

    /// <summary>
    /// Warning messages about the template.
    /// </summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>
    /// Error messages that prevent the template from being valid.
    /// </summary>
    public List<string> Errors { get; set; } = [];
}
