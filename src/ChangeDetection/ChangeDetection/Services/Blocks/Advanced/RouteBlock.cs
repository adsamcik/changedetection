using System.Globalization;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Advanced;

/// <summary>
/// Routes data by evaluating conditions against input fields.
/// Adds a _route field indicating which condition matched.
/// </summary>
public class RouteBlock : IPipelineBlock
{
    public string BlockType => "Route";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("Route block requires a 'data' input."));

        var conditions = ReadConfig(context);

        var matchedRoute = "default";

        if (conditions is not null)
        {
            foreach (var condition in conditions)
            {
                if (EvaluateCondition(dataElement, condition))
                {
                    matchedRoute = condition.Output ?? "default";
                    break;
                }
            }
        }

        try
        {
            // Clone data and add routing fields
            var dict = new Dictionary<string, JsonElement>();
            if (dataElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in dataElement.EnumerateObject())
                    dict[prop.Name] = prop.Value.Clone();
            }
            dict["_route"] = JsonSerializer.SerializeToElement(matchedRoute);
            dict["_selectedRoute"] = JsonSerializer.SerializeToElement(matchedRoute);
            dict["signal"] = JsonSerializer.SerializeToElement(true);

            var output = JsonSerializer.SerializeToElement(dict);
            return Task.FromResult(BlockResult.Succeeded(output));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"Route failed: {ex.Message}"));
        }
    }

    private static bool EvaluateCondition(JsonElement data, RouteCondition condition)
    {
        if (string.IsNullOrWhiteSpace(condition.Field))
            return false;

        // Navigate to field value
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(condition.Field, out var fieldValue))
            return false;

        var fieldStr = fieldValue.ValueKind == JsonValueKind.String
            ? fieldValue.GetString() ?? ""
            : fieldValue.GetRawText();

        if (condition.Equals is not null)
            return string.Equals(fieldStr, condition.Equals, StringComparison.OrdinalIgnoreCase);

        if (condition.Contains is not null)
            return fieldStr.Contains(condition.Contains, StringComparison.OrdinalIgnoreCase);

        if (condition.GreaterThan is not null)
        {
            if (TryGetDecimal(fieldValue, out var actual) &&
                decimal.TryParse(condition.GreaterThan, NumberStyles.Any, CultureInfo.InvariantCulture, out var threshold))
                return actual > threshold;
        }

        if (condition.LessThan is not null)
        {
            if (TryGetDecimal(fieldValue, out var actual) &&
                decimal.TryParse(condition.LessThan, NumberStyles.Any, CultureInfo.InvariantCulture, out var threshold))
                return actual < threshold;
        }

        return false;
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.TryGetDecimal(out value);

        if (element.ValueKind == JsonValueKind.String)
            return decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

        value = 0;
        return false;
    }

    private static List<RouteCondition>? ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return null;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return null;

        if (!config.TryGetProperty("conditions", out var conditionsElem) ||
            conditionsElem.ValueKind != JsonValueKind.Array)
            return null;

        var conditions = new List<RouteCondition>();
        foreach (var item in conditionsElem.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var condition = new RouteCondition
            {
                Field = item.TryGetProperty("field", out var f) && f.ValueKind == JsonValueKind.String
                    ? f.GetString() : null,
                Output = item.TryGetProperty("output", out var o) && o.ValueKind == JsonValueKind.String
                    ? o.GetString() : null,
                Equals = item.TryGetProperty("equals", out var eq) && eq.ValueKind == JsonValueKind.String
                    ? eq.GetString() : null,
                Contains = item.TryGetProperty("contains", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() : null,
                GreaterThan = item.TryGetProperty("greaterThan", out var gt) && gt.ValueKind == JsonValueKind.String
                    ? gt.GetString() : null,
                LessThan = item.TryGetProperty("lessThan", out var lt) && lt.ValueKind == JsonValueKind.String
                    ? lt.GetString() : null
            };

            conditions.Add(condition);
        }

        return conditions;
    }

    private sealed class RouteCondition
    {
        public string? Field { get; init; }
        public string? Output { get; init; }
        public new string? Equals { get; init; }
        public string? Contains { get; init; }
        public string? GreaterThan { get; init; }
        public string? LessThan { get; init; }
    }
}
