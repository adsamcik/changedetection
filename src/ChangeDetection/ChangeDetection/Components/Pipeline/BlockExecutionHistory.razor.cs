using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.AspNetCore.Components;

namespace ChangeDetection.Components.Pipeline;

public partial class BlockExecutionHistory
{
    [Inject] private IBlockStateStore StateStore { get; set; } = null!;

    [Parameter] public Guid WatchId { get; set; }
    [Parameter, EditorRequired] public string BlockInstanceId { get; set; } = null!;
    [Parameter, EditorRequired] public string BlockType { get; set; } = null!;

    private bool _loading = true;
    private List<HistoryEntry> _entries = [];

    private record HistoryEntry(DateTime Timestamp, string Status, long? DurationMs, string? OutputPreview);

    protected override async Task OnInitializedAsync()
    {
        try
        {
            var snapshots = await StateStore.GetHistoryAsync(
                WatchId.ToString(), BlockInstanceId, maxResults: 10);

            _entries = snapshots.Select(s => new HistoryEntry(
                s.Timestamp,
                GetSnapshotStatus(s),
                s.DurationMs,
                GetOutputPreview(s.Output)
            )).ToList();
        }
        catch
        {
            _entries = [];
        }

        _loading = false;
    }

    private static string GetSnapshotStatus(BlockExecutionSnapshot snapshot)
    {
        if (snapshot.Output.TryGetProperty("error", out _))
            return "Failed";
        if (snapshot.Output.TryGetProperty("skipped", out _))
            return "Skipped";
        return "Completed";
    }

    private static string? GetOutputPreview(JsonElement output)
    {
        var text = output.ToString();
        return text.Length > 100 ? text[..100] + "…" : text;
    }

    private static string GetEntryBadgeClass(HistoryEntry entry) => entry.Status switch
    {
        "Completed" => "bg-success",
        "Failed" => "bg-danger",
        "Skipped" => "bg-warning text-dark",
        _ => "bg-secondary"
    };
}
