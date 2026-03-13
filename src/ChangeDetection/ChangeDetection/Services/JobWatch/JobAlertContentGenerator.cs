using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.JobWatch;

/// <summary>
/// Generates formatted alert content with profile match breakdown for job notifications.
/// </summary>
public class JobAlertContentGenerator : IJobAlertContentGenerator
{
    private static readonly Dictionary<string, string> StatusEmojis = new()
    {
        ["PASS"] = "✅",
        ["FAIL"] = "❌",
        ["STRETCH"] = "⚠️",
        ["UNKNOWN"] = "❓"
    };

    private static readonly Dictionary<JobAlertLevel, string> LevelEmojis = new()
    {
        [JobAlertLevel.High] = "🔴",
        [JobAlertLevel.Medium] = "🟡",
        [JobAlertLevel.Silent] = "⚪",
        [JobAlertLevel.Info] = "ℹ️"
    };

    public JobAlertContent Generate(TrackedListing listing, JobAlertPolicyResult policyResult)
    {
        var levelEmoji = LevelEmojis.GetValueOrDefault(policyResult.AlertLevel, "⚪");
        var summary = $"{levelEmoji} {listing.Title} at {listing.Company}";

        var plainText = GeneratePlainText(listing, policyResult, levelEmoji);
        var html = GenerateHtml(listing, policyResult, levelEmoji);

        return new JobAlertContent
        {
            PlainText = plainText,
            Html = html,
            Summary = summary,
            LevelEmoji = levelEmoji
        };
    }

    private static string GeneratePlainText(TrackedListing listing, JobAlertPolicyResult policyResult, string levelEmoji)
    {
        var sb = new StringBuilder();

        var label = policyResult.AlertLevel switch
        {
            JobAlertLevel.High => "NEW MATCH",
            JobAlertLevel.Medium => "WORTH REVIEWING",
            JobAlertLevel.Info => "STATUS UPDATE",
            _ => "LOGGED"
        };

        sb.AppendLine($"{levelEmoji} {label}: {listing.Title} at {listing.Company}");
        sb.AppendLine($"Location: {listing.Location ?? "Not specified"}");

        if (listing.Deadline.HasValue)
            sb.AppendLine($"Deadline: {listing.Deadline.Value:yyyy-MM-dd}");

        if (!string.IsNullOrWhiteSpace(listing.Url))
            sb.AppendLine($"Link: {listing.Url}");

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
        sb.AppendLine($"Recommendation: {listing.Recommendation ?? policyResult.Reason}");

        return sb.ToString();
    }

    private static string GenerateHtml(TrackedListing listing, JobAlertPolicyResult policyResult, string levelEmoji)
    {
        var sb = new StringBuilder();

        var label = policyResult.AlertLevel switch
        {
            JobAlertLevel.High => "NEW MATCH",
            JobAlertLevel.Medium => "WORTH REVIEWING",
            JobAlertLevel.Info => "STATUS UPDATE",
            _ => "LOGGED"
        };

        sb.AppendLine($"<h2>{levelEmoji} {label}: {Escape(listing.Title)} at {Escape(listing.Company)}</h2>");
        sb.AppendLine("<table style='border-collapse:collapse;'>");
        sb.AppendLine($"<tr><td><strong>Location:</strong></td><td>{Escape(listing.Location ?? "Not specified")}</td></tr>");

        if (listing.Deadline.HasValue)
            sb.AppendLine($"<tr><td><strong>Deadline:</strong></td><td>{listing.Deadline.Value:yyyy-MM-dd}</td></tr>");

        if (!string.IsNullOrWhiteSpace(listing.Url))
            sb.AppendLine($"<tr><td><strong>Link:</strong></td><td><a href='{Escape(listing.Url)}'>{Escape(listing.Url)}</a></td></tr>");

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

        sb.AppendLine($"<p><strong>Recommendation:</strong> {Escape(listing.Recommendation ?? policyResult.Reason)}</p>");

        return sb.ToString();
    }

    private static string FormatDimensionName(string name) =>
        char.ToUpper(name[0]) + name[1..].Replace('_', ' ');

    private static string Escape(string? value) =>
        System.Net.WebUtility.HtmlEncode(value ?? "");
}
