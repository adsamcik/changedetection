using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Advanced;

/// <summary>
/// Applies field transformations: rename, drop, and compute (simple template substitution).
/// </summary>
public partial class TransformBlock : IPipelineBlock
{
    public string BlockType => "Transform";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;
    public bool IsCacheable => true;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("Transform block requires a 'data' input."));

        var (rename, drop, compute) = ReadConfig(context);

        try
        {
            var result = ApplyTransformations(dataElement, rename, drop, compute);
            return Task.FromResult(BlockResult.Succeeded(result));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"Transform failed: {ex.Message}"));
        }
    }

    private static JsonElement ApplyTransformations(
        JsonElement data,
        Dictionary<string, string>? rename,
        List<string>? drop,
        Dictionary<string, string>? compute)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return data;

        var dict = new Dictionary<string, JsonElement>();

        // Copy existing fields
        foreach (var prop in data.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();

        // Apply renames
        if (rename is not null)
        {
            foreach (var (oldName, newName) in rename)
            {
                if (dict.Remove(oldName, out var value))
                    dict[newName] = value;
            }
        }

        // Apply drops
        if (drop is not null)
        {
            foreach (var field in drop)
                dict.Remove(field);
        }

        // Apply compute (simple ${fieldName} substitution)
        if (compute is not null)
        {
            foreach (var (targetField, template) in compute)
            {
                var result = FieldReferencePattern().Replace(template, match =>
                {
                    var refField = match.Groups[1].Value;
                    if (dict.TryGetValue(refField, out var refValue))
                    {
                        return refValue.ValueKind == JsonValueKind.String
                            ? refValue.GetString() ?? ""
                            : refValue.GetRawText();
                    }
                    return match.Value;
                });
                dict[targetField] = JsonSerializer.SerializeToElement(result);
            }
        }

        return JsonSerializer.SerializeToElement(dict);
    }

    private static (Dictionary<string, string>? rename, List<string>? drop, Dictionary<string, string>? compute) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null, null);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null, null);

        Dictionary<string, string>? rename = null;
        List<string>? drop = null;
        Dictionary<string, string>? compute = null;

        if (config.TryGetProperty("rename", out var renameElem) && renameElem.ValueKind == JsonValueKind.Object)
        {
            rename = [];
            foreach (var prop in renameElem.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    rename[prop.Name] = prop.Value.GetString()!;
            }
        }

        if (config.TryGetProperty("drop", out var dropElem) && dropElem.ValueKind == JsonValueKind.Array)
        {
            drop = [];
            foreach (var item in dropElem.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    drop.Add(item.GetString()!);
            }
        }

        if (config.TryGetProperty("compute", out var computeElem) && computeElem.ValueKind == JsonValueKind.Object)
        {
            compute = [];
            foreach (var prop in computeElem.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    compute[prop.Name] = prop.Value.GetString()!;
            }
        }

        return (rename, drop, compute);
    }

    [GeneratedRegex(@"\$\{(\w+)\}")]
    private static partial Regex FieldReferencePattern();
}
