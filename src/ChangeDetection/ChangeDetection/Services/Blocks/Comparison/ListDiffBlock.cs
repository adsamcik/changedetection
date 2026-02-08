using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Blocks.Comparison;

/// <summary>
/// Compares lists of objects across runs using an identity key, detecting additions, removals, and modifications.
/// </summary>
public class ListDiffBlock : IPipelineBlock
{
    public string BlockType => "ListDiff";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "result", Type = PortType.DiffResult }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return BlockResult.Failed("ListDiff block requires a 'data' input.");

        if (dataElement.ValueKind != JsonValueKind.Array)
            return BlockResult.Failed("ListDiff block expects a JSON array input.");

        var ct = context.CancellationToken;
        var (identityKey, mode) = ReadConfig(context);

        var previous = await context.StateStore.GetPreviousOutputAsync(
            context.WatchId.ToString(), context.BlockInstanceId, ct);

        if (previous is null)
        {
            var baseline = JsonSerializer.SerializeToElement(new
            {
                items = dataElement,
                changed = false
            });
            return BlockResult.BaselineCapture(baseline);
        }

        var previousItems = previous.Value.TryGetProperty("items", out var prevItemsElem)
            && prevItemsElem.ValueKind == JsonValueKind.Array
            ? prevItemsElem
            : (JsonElement?)null;

        var currentMap = BuildIdentityMap(dataElement, identityKey, context.Logger);
        var previousMap = previousItems.HasValue
            ? BuildIdentityMap(previousItems.Value, identityKey, context.Logger)
            : new Dictionary<string, JsonElement>();

        var added = new List<JsonElement>();
        var removed = new List<JsonElement>();
        var modified = new List<JsonElement>();
        var unchanged = new List<JsonElement>();

        foreach (var (key, currentItem) in currentMap)
        {
            if (!previousMap.ContainsKey(key))
                added.Add(currentItem);
            else if (currentItem.GetRawText() != previousMap[key].GetRawText())
                modified.Add(currentItem);
            else
                unchanged.Add(currentItem);
        }

        foreach (var (key, prevItem) in previousMap)
        {
            if (!currentMap.ContainsKey(key))
                removed.Add(prevItem);
        }

        var changed = added.Count > 0 || removed.Count > 0 || modified.Count > 0;

        object result = mode switch
        {
            "additions_only" => new
            {
                items = dataElement,
                added,
                changed = added.Count > 0
            },
            "removals_only" => new
            {
                items = dataElement,
                removed,
                changed = removed.Count > 0
            },
            _ => new
            {
                items = dataElement,
                added,
                removed,
                modified,
                unchanged,
                changed
            }
        };

        var output = JsonSerializer.SerializeToElement(result);
        return BlockResult.Succeeded(output);
    }

    private static Dictionary<string, JsonElement> BuildIdentityMap(
        JsonElement array, string identityKey, ILogger logger)
    {
        var map = new Dictionary<string, JsonElement>();
        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var key = item.TryGetProperty(identityKey, out var keyElem) && keyElem.ValueKind == JsonValueKind.String
                ? keyElem.GetString()!
                : index.ToString();
            if (map.ContainsKey(key))
                logger.LogWarning("ListDiff: duplicate identity key '{Key}' — last item wins", key);
            map[key] = item.Clone();
            index++;
        }
        return map;
    }

    private static (string identityKey, string mode) ReadConfig(BlockContext context)
    {
        var identityKey = "url";
        var mode = "all_changes";

        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (identityKey, mode);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (identityKey, mode);

        if (config.TryGetProperty("identityKey", out var keyElem) && keyElem.ValueKind == JsonValueKind.String)
            identityKey = keyElem.GetString()!;

        if (config.TryGetProperty("mode", out var modeElem) && modeElem.ValueKind == JsonValueKind.String)
            mode = modeElem.GetString()!;

        return (identityKey, mode);
    }
}
