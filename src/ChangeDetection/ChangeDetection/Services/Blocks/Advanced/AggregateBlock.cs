using System.Globalization;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Blocks.Advanced;

/// <summary>
/// Groups input data by a field and applies summarization functions (count, sum, min, max, avg).
/// </summary>
public class AggregateBlock : IPipelineBlock
{
    public string BlockType => "Aggregate";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("Aggregate block requires a 'data' input."));

        var (groupBy, summarize) = ReadConfig(context);
        if (string.IsNullOrWhiteSpace(groupBy))
            return Task.FromResult(BlockResult.Failed("Aggregate block requires 'groupBy' in config."));

        try
        {
            // Wrap scalar/object in array
            var items = new List<JsonElement>();
            if (dataElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataElement.EnumerateArray())
                    items.Add(item);
            }
            else
            {
                items.Add(dataElement);
            }

            // Group by field
            var groups = new Dictionary<string, List<JsonElement>>();
            foreach (var item in items)
            {
                var key = "(none)";
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty(groupBy, out var keyElem))
                {
                    key = keyElem.ValueKind == JsonValueKind.String
                        ? keyElem.GetString() ?? "(none)"
                        : keyElem.GetRawText();
                }
                if (!groups.TryGetValue(key, out var list))
                {
                    list = [];
                    groups[key] = list;
                }
                list.Add(item);
            }

            // Apply summarization
            var groupResults = new List<Dictionary<string, object>>();
            foreach (var (key, groupItems) in groups)
            {
                var result = new Dictionary<string, object> { ["key"] = key };

                if (summarize is not null)
                {
                    foreach (var (outputName, function) in summarize)
                    {
                        result[outputName] = ApplySummarization(function, groupItems, context.Logger);
                    }
                }

                groupResults.Add(result);
            }

            var output = JsonSerializer.SerializeToElement(new { groups = groupResults });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"Aggregation failed: {ex.Message}"));
        }
    }

    private static object ApplySummarization(string function, List<JsonElement> items, ILogger logger)
    {
        if (function.Equals("count", StringComparison.OrdinalIgnoreCase))
            return items.Count;

        var parts = function.Split(':', 2);
        if (parts.Length != 2)
        {
            logger.LogWarning("AggregateBlock: unknown function '{Function}'", function);
            return 0;
        }

        var op = parts[0].ToLowerInvariant();
        var field = parts[1];

        var values = new List<decimal>();
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var val))
            {
                if (val.ValueKind == JsonValueKind.Number && val.TryGetDecimal(out var d))
                    values.Add(d);
                else if (val.ValueKind == JsonValueKind.String &&
                         decimal.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    values.Add(parsed);
            }
        }

        if (values.Count == 0)
            return 0;

        return op switch
        {
            "sum" => values.Sum(),
            "min" => values.Min(),
            "max" => values.Max(),
            "avg" => values.Average(),
            _ => LogAndReturnDefault(logger, op)
        };
    }

    private static object LogAndReturnDefault(ILogger logger, string op)
    {
        logger.LogWarning("AggregateBlock: unknown function '{Function}'", op);
        return 0;
    }

    private static (string? groupBy, Dictionary<string, string>? summarize) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null);

        string? groupBy = null;
        Dictionary<string, string>? summarize = null;

        if (config.TryGetProperty("groupBy", out var groupByElem) && groupByElem.ValueKind == JsonValueKind.String)
            groupBy = groupByElem.GetString();

        if (config.TryGetProperty("summarize", out var sumElem) && sumElem.ValueKind == JsonValueKind.Object)
        {
            summarize = [];
            foreach (var prop in sumElem.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    summarize[prop.Name] = prop.Value.GetString()!;
            }
        }

        return (groupBy, summarize);
    }
}
