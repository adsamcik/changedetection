using ChangeDetection.Core.Pipeline;
using Microsoft.AspNetCore.Components;

namespace ChangeDetection.Components.Pipeline;

public partial class PipelineFlowDiagram
{
    [Parameter] public PipelineDefinition? Pipeline { get; set; }
    [Parameter] public Dictionary<string, BlockExecutionStatus>? BlockStatuses { get; set; }
    [Parameter] public EventCallback<string> OnBlockClicked { get; set; }

    private IReadOnlyList<BlockDefinition> GetOrderedBlocks()
    {
        if (Pipeline is null) return [];

        var blocks = Pipeline.Blocks.ToList();
        var connections = Pipeline.Connections;

        // Kahn's algorithm for topological sort
        var inDegree = blocks.ToDictionary(b => b.Id, _ => 0);
        var adjacency = blocks.ToDictionary(b => b.Id, _ => new List<string>());

        foreach (var conn in connections)
        {
            if (inDegree.ContainsKey(conn.ToBlockId))
                inDegree[conn.ToBlockId]++;
            if (adjacency.ContainsKey(conn.FromBlockId))
                adjacency[conn.FromBlockId].Add(conn.ToBlockId);
        }

        var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<BlockDefinition>();
        var blockMap = blocks.ToDictionary(b => b.Id);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (blockMap.TryGetValue(id, out var block))
                sorted.Add(block);

            foreach (var neighbor in adjacency.GetValueOrDefault(id, []))
            {
                inDegree[neighbor]--;
                if (inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
            }
        }

        // Append any blocks not reached (disconnected)
        foreach (var block in blocks)
        {
            if (!sorted.Contains(block))
                sorted.Add(block);
        }

        return sorted;
    }

    private static string GetBlockIcon(string type) => type.ToLowerInvariant() switch
    {
        "navigate" => "🌐",
        "filter" or "cssfilter" or "xpathfilter" => "🔍",
        "extractschema" or "extract" => "📊",
        "textdiff" or "hashcompare" or "numericdelta" or "listdiff" => "🔄",
        "condition" or "route" => "⚡",
        "notify" or "notification" => "📢",
        "llm" or "llmextract" or "llmanalysis" => "🤖",
        "input" => "📦",
        "output" => "📤",
        _ => "⬜"
    };

    private string GetStatusClass(string blockId)
    {
        if (BlockStatuses is null || !BlockStatuses.TryGetValue(blockId, out var status))
            return string.Empty;

        return status switch
        {
            BlockExecutionStatus.Completed => "status-completed",
            BlockExecutionStatus.Failed => "status-failed",
            BlockExecutionStatus.Skipped => "status-skipped",
            _ => string.Empty
        };
    }

    private MarkupString GetStatusBadge(string blockId)
    {
        if (BlockStatuses is null || !BlockStatuses.TryGetValue(blockId, out var status))
            return new MarkupString("<span class='badge bg-secondary'>⬜ Pending</span>");

        var (icon, css) = status switch
        {
            BlockExecutionStatus.Completed => ("✅", "bg-success"),
            BlockExecutionStatus.Failed => ("❌", "bg-danger"),
            BlockExecutionStatus.Skipped => ("⚠️", "bg-warning text-dark"),
            BlockExecutionStatus.Baseline => ("⏳", "bg-info"),
            _ => ("⬜", "bg-secondary")
        };

        return new MarkupString($"<span class='badge {css}'>{icon} {status}</span>");
    }
}
