using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Pipeline;
using Microsoft.AspNetCore.Components;

namespace ChangeDetection.Components.Pipeline;

public partial class WatchDashboardCard
{
    [Parameter, EditorRequired]
    public WatchedSite Watch { get; set; } = null!;

    [Parameter]
    public PipelineExecutionResult? LastResult { get; set; }

    private enum CardType { Price, List, Content, MultiSignal, Default }

    private CardType GetCardType()
    {
        if (Watch.PipelineDefinitionJson is null)
            return CardType.Default;

        try
        {
            var pipeline = JsonSerializer.Deserialize<PipelineDefinition>(Watch.PipelineDefinitionJson);
            if (pipeline is null) return CardType.Default;

            if (pipeline.Metadata?.CardType is not null)
            {
                return pipeline.Metadata.CardType.ToLowerInvariant() switch
                {
                    "price" => CardType.Price,
                    "list" => CardType.List,
                    "content" => CardType.Content,
                    "multisignal" => CardType.MultiSignal,
                    _ => CardType.Default
                };
            }

            var blockTypes = pipeline.Blocks.Select(b => b.Type.ToLowerInvariant()).ToHashSet();

            if (blockTypes.Contains("numericdelta"))
                return CardType.Price;
            if (blockTypes.Contains("listdiff"))
                return CardType.List;
            if (blockTypes.Contains("textdiff") || blockTypes.Contains("hashcompare"))
                return CardType.Content;
            if (blockTypes.Contains("route"))
                return CardType.MultiSignal;
        }
                catch (Exception ex)
        {
            // Malformed pipeline JSON
            Console.WriteLine($"[WatchDashboardCard.razor] Error in GetCardType: {ex.Message}");
        }

        return CardType.Default;
    }

    private string GetPrimaryValue()
    {
        if (LastResult?.OutputData is { } output &&
            output.TryGetProperty("currentValue", out var val))
        {
            return val.ToString();
        }

        return "—";
    }

    private string GetDeltaText()
    {
        if (LastResult?.OutputData is { } output &&
            output.TryGetProperty("delta", out var delta))
        {
            var text = delta.ToString();
            return text.StartsWith('-') ? text : $"+{text}";
        }

        return "no change";
    }

    private string GetDeltaBadgeClass()
    {
        if (LastResult?.OutputData is { } output &&
            output.TryGetProperty("delta", out var delta) &&
            delta.TryGetDouble(out var d))
        {
            if (d < 0) return "bg-success";
            if (d > 0) return "bg-danger";
        }

        return "bg-secondary";
    }

    private string GetListSummary()
    {
        if (LastResult?.OutputData is { } output &&
            output.TryGetProperty("added", out var added) &&
            output.TryGetProperty("removed", out var removed))
        {
            var addedCount = added.GetArrayLength();
            var removedCount = removed.GetArrayLength();
            return $"+{addedCount} added, -{removedCount} removed";
        }

        return "No changes detected";
    }

    private string GetContentSummary()
    {
        if (LastResult is null)
            return "Awaiting first check";

        if (!LastResult.Success)
            return "Last check failed";

        if (LastResult.WasBaseline)
            return "Baseline captured";

        if (LastResult.OutputData is { } output &&
            output.TryGetProperty("changed", out var changed) &&
            changed.GetBoolean())
        {
            return "Content changed";
        }

        return "No changes";
    }

    private string GetLastCheckStatus()
    {
        if (LastResult is null)
            return "Pending";

        return LastResult.Success ? "OK" : "Error";
    }

    private string FormatLastChecked()
    {
        if (Watch.LastChecked is null)
            return "Never";

        var elapsed = DateTime.UtcNow - Watch.LastChecked.Value;
        var totalMinutes = elapsed.TotalMinutes;

        if (totalMinutes < 1) return "just now";
        if (totalMinutes < 60) return $"{(int)totalMinutes}m ago";
        if (totalMinutes < 1440) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
}
