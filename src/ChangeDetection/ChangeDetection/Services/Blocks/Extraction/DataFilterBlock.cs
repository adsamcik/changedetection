using System.Globalization;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Extraction;

/// <summary>
/// Filters structured JSON data using WHERE-clause-like conditions.
/// Operates on ExtractedObjects (JSON arrays/objects) from upstream blocks.
/// Config: { "conditions": [{ "field": "price", "operator": "lt", "value": 100 }], "mode": "all"|"any" }
/// </summary>
public class DataFilterBlock : IPipelineBlock
{
    public string BlockType => "DataFilter";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "filtered", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
    public bool IsCacheable => true;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("DataFilter block requires a 'data' input."));

        var (conditions, mode) = ReadConfig(context);

        if (conditions.Count == 0)
            return Task.FromResult(BlockResult.Succeeded(dataElement));

        try
        {
            if (dataElement.ValueKind == JsonValueKind.Array)
            {
                var filtered = new List<JsonElement>();
                foreach (var item in dataElement.EnumerateArray())
                {
                    if (EvaluateConditions(item, conditions, mode))
                        filtered.Add(item.Clone());
                }

                var result = JsonSerializer.SerializeToElement(filtered);
                return Task.FromResult(BlockResult.Succeeded(result));
            }

            if (dataElement.ValueKind == JsonValueKind.Object)
            {
                if (EvaluateConditions(dataElement, conditions, mode))
                    return Task.FromResult(BlockResult.Succeeded(dataElement));
                else
                    return Task.FromResult(BlockResult.Succeeded(JsonSerializer.SerializeToElement(Array.Empty<object>())));
            }

            return Task.FromResult(BlockResult.Failed($"DataFilter expects array or object, got {dataElement.ValueKind}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"DataFilter error: {ex.Message}"));
        }
    }

    private static bool EvaluateConditions(JsonElement item, List<FilterCondition> conditions, string mode)
    {
        if (mode == "any")
            return conditions.Any(c => EvaluateSingle(item, c));
        return conditions.All(c => EvaluateSingle(item, c)); // "all" is default
    }

    private static bool EvaluateSingle(JsonElement item, FilterCondition condition)
    {
        if (!item.TryGetProperty(condition.Field, out var fieldValue))
            return condition.Operator.Equals("notexists", StringComparison.OrdinalIgnoreCase);

        return condition.Operator.ToLowerInvariant() switch
        {
            "eq" or "==" or "equals" => CompareEqual(fieldValue, condition.Value),
            "neq" or "!=" or "notequals" => !CompareEqual(fieldValue, condition.Value),
            "gt" or ">" => CompareNumeric(fieldValue, condition.Value, (a, b) => a > b),
            "gte" or ">=" => CompareNumeric(fieldValue, condition.Value, (a, b) => a >= b),
            "lt" or "<" => CompareNumeric(fieldValue, condition.Value, (a, b) => a < b),
            "lte" or "<=" => CompareNumeric(fieldValue, condition.Value, (a, b) => a <= b),
            "contains" => GetStringValue(fieldValue)?.Contains(GetStringFromValue(condition.Value), StringComparison.OrdinalIgnoreCase) ?? false,
            "startswith" => GetStringValue(fieldValue)?.StartsWith(GetStringFromValue(condition.Value), StringComparison.OrdinalIgnoreCase) ?? false,
            "endswith" => GetStringValue(fieldValue)?.EndsWith(GetStringFromValue(condition.Value), StringComparison.OrdinalIgnoreCase) ?? false,
            "exists" => true,
            "notexists" => false, // field was found, so notExists is false
            "isnull" => fieldValue.ValueKind == JsonValueKind.Null,
            "isnotnull" => fieldValue.ValueKind != JsonValueKind.Null,
            _ => false
        };
    }

    private static bool CompareEqual(JsonElement field, JsonElement value)
    {
        if (field.ValueKind == JsonValueKind.String && value.ValueKind == JsonValueKind.String)
            return string.Equals(field.GetString(), value.GetString(), StringComparison.OrdinalIgnoreCase);

        if (field.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number)
            return field.GetDecimal() == value.GetDecimal();

        if (field.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return field.GetBoolean() == value.GetBoolean();

        // Cross-type: compare string representations
        return string.Equals(field.ToString(), value.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool CompareNumeric(JsonElement field, JsonElement value, Func<decimal, decimal, bool> comparison)
    {
        var fieldNum = GetNumericValue(field);
        var valueNum = GetNumericValue(value);
        if (fieldNum.HasValue && valueNum.HasValue)
            return comparison(fieldNum.Value, valueNum.Value);
        return false;
    }

    private static decimal? GetNumericValue(JsonElement elem)
    {
        if (elem.ValueKind == JsonValueKind.Number)
            return elem.GetDecimal();
        if (elem.ValueKind == JsonValueKind.String &&
            decimal.TryParse(elem.GetString()?.Replace("$", "").Replace("€", "").Replace("£", "").Trim(),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }

    private static string? GetStringValue(JsonElement elem) =>
        elem.ValueKind == JsonValueKind.String ? elem.GetString() : elem.ToString();

    private static string GetStringFromValue(JsonElement elem) =>
        elem.ValueKind == JsonValueKind.String ? elem.GetString() ?? "" : elem.ToString();

    private static (List<FilterCondition> conditions, string mode) ReadConfig(BlockContext context)
    {
        var conditions = new List<FilterCondition>();
        var mode = "all";

        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (conditions, mode);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (conditions, mode);

        if (config.TryGetProperty("mode", out var modeElem) && modeElem.ValueKind == JsonValueKind.String)
            mode = modeElem.GetString() ?? "all";

        if (config.TryGetProperty("conditions", out var condArray) && condArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var cond in condArray.EnumerateArray())
            {
                if (cond.ValueKind != JsonValueKind.Object) continue;
                if (!cond.TryGetProperty("field", out var fieldProp) || fieldProp.ValueKind != JsonValueKind.String) continue;
                if (!cond.TryGetProperty("operator", out var opProp) || opProp.ValueKind != JsonValueKind.String) continue;

                var value = cond.TryGetProperty("value", out var valProp) ? valProp : default;

                conditions.Add(new FilterCondition(fieldProp.GetString()!, opProp.GetString()!, value));
            }
        }

        return (conditions, mode);
    }

    private sealed record FilterCondition(string Field, string Operator, JsonElement Value);
}
