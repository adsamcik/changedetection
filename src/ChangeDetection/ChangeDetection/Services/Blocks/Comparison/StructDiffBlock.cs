using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Comparison;

/// <summary>
/// Compares structured JSON objects property-by-property, reporting individual field changes.
/// </summary>
public class StructDiffBlock : IPipelineBlock
{
    public string BlockType => "StructDiff";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "result", Type = PortType.DiffResult }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return BlockResult.Failed("StructDiff block requires a 'data' input.");

        if (dataElement.ValueKind != JsonValueKind.Object)
            return BlockResult.Failed($"StructDiff expects a JSON object input, got {dataElement.ValueKind}");

        var ct = context.CancellationToken;
        var identityFields = ReadIdentityFields(context);

        var previous = await context.StateStore.GetPreviousOutputAsync(
            context.WatchId.ToString(), context.BlockInstanceId, ct);

        if (previous is null)
        {
            var baseline = JsonSerializer.SerializeToElement(new
            {
                snapshot = dataElement,
                changed = false
            });
            return BlockResult.BaselineCapture(baseline);
        }

        var previousSnapshot = previous.Value.TryGetProperty("snapshot", out var snapshotElem)
            ? snapshotElem
            : previous.Value;

        var changes = new List<object>();

        var fieldsToCompare = identityFields is not null
            ? new HashSet<string>(identityFields, StringComparer.Ordinal)
            : null;

        foreach (var prop in dataElement.EnumerateObject())
        {
            if (fieldsToCompare is not null && !fieldsToCompare.Contains(prop.Name))
                continue;

            var currentValue = prop.Value.GetRawText();

            if (previousSnapshot.TryGetProperty(prop.Name, out var prevProp))
            {
                var previousValue = prevProp.GetRawText();
                if (currentValue != previousValue)
                {
                    changes.Add(new
                    {
                        field = prop.Name,
                        old = FormatValue(prevProp),
                        @new = FormatValue(prop.Value)
                    });
                }
            }
            else
            {
                changes.Add(new
                {
                    field = prop.Name,
                    old = (string?)null,
                    @new = FormatValue(prop.Value)
                });
            }
        }

        if (previousSnapshot.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in previousSnapshot.EnumerateObject())
            {
                if (fieldsToCompare is not null && !fieldsToCompare.Contains(prop.Name))
                    continue;

                if (!dataElement.TryGetProperty(prop.Name, out _))
                {
                    changes.Add(new
                    {
                        field = prop.Name,
                        old = FormatValue(prop.Value),
                        @new = (string?)null
                    });
                }
            }
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            snapshot = dataElement,
            changes,
            changed = changes.Count > 0
        });

        return BlockResult.Succeeded(output);
    }

    private static string? FormatValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText()
    };

    private static List<string>? ReadIdentityFields(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return null;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return null;

        if (!config.TryGetProperty("identityFields", out var fieldsElem) ||
            fieldsElem.ValueKind != JsonValueKind.Array)
            return null;

        var fields = new List<string>();
        foreach (var item in fieldsElem.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                fields.Add(item.GetString()!);
        }

        return fields.Count > 0 ? fields : null;
    }
}
