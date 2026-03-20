using System.Text.Json;
using System.Text.Json.Nodes;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Reusable utility for patching RelevanceScore block keywords in pipeline definitions.
/// Used by both GroupWatchDiscoveryService (at watch creation) and the profile endpoint (bulk update).
/// </summary>
public static class PipelineKeywordPatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Replaces the positiveKeywords and negativeKeywords in any RelevanceScore block
    /// of the given pipeline definition. Returns a new PipelineDefinition with the patched blocks.
    /// </summary>
    public static PipelineDefinition ApplyRelevanceKeywords(
        PipelineDefinition pipeline,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords)
    {
        if (positiveKeywords.Count == 0 && negativeKeywords.Count == 0)
            return pipeline;

        var blocks = pipeline.Blocks
            .Select(block =>
            {
                if (!string.Equals(block.Type, "RelevanceScore", StringComparison.OrdinalIgnoreCase))
                    return block;

                var config = block.Config is { } existing
                    ? JsonNode.Parse(existing.GetRawText()) as JsonObject ?? new JsonObject()
                    : new JsonObject();

                config["targetFields"] ??= JsonSerializer.SerializeToNode(
                    new[] { "title", "description", "location", "summary" });
                config["positiveKeywords"] = JsonSerializer.SerializeToNode(
                    positiveKeywords.Select(keyword => new { keyword = keyword.Keyword, weight = keyword.Weight }));
                config["negativeKeywords"] = JsonSerializer.SerializeToNode(
                    negativeKeywords.Select(keyword => new { keyword = keyword.Keyword, weight = keyword.Weight }));
                config["minScore"] ??= 0;

                return block with
                {
                    Config = JsonSerializer.SerializeToElement(config, JsonOptions)
                };
            })
            .ToList();

        return pipeline with { Blocks = blocks };
    }

    /// <summary>
    /// Patches a single watch's PipelineDefinitionJson string in-place with new relevance keywords.
    /// Returns the updated JSON string, or null if the watch has no pipeline definition.
    /// </summary>
    public static string? PatchPipelineJson(
        string? pipelineDefinitionJson,
        IReadOnlyList<RelevanceKeyword> positiveKeywords,
        IReadOnlyList<RelevanceKeyword> negativeKeywords)
    {
        if (string.IsNullOrWhiteSpace(pipelineDefinitionJson))
            return pipelineDefinitionJson;

        PipelineDefinition? pipeline;
        try
        {
            pipeline = JsonSerializer.Deserialize<PipelineDefinition>(pipelineDefinitionJson, JsonOptions);
        }
        catch
        {
            return pipelineDefinitionJson;
        }

        if (pipeline is null)
            return pipelineDefinitionJson;

        var patched = ApplyRelevanceKeywords(pipeline, positiveKeywords, negativeKeywords);
        return JsonSerializer.Serialize(patched, JsonOptions);
    }
}
