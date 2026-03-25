using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.BlockExecution;

/// <summary>
/// Utility to build a <see cref="PipelineRunSummaryEntity"/> from a pipeline execution result
/// and its definition.
/// </summary>
public static class PipelineRunSummaryBuilder
{
    /// <summary>
    /// Build a persistable summary from the execution result and pipeline definition.
    /// </summary>
    public static PipelineRunSummaryEntity Build(
        string watchId,
        PipelineExecutionResult result,
        PipelineDefinition definition)
    {
        var blockTypeMap = definition.Blocks.ToDictionary(b => b.Id, b => b.Type, StringComparer.OrdinalIgnoreCase);

        var blockSummaries = result.BlockResults.Select(kvp =>
        {
            var outputSize = kvp.Value.Output.HasValue
                ? kvp.Value.Output.Value.GetRawText().Length
                : (int?)null;

            blockTypeMap.TryGetValue(kvp.Key, out var blockType);

            return new PipelineBlockSummary
            {
                BlockId = kvp.Key,
                BlockType = blockType ?? "Unknown",
                Status = kvp.Value.Status.ToString(),
                DurationMs = null, // Per-block timing not tracked in BlockResult
                OutputSizeChars = outputSize,
                Error = kvp.Value.Error,
                CacheHit = kvp.Value.CacheHit
            };
        }).ToList();

        return new PipelineRunSummaryEntity
        {
            WatchId = watchId,
            Timestamp = DateTime.UtcNow,
            Success = result.Success,
            IsDegraded = result.IsDegraded,
            ExecutionDurationMs = result.ExecutionDurationMs,
            Error = result.Error,
            BlockSummariesJson = JsonSerializer.Serialize(blockSummaries)
        };
    }
}
