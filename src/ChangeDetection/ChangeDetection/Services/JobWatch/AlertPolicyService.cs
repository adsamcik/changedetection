using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.JobWatch;

/// <summary>
/// Maps LLM dimension scores to alert levels.
/// Logic: All PASS → HIGH, any STRETCH → MEDIUM, any hard FAIL → SILENT.
/// Hard-fail dimensions are configurable via TrackingConfig per domain.
/// </summary>
public class AlertPolicyService(ILogger<AlertPolicyService> logger) : IAlertPolicyService
{
    // Education is intentionally NOT a hard-fail dimension.
    // "PhD or equivalent" and "advanced degree" are common in European biotech
    // and should produce MEDIUM (STRETCH), not SILENT. Only dealbreakers and
    // location are hard gates — the user should see everything else for review.
    private static readonly HashSet<string> DefaultHardFailDimensions =
        ["dealbreakers", "location"];

    public AlertPolicyResult Evaluate(
        string? dimensionsJson,
        string? recommendation,
        DateTime? deadline = null,
        TrackingConfig? config = null)
    {
        var hardFailDims = config?.HardFailDimensions is { Count: > 0 }
            ? new HashSet<string>(config.HardFailDimensions, StringComparer.OrdinalIgnoreCase)
            : DefaultHardFailDimensions;

        if (string.IsNullOrWhiteSpace(dimensionsJson))
        {
            return new AlertPolicyResult
            {
                AlertLevel = recommendation?.Equals("SKIP", StringComparison.OrdinalIgnoreCase) == true
                    ? AlertLevel.Silent
                    : AlertLevel.Medium,
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
            return new AlertPolicyResult
            {
                AlertLevel = AlertLevel.Medium,
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
                    if (hardFailDims.Contains(name))
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
        AlertLevel baseLevel;
        string reason;

        if (hasHardFail)
        {
            baseLevel = AlertLevel.Silent;
            reason = $"Hard disqualifier: {string.Join("; ", failReasons)}";
        }
        else if (hasStretch)
        {
            baseLevel = AlertLevel.Medium;
            reason = $"Partial match: {string.Join("; ", stretchReasons)}";
        }
        else if (hasUnknown && dimensions.Count(d =>
            d.Value.Status?.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true) < 3)
        {
            // Too many unknowns with few passes — be cautious, show as MEDIUM
            baseLevel = AlertLevel.Medium;
            reason = "Insufficient information for confident match — review recommended";
        }
        else
        {
            baseLevel = AlertLevel.High;
            reason = "All checks pass — strong profile match";
        }

        // Override with LLM recommendation if it contradicts
        if (recommendation is not null)
        {
            if (recommendation.Equals("SKIP", StringComparison.OrdinalIgnoreCase) && baseLevel == AlertLevel.High)
            {
                baseLevel = AlertLevel.Medium;
                reason = $"LLM recommends SKIP despite passing checks: {reason}";
            }
            else if (recommendation.Equals("APPLY", StringComparison.OrdinalIgnoreCase) && baseLevel == AlertLevel.Medium)
            {
                // Don't escalate MEDIUM→HIGH based on recommendation alone, but note it
                reason = $"[LLM: APPLY] {reason}";
            }
        }

        // Apply deadline urgency
        var urgencyApplied = false;
        AlertLevel? preUrgencyLevel = null;
        int? daysUntilDeadline = null;

        if (deadline.HasValue)
        {
            var daysLeft = (deadline.Value.Date - DateTime.UtcNow.Date).Days;
            daysUntilDeadline = daysLeft;

            if (daysLeft < 0)
            {
                // Deadline already passed — don't escalate, this will be expired
                reason = $"{reason} | ⏰ Deadline passed";
            }
            else if (daysLeft == 0 && baseLevel != AlertLevel.Silent)
            {
                // Deadline is TODAY — maximum urgency
                preUrgencyLevel = baseLevel;
                baseLevel = AlertLevel.High;
                urgencyApplied = true;
                reason = $"🚨 DEADLINE TODAY | {reason}";
            }
            else if (daysLeft <= 3 && baseLevel != AlertLevel.Silent)
            {
                preUrgencyLevel = baseLevel;
                baseLevel = AlertLevel.High;
                urgencyApplied = true;
                reason = $"🚨 URGENT — {daysLeft}d left | {reason}";
            }
            else if (daysLeft <= 7 && baseLevel == AlertLevel.Medium)
            {
                preUrgencyLevel = baseLevel;
                baseLevel = AlertLevel.High;
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

        return new AlertPolicyResult
        {
            AlertLevel = baseLevel,
            Reason = reason,
            Dimensions = dimensions,
            UrgencyApplied = urgencyApplied,
            PreUrgencyLevel = preUrgencyLevel,
            DaysUntilDeadline = daysUntilDeadline
        };
    }

    public AlertPolicyResult EvaluateRemoval(TrackedItem item)
    {
        return new AlertPolicyResult
        {
            AlertLevel = AlertLevel.Info,
            Reason = $"Listing removed: {item.DisplayName} at {item.DisplaySecondary}"
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
