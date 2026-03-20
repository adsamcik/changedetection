namespace ChangeDetection.Shared.Dtos;

/// <summary>
/// DTO for reading a notification template.
/// </summary>
public class NotificationTemplateDto
{
    /// <summary>
    /// Unique identifier for this template.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Human-readable name for this template.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The type of notification this template is for.
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Whether this is a built-in template (cannot be deleted).
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Whether to generate an LLM summary when {LlmSummary} placeholder is used.
    /// </summary>
    public bool GenerateLlmSummary { get; set; }

    // Email templates
    public string? EmailSubjectTemplate { get; set; }
    public string? EmailBodyHtmlTemplate { get; set; }
    public string? EmailBodyTextTemplate { get; set; }

    // Discord templates
    public string? DiscordTitleTemplate { get; set; }
    public string? DiscordBodyTemplate { get; set; }

    // Webhook templates
    public string? WebhookPayloadTemplate { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
}

/// <summary>
/// DTO for creating or updating a notification template.
/// </summary>
public class NotificationTemplateCreateDto
{
    /// <summary>
    /// Human-readable name for this template.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// The type of notification this template is for.
    /// Values: ContentChange, PriceAlert, StockAlert, ThresholdTriggered, ItemAdded, ItemRemoved, SchemaDrift
    /// </summary>
    public required string Type { get; set; }

    /// <summary>
    /// Whether to generate an LLM summary when {LlmSummary} placeholder is used.
    /// </summary>
    public bool GenerateLlmSummary { get; set; }

    // Email templates
    public string? EmailSubjectTemplate { get; set; }
    public string? EmailBodyHtmlTemplate { get; set; }
    public string? EmailBodyTextTemplate { get; set; }

    // Discord templates
    public string? DiscordTitleTemplate { get; set; }
    public string? DiscordBodyTemplate { get; set; }

    // Webhook templates
    public string? WebhookPayloadTemplate { get; set; }
}

/// <summary>
/// DTO for template validation results.
/// </summary>
public class TemplateValidationResultDto
{
    public bool IsValid { get; set; } = true;
    public List<string> UnknownPlaceholders { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// DTO for available placeholder information.
/// </summary>
public class PlaceholderInfoDto
{
    public required string Name { get; set; }
    public required string Description { get; set; }
}

/// <summary>
/// DTO for global SMTP settings.
/// </summary>
public class SmtpSettingsDto
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
}

/// <summary>
/// DTO for updating SMTP settings.
/// </summary>
public class SmtpSettingsUpdateDto
{
    public bool? Enabled { get; set; }
    public string? Host { get; set; }
    public int? Port { get; set; }
    public bool? UseSsl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? FromEmail { get; set; }
    public string? FromName { get; set; }
}

/// <summary>
/// DTO for testing notification delivery.
/// </summary>
public class TestNotificationDto
{
    /// <summary>
    /// Channel type to test: Email, Discord, Webhook.
    /// </summary>
    public required string ChannelType { get; set; }

    /// <summary>
    /// Target address (email, webhook URL, or Discord webhook URL).
    /// </summary>
    public required string Target { get; set; }

    /// <summary>
    /// Optional template ID to use for the test.
    /// </summary>
    public Guid? TemplateId { get; set; }
}

/// <summary>
/// Result of a test notification.
/// </summary>
public class TestNotificationResultDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? Details { get; set; }
}

/// <summary>
/// Global notification channel settings configured from the Settings page.
/// </summary>
public class NotificationChannelSettingsDto
{
    public bool EmailEnabled { get; set; }
    public string? EmailAddress { get; set; }
    public bool DiscordEnabled { get; set; }
    public string? DiscordWebhookUrl { get; set; }
    public bool WebhookEnabled { get; set; }
    public string? WebhookUrl { get; set; }
    public bool BrowserEnabled { get; set; }
    public string? DefaultChannelName { get; set; }
}
