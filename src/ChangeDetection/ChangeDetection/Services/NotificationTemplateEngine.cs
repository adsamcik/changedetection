using System.Text.RegularExpressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services;

/// <summary>
/// Template engine for rendering notification messages with placeholder substitution.
/// Supports hardcoded default templates with database overrides.
/// </summary>
public partial class NotificationTemplateEngine(
    IRepository<NotificationTemplate> templateRepository,
    ILlmProviderChain llmProvider,
    ILogger<NotificationTemplateEngine> logger) : INotificationTemplateEngine
{
    private static readonly Regex PlaceholderRegex = GeneratePlaceholderRegex();

    /// <summary>
    /// All available placeholders with descriptions for the template editor.
    /// </summary>
    private static readonly Dictionary<string, string> AvailablePlaceholdersDict = new()
    {
        // Watch properties
        ["Watch.Name"] = "The name of the watch",
        ["Watch.Url"] = "The URL being watched",

        // Price-specific
        ["Price"] = "The current price value",
        ["OldPrice"] = "The previous price value",
        ["Currency"] = "The currency code (e.g., CZK, USD)",

        // Stock-specific
        ["StockStatus"] = "The current stock status (e.g., In Stock, Out of Stock)",
        ["OldStockStatus"] = "The previous stock status",

        // Generic value changes
        ["OldValue"] = "The previous value",
        ["NewValue"] = "The new/current value",
        ["ChangePercent"] = "The percentage change (e.g., -15.5)",
        ["ChangeAbsolute"] = "The absolute change amount",
        ["ChangeDirection"] = "Direction of change: increased, decreased, or unchanged",

        // Field/threshold info
        ["FieldName"] = "The name of the field that changed",
        ["AlertCondition"] = "Description of the triggered alert condition",
        ["ThresholdValue"] = "The threshold value that was crossed",

        // Change event
        ["Change.DetectedAt"] = "When the change was detected",
        ["Change.Importance"] = "The importance level of the change",
        ["Change.LinesAdded"] = "Number of lines added",
        ["Change.LinesRemoved"] = "Number of lines removed",

        // LLM-generated
        ["LlmSummary"] = "AI-generated summary of the change (requires GenerateLlmSummary=true)"
    };

    /// <summary>
    /// Default templates for each notification type.
    /// </summary>
    private static readonly Dictionary<NotificationTemplateType, NotificationTemplate> DefaultTemplates = new()
    {
        [NotificationTemplateType.PriceAlert] = new NotificationTemplate
        {
            Id = Guid.Parse("00000000-0000-0000-0001-000000000001"),
            Name = "Default Price Alert",
            Type = NotificationTemplateType.PriceAlert,
            IsBuiltIn = true,
            EmailSubjectTemplate = "💰 Price Alert: {Watch.Name} is now {Price} {Currency}",
            EmailBodyHtmlTemplate = """
                <h2>Price Alert</h2>
                <p><strong>Watch:</strong> {Watch.Name}</p>
                <p><strong>URL:</strong> <a href="{Watch.Url}">{Watch.Url}</a></p>
                <hr>
                <p>The price has <strong>{ChangeDirection}</strong> from <strong>{OldPrice} {Currency}</strong> to <strong>{Price} {Currency}</strong></p>
                <p>Change: <strong>{ChangePercent}%</strong> ({ChangeAbsolute} {Currency})</p>
                """,
            EmailBodyTextTemplate = "Price Alert: {Watch.Name}\nPrice changed from {OldPrice} to {Price} {Currency} ({ChangePercent}%)",
            DiscordTitleTemplate = "💰 {Watch.Name}",
            DiscordBodyTemplate = "Price {ChangeDirection} from **{OldPrice}** to **{Price} {Currency}** ({ChangePercent}%)"
        },

        [NotificationTemplateType.StockAlert] = new NotificationTemplate
        {
            Id = Guid.Parse("00000000-0000-0000-0001-000000000002"),
            Name = "Default Stock Alert",
            Type = NotificationTemplateType.StockAlert,
            IsBuiltIn = true,
            EmailSubjectTemplate = "📦 {Watch.Name} is now {StockStatus}",
            EmailBodyHtmlTemplate = """
                <h2>Stock Alert</h2>
                <p><strong>Watch:</strong> {Watch.Name}</p>
                <p><strong>URL:</strong> <a href="{Watch.Url}">{Watch.Url}</a></p>
                <hr>
                <p>Stock status changed from <strong>{OldStockStatus}</strong> to <strong>{StockStatus}</strong></p>
                """,
            EmailBodyTextTemplate = "Stock Alert: {Watch.Name}\nStatus changed from {OldStockStatus} to {StockStatus}",
            DiscordTitleTemplate = "📦 {Watch.Name}",
            DiscordBodyTemplate = "Stock status: **{OldStockStatus}** → **{StockStatus}**"
        },

        [NotificationTemplateType.ThresholdTriggered] = new NotificationTemplate
        {
            Id = Guid.Parse("00000000-0000-0000-0001-000000000003"),
            Name = "Default Threshold Alert",
            Type = NotificationTemplateType.ThresholdTriggered,
            IsBuiltIn = true,
            EmailSubjectTemplate = "⚠️ Alert: {FieldName} {AlertCondition}",
            EmailBodyHtmlTemplate = """
                <h2>Threshold Alert</h2>
                <p><strong>Watch:</strong> {Watch.Name}</p>
                <p><strong>URL:</strong> <a href="{Watch.Url}">{Watch.Url}</a></p>
                <hr>
                <p><strong>{FieldName}</strong> has triggered: {AlertCondition}</p>
                <p>Value changed from <strong>{OldValue}</strong> to <strong>{NewValue}</strong></p>
                """,
            EmailBodyTextTemplate = "Alert: {FieldName} {AlertCondition}\nValue: {OldValue} → {NewValue}",
            DiscordTitleTemplate = "⚠️ {Watch.Name}",
            DiscordBodyTemplate = "**{FieldName}** {AlertCondition}: {OldValue} → {NewValue}"
        },

        [NotificationTemplateType.ContentChange] = new NotificationTemplate
        {
            Id = Guid.Parse("00000000-0000-0000-0001-000000000004"),
            Name = "Default Content Change",
            Type = NotificationTemplateType.ContentChange,
            IsBuiltIn = true,
            EmailSubjectTemplate = "🔔 Change detected: {Watch.Name}",
            EmailBodyHtmlTemplate = """
                <h2>Change Detected</h2>
                <p><strong>Watch:</strong> {Watch.Name}</p>
                <p><strong>URL:</strong> <a href="{Watch.Url}">{Watch.Url}</a></p>
                <p><strong>Detected at:</strong> {Change.DetectedAt}</p>
                <p><strong>Importance:</strong> {Change.Importance}</p>
                <hr>
                <p><strong>Changes:</strong> +{Change.LinesAdded} / -{Change.LinesRemoved} lines</p>
                """,
            EmailBodyTextTemplate = "Change detected: {Watch.Name}\n+{Change.LinesAdded} / -{Change.LinesRemoved} lines",
            DiscordTitleTemplate = "🔔 {Watch.Name}",
            DiscordBodyTemplate = "Change detected: +{Change.LinesAdded} / -{Change.LinesRemoved} lines"
        },

        [NotificationTemplateType.ItemAdded] = new NotificationTemplate
        {
            Id = Guid.Parse("00000000-0000-0000-0001-000000000005"),
            Name = "Default Item Added",
            Type = NotificationTemplateType.ItemAdded,
            IsBuiltIn = true,
            EmailSubjectTemplate = "➕ New item: {Watch.Name}",
            EmailBodyHtmlTemplate = """
                <h2>New Item Added</h2>
                <p><strong>Watch:</strong> {Watch.Name}</p>
                <p><strong>URL:</strong> <a href="{Watch.Url}">{Watch.Url}</a></p>
                <hr>
                <p>A new item has been added to the list.</p>
                """,
            EmailBodyTextTemplate = "New item added to {Watch.Name}",
            DiscordTitleTemplate = "➕ {Watch.Name}",
            DiscordBodyTemplate = "A new item has been added"
        },

        [NotificationTemplateType.ItemRemoved] = new NotificationTemplate
        {
            Id = Guid.Parse("00000000-0000-0000-0001-000000000006"),
            Name = "Default Item Removed",
            Type = NotificationTemplateType.ItemRemoved,
            IsBuiltIn = true,
            EmailSubjectTemplate = "➖ Item removed: {Watch.Name}",
            EmailBodyHtmlTemplate = """
                <h2>Item Removed</h2>
                <p><strong>Watch:</strong> {Watch.Name}</p>
                <p><strong>URL:</strong> <a href="{Watch.Url}">{Watch.Url}</a></p>
                <hr>
                <p>An item has been removed from the list.</p>
                """,
            EmailBodyTextTemplate = "Item removed from {Watch.Name}",
            DiscordTitleTemplate = "➖ {Watch.Name}",
            DiscordBodyTemplate = "An item has been removed"
        },

        [NotificationTemplateType.SchemaDrift] = new NotificationTemplate
        {
            Id = Guid.Parse("00000000-0000-0000-0001-000000000007"),
            Name = "Default Schema Drift",
            Type = NotificationTemplateType.SchemaDrift,
            IsBuiltIn = true,
            EmailSubjectTemplate = "⚙️ Page structure changed: {Watch.Name}",
            EmailBodyHtmlTemplate = """
                <h2>Schema Drift Detected</h2>
                <p><strong>Watch:</strong> {Watch.Name}</p>
                <p><strong>URL:</strong> <a href="{Watch.Url}">{Watch.Url}</a></p>
                <hr>
                <p>The page structure has changed. Selectors may need to be updated.</p>
                """,
            EmailBodyTextTemplate = "Page structure changed for {Watch.Name}. Selectors may need updating.",
            DiscordTitleTemplate = "⚙️ {Watch.Name}",
            DiscordBodyTemplate = "Page structure changed - selectors may need updating"
        }
    };

    /// <inheritdoc/>
    public async Task<string> RenderAsync(string template, NotificationContext context, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        var values = BuildPlaceholderValues(context);

        // Check if we need to generate LLM summary
        if (template.Contains("{LlmSummary}", StringComparison.OrdinalIgnoreCase) &&
            context.GenerateLlmSummary &&
            string.IsNullOrEmpty(context.LlmSummary))
        {
            var summary = await GenerateLlmSummaryAsync(context, ct);
            values["LlmSummary"] = summary;
        }
        else if (!string.IsNullOrEmpty(context.LlmSummary))
        {
            values["LlmSummary"] = context.LlmSummary;
        }

        // Replace all placeholders
        var result = PlaceholderRegex.Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (values.TryGetValue(key, out var value))
            {
                return value ?? string.Empty;
            }

            // Unknown placeholder - leave as literal (graceful fallback)
            logger.LogDebug("Unknown placeholder in template: {{{Placeholder}}}", key);
            return match.Value;
        });

        return result;
    }

    /// <inheritdoc/>
    public TemplateValidationResult ValidatePlaceholders(string template)
    {
        var result = new TemplateValidationResult();

        if (string.IsNullOrEmpty(template))
            return result;

        var matches = PlaceholderRegex.Matches(template);
        foreach (Match match in matches)
        {
            var placeholder = match.Groups[1].Value;
            if (!AvailablePlaceholdersDict.ContainsKey(placeholder))
            {
                result.UnknownPlaceholders.Add(placeholder);
                result.Warnings.Add($"Unknown placeholder '{{{placeholder}}}' will be rendered as literal text.");
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<NotificationTemplate> GetEffectiveTemplateAsync(
        NotificationTemplateType type,
        Guid? customTemplateId = null,
        CancellationToken ct = default)
    {
        // If a custom template is specified, try to load it
        if (customTemplateId.HasValue)
        {
            var customTemplate = await templateRepository.GetByIdAsync(customTemplateId.Value, ct);
            if (customTemplate != null)
                return customTemplate;

            logger.LogWarning("Custom template {TemplateId} not found, falling back to default", customTemplateId);
        }

        // Try to find a user-defined template for this type
        var templates = await templateRepository.GetAllAsync(ct);
        var userTemplate = templates.FirstOrDefault(t => t.Type == type && !t.IsBuiltIn);
        if (userTemplate != null)
            return userTemplate;

        // Return the default template
        return DefaultTemplates.TryGetValue(type, out var defaultTemplate)
            ? defaultTemplate
            : DefaultTemplates[NotificationTemplateType.ContentChange];
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> GetAvailablePlaceholders() => AvailablePlaceholdersDict;

    private Dictionary<string, string?> BuildPlaceholderValues(NotificationContext context)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            // Watch properties
            ["Watch.Name"] = context.Watch.Name ?? context.Watch.Url,
            ["Watch.Url"] = context.Watch.Url,

            // Price-specific
            ["Price"] = context.NewPrice?.ToString("N2"),
            ["OldPrice"] = context.OldPrice?.ToString("N2"),
            ["Currency"] = context.Currency,

            // Stock-specific
            ["StockStatus"] = FormatStockStatus(context.NewStockStatus),
            ["OldStockStatus"] = FormatStockStatus(context.OldStockStatus),

            // Generic value changes
            ["OldValue"] = context.OldValue?.ToString(),
            ["NewValue"] = context.NewValue?.ToString(),
            ["ChangePercent"] = context.ChangePercent?.ToString("N1"),
            ["ChangeAbsolute"] = context.ChangeAbsolute?.ToString("N2"),
            ["ChangeDirection"] = context.ChangeDirection ?? DetermineChangeDirection(context),

            // Field/threshold info
            ["FieldName"] = context.Field?.Name,
            ["AlertCondition"] = FormatAlertCondition(context.TriggeredThreshold),
            ["ThresholdValue"] = context.TriggeredThreshold?.Value.ToString("N2")
        };

        // Change event properties
        if (context.Change != null)
        {
            values["Change.DetectedAt"] = context.Change.DetectedAt.ToString("g");
            values["Change.Importance"] = context.Change.Importance.ToString();
            values["Change.LinesAdded"] = context.Change.LinesAdded.ToString();
            values["Change.LinesRemoved"] = context.Change.LinesRemoved.ToString();
        }

        // Custom values
        foreach (var (key, value) in context.CustomValues)
        {
            values[key] = value?.ToString();
        }

        return values;
    }

    private static string? FormatStockStatus(StockStatus? status) => status switch
    {
        StockStatus.InStock => "In Stock",
        StockStatus.OutOfStock => "Out of Stock",
        StockStatus.LimitedStock => "Limited Stock",
        StockStatus.PreOrder => "Pre-Order",
        StockStatus.Discontinued => "Discontinued",
        StockStatus.Backorder => "Backorder",
        StockStatus.Unknown => "Unknown",
        null => null,
        _ => status.ToString()
    };

    private static string? FormatAlertCondition(FieldAlertThreshold? threshold)
    {
        if (threshold == null) return null;

        return threshold.ConditionType switch
        {
            AlertConditionType.DropsBelow => $"dropped below {threshold.Value:N2}",
            AlertConditionType.RisesAbove => $"rose above {threshold.Value:N2}",
            AlertConditionType.ChangesBy => $"changed by more than {threshold.Value:N2}",
            AlertConditionType.ChangesByPercent => $"changed by more than {threshold.Value:N1}%",
            AlertConditionType.DropsByPercent => $"dropped by more than {threshold.Value:N1}%",
            AlertConditionType.RisesByPercent => $"rose by more than {threshold.Value:N1}%",
            AlertConditionType.EntersRange => $"entered range {threshold.Value:N2} - {threshold.SecondaryValue:N2}",
            AlertConditionType.ExitsRange => $"exited range {threshold.Value:N2} - {threshold.SecondaryValue:N2}",
            AlertConditionType.NewMinimum => "reached a new minimum",
            AlertConditionType.NewMaximum => "reached a new maximum",
            AlertConditionType.ReturnsToBaseline => $"returned to within {threshold.Value:N1}% of baseline",
            AlertConditionType.TargetReached => $"reached target of {threshold.Value:N2}",
            _ => threshold.ConditionType.ToString()
        };
    }

    private static string DetermineChangeDirection(NotificationContext context)
    {
        if (context.NewPrice.HasValue && context.OldPrice.HasValue)
        {
            if (context.NewPrice > context.OldPrice) return "increased";
            if (context.NewPrice < context.OldPrice) return "decreased";
            return "unchanged";
        }

        if (context.ChangeAbsolute.HasValue)
        {
            if (context.ChangeAbsolute > 0) return "increased";
            if (context.ChangeAbsolute < 0) return "decreased";
            return "unchanged";
        }

        return "changed";
    }

    private async Task<string> GenerateLlmSummaryAsync(NotificationContext context, CancellationToken ct)
    {
        try
        {
            var prompt = BuildSummaryPrompt(context);
            var response = await llmProvider.ExecuteAsync(prompt, ct: ct);
            return response.Content ?? "Unable to generate summary.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to generate LLM summary for notification");
            return "Summary unavailable.";
        }
    }

    private static string BuildSummaryPrompt(NotificationContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Summarize the following change notification in 1-2 sentences:");
        sb.AppendLine($"Watch: {context.Watch.Name ?? context.Watch.Url}");

        if (context.NewPrice.HasValue)
            sb.AppendLine($"Price changed from {context.OldPrice} to {context.NewPrice} {context.Currency}");

        if (context.NewStockStatus.HasValue)
            sb.AppendLine($"Stock status: {context.OldStockStatus} → {context.NewStockStatus}");

        if (context.Change != null)
            sb.AppendLine($"Content change: +{context.Change.LinesAdded}/-{context.Change.LinesRemoved} lines");

        return sb.ToString();
    }

    [GeneratedRegex(@"\{([^}]+)\}", RegexOptions.Compiled)]
    private static partial Regex GeneratePlaceholderRegex();
}
