using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Core.Entities;

/// <summary>
/// Payload for change notifications stored in the outbox.
/// </summary>
public record ChangeNotificationPayload(
    Guid WatchId,
    string WatchName,
    string WatchUrl,
    Guid ChangeEventId,
    DateTime DetectedAt,
    string Importance,
    int LinesAdded,
    int LinesRemoved,
    string? Summary,
    string? DiffSummary)
{
    public static ChangeNotificationPayload FromEntities(WatchedSite watch, ChangeEvent change, string? summary)
    {
        return new ChangeNotificationPayload(
            watch.Id,
            watch.Name ?? watch.Url,
            watch.Url,
            change.Id,
            change.DetectedAt,
            change.Importance.ToString(),
            change.LinesAdded,
            change.LinesRemoved,
            summary,
            change.DiffSummary);
    }

    public string ToJson() => JsonSerializer.Serialize(this);
    
    public static ChangeNotificationPayload? FromJson(string json) => 
        JsonSerializer.Deserialize<ChangeNotificationPayload>(json);
}

/// <summary>
/// Payload for alert notifications stored in the outbox.
/// </summary>
public record AlertNotificationPayload(
    Guid WatchId,
    string WatchName,
    string WatchUrl,
    string CombinedMessage,
    string HighestImportance,
    List<TriggeredThresholdPayload> TriggeredThresholds)
{
    public static AlertNotificationPayload FromEntities(WatchedSite watch, AlertEvaluationResult alertResult)
    {
        return new AlertNotificationPayload(
            watch.Id,
            watch.Name ?? watch.Url,
            watch.Url,
            alertResult.CombinedMessage ?? "Alert triggered",
            alertResult.HighestImportance?.ToString() ?? "Medium",
            alertResult.TriggeredThresholds.Select(t => new TriggeredThresholdPayload(
                t.Field.Name,
                t.Threshold.Name ?? t.Field.Name,
                t.Message,
                t.NewValue?.ToString() ?? "",
                (decimal?)t.CalculatedChange)).ToList());
    }

    public string ToJson() => JsonSerializer.Serialize(this);
    
    public static AlertNotificationPayload? FromJson(string json) => 
        JsonSerializer.Deserialize<AlertNotificationPayload>(json);
}

/// <summary>
/// Individual triggered threshold info in the payload.
/// </summary>
public record TriggeredThresholdPayload(
    string FieldName,
    string ThresholdName,
    string Message,
    string CurrentValue,
    decimal? ChangePercent);
