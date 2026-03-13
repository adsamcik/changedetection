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
        var summary = $"{levelEmoji} {item.DisplayName} at {item.DisplaySecondary}";

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

        var label = policyResult.AlertLevel switch
        {
            AlertLevel.High => "NEW MATCH",
            AlertLevel.Medium => "WORTH REVIEWING",
            AlertLevel.Info => "STATUS UPDATE",
            _ => "LOGGED"
        };

        sb.AppendLine($"{levelEmoji} {label}: {item.DisplayName} at {item.DisplaySecondary}");
        sb.AppendLine($"Location: {item.DisplayContext ?? "Not specified"}");

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

        var label = policyResult.AlertLevel switch
        {
            AlertLevel.High => "NEW MATCH",
            AlertLevel.Medium => "WORTH REVIEWING",
            AlertLevel.Info => "STATUS UPDATE",
            _ => "LOGGED"
        };

        sb.AppendLine($"<h2>{levelEmoji} {label}: {Escape(item.DisplayName)} at {Escape(item.DisplaySecondary)}</h2>");
        sb.AppendLine("<table style='border-collapse:collapse;'>");
        sb.AppendLine($"<tr><td><strong>Location:</strong></td><td>{Escape(item.DisplayContext ?? "Not specified")}</td></tr>");

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

    private static string FormatDimensionName(string name) =>
        char.ToUpper(name[0]) + name[1..].Replace('_', ' ');

    private static string Escape(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? "");
}
