using System.Globalization;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Decision;

/// <summary>
/// Evaluates a condition against extracted data or diff results, outputting a boolean signal.
/// Supports operators: equals, notEquals, contains, notContains, greaterThan, lessThan,
/// between, changedByPercent, isNewMinimum, regex.
/// </summary>
public class ConditionBlock : IPipelineBlock
{
    public string BlockType => "Condition";

    public IReadOnlyList<PortDescriptor> InputPorts =>
    [
        new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false },
        new PortDescriptor { Name = "result", Type = PortType.DiffResult, Required = false }
    ];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (!context.Inputs.TryGetValue("data", out var inputElement) &&
            !context.Inputs.TryGetValue("result", out inputElement))
        {
            return Task.FromResult(BlockResult.Skip("No data to evaluate"));
        }

        var (field, op, threshold) = ReadConfig(context);

        if (field is null || op is null)
            return Task.FromResult(BlockResult.Failed("Condition block requires 'field' and 'operator' in config."));

        var actualValue = NavigateToField(inputElement, field);
        var signal = Evaluate(op, actualValue, threshold, inputElement);

        var output = JsonSerializer.SerializeToElement(new
        {
            signal,
            field,
            actualValue = actualValue?.ToString() ?? "",
            @operator = op,
            threshold = threshold?.ToString() ?? ""
        });

        return Task.FromResult(BlockResult.Succeeded(output));
    }

    private static (string? field, string? op, JsonElement? value) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null, null);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null, null);

        string? field = null, op = null;
        JsonElement? value = null;

        if (config.TryGetProperty("field", out var fieldElem) && fieldElem.ValueKind == JsonValueKind.String)
            field = fieldElem.GetString();

        if (config.TryGetProperty("operator", out var opElem) && opElem.ValueKind == JsonValueKind.String)
            op = opElem.GetString();

        if (config.TryGetProperty("value", out var valElem))
            value = valElem;

        return (field, op, value);
    }

    /// <summary>
    /// Navigates a JSON element using dot notation (e.g., "added.length").
    /// For arrays, ".length" returns the array count.
    /// </summary>
    private static JsonElement? NavigateToField(JsonElement element, string fieldPath)
    {
        var segments = fieldPath.Split('.');
        var current = element;

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];

            if (segment.Equals("length", StringComparison.OrdinalIgnoreCase) &&
                current.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.SerializeToElement(current.GetArrayLength());
            }

            if (current.ValueKind != JsonValueKind.Object ||
                !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static bool Evaluate(string op, JsonElement? actualElement, JsonElement? threshold, JsonElement root)
    {
        return op.ToLowerInvariant() switch
        {
            "equals" => EqualsCheck(actualElement, threshold),
            "notequals" => !EqualsCheck(actualElement, threshold),
            "contains" => ContainsCheck(actualElement, threshold),
            "notcontains" => !ContainsCheck(actualElement, threshold),
            "greaterthan" => NumericCompare(actualElement, threshold) > 0,
            "lessthan" => NumericCompare(actualElement, threshold) < 0,
            "between" => BetweenCheck(actualElement, threshold),
            "changedbypercent" => ChangedByPercentCheck(root, threshold),
            "isnewminimum" => IsNewMinimumCheck(root),
            "regex" => RegexCheck(actualElement, threshold),
            _ => false
        };
    }

    private static bool EqualsCheck(JsonElement? actual, JsonElement? expected)
    {
        if (actual is null || expected is null) return false;

        return (actual.Value.ValueKind, expected.Value.ValueKind) switch
        {
            (JsonValueKind.True or JsonValueKind.False, JsonValueKind.True or JsonValueKind.False)
                => actual.Value.ValueKind == expected.Value.ValueKind,

            (JsonValueKind.Number, JsonValueKind.Number)
                => actual.Value.GetDecimal() == expected.Value.GetDecimal(),

            (JsonValueKind.String, JsonValueKind.String)
                => string.Equals(actual.Value.GetString(), expected.Value.GetString(), StringComparison.OrdinalIgnoreCase),

            // Compare boolean element against boolean value stored as non-boolean JSON
            (JsonValueKind.True or JsonValueKind.False, _) when TryGetBool(expected.Value, out var b)
                => (actual.Value.ValueKind == JsonValueKind.True) == b,

            (_, JsonValueKind.True or JsonValueKind.False) when TryGetBool(actual.Value, out var b)
                => b == (expected.Value.ValueKind == JsonValueKind.True),

            _ => string.Equals(actual.Value.ToString(), expected.Value.ToString(), StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool TryGetBool(JsonElement element, out bool value)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
            return bool.TryParse(element.GetString(), out value);

        value = false;
        return false;
    }

    private static bool ContainsCheck(JsonElement? actual, JsonElement? expected)
    {
        if (actual is null || expected is null) return false;

        var actualStr = actual.Value.ValueKind == JsonValueKind.String
            ? actual.Value.GetString()
            : actual.Value.GetRawText();

        var expectedStr = expected.Value.ValueKind == JsonValueKind.String
            ? expected.Value.GetString()
            : expected.Value.GetRawText();

        return actualStr?.Contains(expectedStr ?? "", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static int NumericCompare(JsonElement? actual, JsonElement? expected)
    {
        var a = GetDecimal(actual);
        var b = GetDecimal(expected);
        if (a is null || b is null) return 0;
        return a.Value.CompareTo(b.Value);
    }

    private static bool BetweenCheck(JsonElement? actual, JsonElement? threshold)
    {
        if (actual is null || threshold is null) return false;

        var value = GetDecimal(actual);
        if (value is null) return false;

        if (threshold.Value.ValueKind == JsonValueKind.Array && threshold.Value.GetArrayLength() == 2)
        {
            var low = GetDecimal(threshold.Value[0]);
            var high = GetDecimal(threshold.Value[1]);
            if (low is null || high is null) return false;
            return value >= low && value <= high;
        }

        return false;
    }

    private static bool ChangedByPercentCheck(JsonElement root, JsonElement? threshold)
    {
        var deltaPercent = NavigateToField(root, "deltaPercent");
        if (deltaPercent is null || threshold is null) return false;

        var actual = GetDecimal(deltaPercent);
        var expected = GetDecimal(threshold);
        if (actual is null || expected is null) return false;

        return Math.Abs(actual.Value) >= Math.Abs(expected.Value);
    }

    private static bool IsNewMinimumCheck(JsonElement root)
    {
        var field = NavigateToField(root, "isNewMinimum");
        return field?.ValueKind == JsonValueKind.True;
    }

    private static bool RegexCheck(JsonElement? actual, JsonElement? pattern)
    {
        if (actual is null || pattern is null) return false;

        var str = actual.Value.ValueKind == JsonValueKind.String
            ? actual.Value.GetString()
            : actual.Value.GetRawText();

        var pat = pattern.Value.ValueKind == JsonValueKind.String
            ? pattern.Value.GetString()
            : pattern.Value.GetRawText();

        if (str is null || pat is null) return false;
        return SafeRegex.TryIsMatch(str, pat);
    }

    private static decimal? GetDecimal(JsonElement? element)
    {
        if (element is null) return null;
        return GetDecimal(element.Value);
    }

    private static decimal? GetDecimal(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number)
            return element.GetDecimal();

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;

        return null;
    }
}
