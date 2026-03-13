using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.JobWatch;

/// <summary>
/// Generates formatted alert content with profile match breakdown for notifications.
/// </summary>
public class AlertContentGenerator : IAlertContentGenerator
{
    private static readonly Dictionary<string, string> StatusEmojis = new()
    {
        ["PASS"] = "✅",
        ["FAIL"] = "❌",
        ["STRETCH"] = "⚠️",
        ["UNKNOWN"] = "❓"
    };

    private static readonly Dictionary<AlertLevel, string> LevelEmojis = new()
    {
        [AlertLevel.High] = "🔴",
        [AlertLevel.Medium] = "🟡",
        [AlertLevel.Silent] = "⚪",
        [AlertLevel.Info] = "ℹ️"
    };

    public AlertContent Generate(TrackedItem item, AlertPolicyResult policyResult)
    {
        var levelEmoji = LevelEmojis.GetValueOrDefault(policyResult.AlertLevel, "⚪");
        var secondary = item.DisplaySecondary is not null ? $" — {item.DisplaySecondary}" : "";
        var summary = $"{levelEmoji} {item.DisplayName}{secondary}";

        var plainText = GeneratePlainText(item, policyResult, levelEmoji);
        var html = GenerateHtml(item, policyResult, levelEmoji);

        return new AlertContent
        {
            PlainText = plainText,
            Html = html,
            Summary = summary,
            LevelEmoji = levelEmoji
        };
    }

    private static string GeneratePlainText(TrackedItem item, AlertPolicyResult policyResult, string levelEmoji)
    {
        var sb = new StringBuilder();

        var label = GetAlertLabel(policyResult.AlertLevel, item.ItemType);
        var secondary = item.DisplaySecondary is not null ? $" — {item.DisplaySecondary}" : "";

        sb.AppendLine($"{levelEmoji} {label}: {item.DisplayName}{secondary}");

        if (item.DisplayContext is not null)
            sb.AppendLine($"{GetContextLabel(item.ItemType)}: {item.DisplayContext}");

        if (item.Deadline.HasValue)
            sb.AppendLine($"Deadline: {item.Deadline.Value:yyyy-MM-dd}");

        if (!string.IsNullOrWhiteSpace(item.Url))
            sb.AppendLine($"Link: {item.Url}");

        sb.AppendLine();
        sb.AppendLine("Profile match:");

        foreach (var (name, dim) in policyResult.Dimensions)
        {
            var emoji = StatusEmojis.GetValueOrDefault(dim.Status, "❓");
            sb.AppendLine($"  {emoji} {FormatDimensionName(name)}: {dim.Reason ?? dim.Status}");
        }

        if (policyResult.DaysUntilDeadline.HasValue && policyResult.DaysUntilDeadline > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"⏰ {policyResult.DaysUntilDeadline} days until deadline");
        }

        sb.AppendLine();
        sb.AppendLine($"Recommendation: {item.Recommendation ?? policyResult.Reason}");

        return sb.ToString();
    }

    private static string GenerateHtml(TrackedItem item, AlertPolicyResult policyResult, string levelEmoji)
    {
        var sb = new StringBuilder();

        var label = GetAlertLabel(policyResult.AlertLevel, item.ItemType);
        var secondary = item.DisplaySecondary is not null ? $" — {Escape(item.DisplaySecondary)}" : "";

        sb.AppendLine($"<h2>{levelEmoji} {label}: {Escape(item.DisplayName)}{secondary}</h2>");
        sb.AppendLine("<table style='border-collapse:collapse;'>");

        if (item.DisplayContext is not null)
            sb.AppendLine($"<tr><td><strong>{Escape(GetContextLabel(item.ItemType))}:</strong></td><td>{Escape(item.DisplayContext)}</td></tr>");

        if (item.Deadline.HasValue)
            sb.AppendLine($"<tr><td><strong>Deadline:</strong></td><td>{item.Deadline.Value:yyyy-MM-dd}</td></tr>");

        if (!string.IsNullOrWhiteSpace(item.Url))
            sb.AppendLine($"<tr><td><strong>Link:</strong></td><td><a href='{Escape(item.Url)}'>{Escape(item.Url)}</a></td></tr>");

        sb.AppendLine("</table>");
        sb.AppendLine("<h3>Profile Match</h3>");
        sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;'>");

        foreach (var (name, dim) in policyResult.Dimensions)
        {
            var emoji = StatusEmojis.GetValueOrDefault(dim.Status, "❓");
            var color = dim.Status switch
            {
                "PASS" => "#28a745",
                "FAIL" => "#dc3545",
                "STRETCH" => "#ffc107",
                _ => "#6c757d"
            };

            sb.AppendLine($"<tr style='border-bottom:1px solid #eee;'>");
            sb.AppendLine($"  <td style='padding:4px 8px;'>{emoji}</td>");
            sb.AppendLine($"  <td style='padding:4px 8px;font-weight:bold;'>{Escape(FormatDimensionName(name))}</td>");
            sb.AppendLine($"  <td style='padding:4px 8px;color:{color};'>{Escape(dim.Reason ?? dim.Status)}</td>");
            sb.AppendLine("</tr>");
        }

        sb.AppendLine("</table>");

        if (policyResult.DaysUntilDeadline is > 0)
            sb.AppendLine($"<p>⏰ <strong>{policyResult.DaysUntilDeadline} days until deadline</strong></p>");

        sb.AppendLine($"<p><strong>Recommendation:</strong> {Escape(item.Recommendation ?? policyResult.Reason)}</p>");

        return sb.ToString();
    }

    private static string GetAlertLabel(AlertLevel level, string? itemType) => (level, itemType) switch
    {
        (AlertLevel.High, "job-listing") => "NEW MATCH",
        (AlertLevel.High, "product") => "PRICE ALERT",
        (AlertLevel.High, "paper") => "NEW PAPER",
        (AlertLevel.High, _) => "NEW MATCH",
        (AlertLevel.Medium, _) => "WORTH REVIEWING",
        (AlertLevel.Info, _) => "STATUS UPDATE",
        _ => "LOGGED"
    };

    private static string GetContextLabel(string? itemType) => itemType switch
    {
        "job-listing" => "Location",
        "product" => "Seller",
        "paper" => "Authors",
        _ => "Details"
    };

    private static string FormatDimensionName(string name) =>
        char.ToUpper(name[0]) + name[1..].Replace('_', ' ');

    private static string Escape(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? "");
}
