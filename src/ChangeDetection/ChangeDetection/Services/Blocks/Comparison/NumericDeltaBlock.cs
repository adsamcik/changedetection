using System.Globalization;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Comparison;

/// <summary>
/// Tracks numeric field changes across runs, computing deltas, percentages, and trend direction.
/// </summary>
public class NumericDeltaBlock : IPipelineBlock
{
    public string BlockType => "NumericDelta";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
    [
        new PortDescriptor { Name = "result", Type = PortType.DiffResult },
        new PortDescriptor { Name = "value", Type = PortType.NumericValue }
    ];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return BlockResult.Failed("NumericDelta block requires a 'data' input.");

        var ct = context.CancellationToken;
        var field = ReadFieldConfig(context);

        if (!TryExtractNumericValue(dataElement, field, out var currentValue))
            return BlockResult.Failed($"NumericDelta block could not extract numeric value from field '{field}'.");

        var previous = await context.StateStore.GetPreviousOutputAsync(
            context.WatchId.ToString(), context.BlockInstanceId, ct);

        if (previous is null)
        {
            var baseline = JsonSerializer.SerializeToElement(new
            {
                value = currentValue,
                field,
                changed = false
            });
            return BlockResult.BaselineCapture(baseline);
        }

        var previousValue = previous.Value.TryGetProperty("value", out var prevValElem)
            ? prevValElem.GetDecimal()
            : 0m;

        var historicalMin = previous.Value.TryGetProperty("historicalMin", out var minElem)
            ? minElem.GetDecimal()
            : previousValue;
        var historicalMax = previous.Value.TryGetProperty("historicalMax", out var maxElem)
            ? maxElem.GetDecimal()
            : previousValue;

        var delta = currentValue - previousValue;
        decimal deltaPercent;
        if (previousValue == 0)
            deltaPercent = currentValue == 0 ? 0m : (currentValue > 0 ? 100m : -100m);
        else
            deltaPercent = Math.Round(delta / previousValue * 100, 2);

        var trend = delta > 0 ? "up" : delta < 0 ? "down" : "flat";
        var changed = delta != 0;

        var newMin = Math.Min(currentValue, historicalMin);
        var newMax = Math.Max(currentValue, historicalMax);

        var output = JsonSerializer.SerializeToElement(new
        {
            value = currentValue,
            previousValue,
            delta,
            deltaPercent,
            trend,
            isNewMinimum = currentValue < historicalMin,
            isNewMaximum = currentValue > historicalMax,
            historicalMin = newMin,
            historicalMax = newMax,
            field,
            changed
        });

        return BlockResult.Succeeded(output);
    }

    private static bool TryExtractNumericValue(JsonElement data, string field, out decimal value)
    {
        value = 0;

        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out var fieldElem))
        {
            return fieldElem.ValueKind switch
            {
                JsonValueKind.Number => fieldElem.TryGetDecimal(out value),
                JsonValueKind.String => decimal.TryParse(
                    StripCurrencySymbols(fieldElem.GetString()),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out value),
                _ => false
            };
        }

        // If data is a direct numeric value
        if (data.ValueKind == JsonValueKind.Number)
            return data.TryGetDecimal(out value);

        if (data.ValueKind == JsonValueKind.String)
            return decimal.TryParse(
                StripCurrencySymbols(data.GetString()),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out value);

        return false;
    }

    private static string? StripCurrencySymbols(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        // Remove common currency symbols and whitespace
        return input.Replace("$", "").Replace("€", "").Replace("£", "").Replace("¥", "")
            .Replace("₹", "").Replace("₽", "").Replace("₩", "").Replace("¢", "")
            .Replace("₪", "").Replace("₫", "").Replace("₡", "").Trim();
    }

    private static string ReadFieldConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return "price";

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return "price";

        return config.TryGetProperty("field", out var fieldElem) && fieldElem.ValueKind == JsonValueKind.String
            ? fieldElem.GetString()!
            : "price";
    }
}
