using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.JobWatch;

/// <summary>
/// Maps LLM dimension scores to job alert levels.
/// Logic: All PASS → HIGH, any STRETCH → MEDIUM, any hard FAIL → SILENT.
/// Deadline urgency can escalate MEDIUM → HIGH or add URGENT flags.
/// </summary>
public class AlertPolicyService(ILogger<AlertPolicyService> logger) : IAlertPolicyService
{
    private static readonly HashSet<string> HardFailDimensions =
        ["education", "dealbreakers", "location"];

    public JobAlertPolicyResult Evaluate(string? dimensionsJson, string? recommendation, DateTime? deadline = null)
    {
        if (string.IsNullOrWhiteSpace(dimensionsJson))
        {
            return new JobAlertPolicyResult
            {
                AlertLevel = recommendation?.Equals("SKIP", StringComparison.OrdinalIgnoreCase) == true
                    ? JobAlertLevel.Silent
                    : JobAlertLevel.Medium,
                Reason = "No dimension data available — defaulting based on recommendation"
            };
        }

        Dictionary<string, DimensionStatus> dimensions;
        try
        {
            dimensions = ParseDimensions(dimensionsJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse dimensions JSON for alert policy evaluation");
            return new JobAlertPolicyResult
            {
                AlertLevel = JobAlertLevel.Medium,
                Reason = "Dimension parsing failed — defaulting to MEDIUM for safety"
            };
        }

        var hasHardFail = false;
        var hasStretch = false;
        var hasUnknown = false;
        var failReasons = new List<string>();
        var stretchReasons = new List<string>();

        foreach (var (name, dim) in dimensions)
        {
            switch (dim.Status?.ToUpperInvariant())
            {
                case "FAIL":
                    if (HardFailDimensions.Contains(name))
                    {
                        hasHardFail = true;
                        failReasons.Add($"{name}: {dim.Reason ?? "failed"}");
                    }
                    else
                    {
                        // Non-hard-fail dimensions (salary, experience) → treat as STRETCH
                        hasStretch = true;
                        stretchReasons.Add($"{name}: {dim.Reason ?? "does not meet"}");
                    }
                    break;

                case "STRETCH":
                    hasStretch = true;
                    stretchReasons.Add($"{name}: {dim.Reason ?? "partial match"}");
                    break;

                case "UNKNOWN":
                    hasUnknown = true;
                    break;
            }
        }

        // Determine base alert level
        JobAlertLevel baseLevel;
        string reason;

        if (hasHardFail)
        {
            baseLevel = JobAlertLevel.Silent;
            reason = $"Hard disqualifier: {string.Join("; ", failReasons)}";
        }
        else if (hasStretch)
        {
            baseLevel = JobAlertLevel.Medium;
            reason = $"Partial match: {string.Join("; ", stretchReasons)}";
        }
        else if (hasUnknown && dimensions.Count(d =>
            d.Value.Status?.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true) < 3)
        {
            // Too many unknowns with few passes — be cautious, show as MEDIUM
            baseLevel = JobAlertLevel.Medium;
            reason = "Insufficient information for confident match — review recommended";
        }
        else
        {
            baseLevel = JobAlertLevel.High;
            reason = "All checks pass — strong profile match";
        }

        // Override with LLM recommendation if it contradicts
        if (recommendation is not null)
        {
            if (recommendation.Equals("SKIP", StringComparison.OrdinalIgnoreCase) && baseLevel == JobAlertLevel.High)
            {
                baseLevel = JobAlertLevel.Medium;
                reason = $"LLM recommends SKIP despite passing checks: {reason}";
            }
            else if (recommendation.Equals("APPLY", StringComparison.OrdinalIgnoreCase) && baseLevel == JobAlertLevel.Medium)
            {
                // Don't escalate MEDIUM→HIGH based on recommendation alone, but note it
                reason = $"[LLM: APPLY] {reason}";
            }
        }

        // Apply deadline urgency
        var urgencyApplied = false;
        JobAlertLevel? preUrgencyLevel = null;
        int? daysUntilDeadline = null;

        if (deadline.HasValue)
        {
            var daysLeft = (deadline.Value.Date - DateTime.UtcNow.Date).Days;
            daysUntilDeadline = daysLeft;

            if (daysLeft <= 0)
            {
                // Deadline passed — don't escalate, this will be expired
                reason = $"{reason} | ⏰ Deadline passed";
            }
            else if (daysLeft <= 3 && baseLevel != JobAlertLevel.Silent)
            {
                preUrgencyLevel = baseLevel;
                baseLevel = JobAlertLevel.High;
                urgencyApplied = true;
                reason = $"🚨 URGENT — {daysLeft}d left | {reason}";
            }
            else if (daysLeft <= 7 && baseLevel == JobAlertLevel.Medium)
            {
                preUrgencyLevel = baseLevel;
                baseLevel = JobAlertLevel.High;
                urgencyApplied = true;
                reason = $"⏰ {daysLeft}d until deadline — escalated | {reason}";
            }
            else if (daysLeft <= 14)
            {
                reason = $"⏰ {daysLeft}d remaining | {reason}";
            }
        }

        logger.LogDebug(
            "Alert policy evaluated: {AlertLevel} (urgency={Urgency}, dims={DimCount})",
            baseLevel, urgencyApplied, dimensions.Count);

        return new JobAlertPolicyResult
        {
            AlertLevel = baseLevel,
            Reason = reason,
            Dimensions = dimensions,
            UrgencyApplied = urgencyApplied,
            PreUrgencyLevel = preUrgencyLevel,
            DaysUntilDeadline = daysUntilDeadline
        };
    }

    public JobAlertPolicyResult EvaluateRemoval(TrackedListing listing)
    {
        return new JobAlertPolicyResult
        {
            AlertLevel = JobAlertLevel.Info,
            Reason = $"Listing removed: {listing.Title} at {listing.Company}"
        };
    }

    private static Dictionary<string, DimensionStatus> ParseDimensions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, DimensionStatus>();

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var status = "UNKNOWN";
            float score = 0;
            string? reason = null;

            if (prop.Value.TryGetProperty("status", out var statusEl))
                status = statusEl.GetString() ?? "UNKNOWN";
            if (prop.Value.TryGetProperty("score", out var scoreEl))
                score = scoreEl.GetSingle();
            if (prop.Value.TryGetProperty("reason", out var reasonEl))
                reason = reasonEl.GetString();

            result[prop.Name] = new DimensionStatus
            {
                Status = status,
                Score = score,
                Reason = reason
            };
        }

        return result;
    }
}
