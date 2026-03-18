using System.Buffers;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Extraction;

/// <summary>
/// Strips volatile text fragments such as timestamps from extracted JSON data before comparison.
/// </summary>
public class VolatilityFilterBlock : IPipelineBlock
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly (string Name, string Pattern)[] DefaultTimestampPatterns =
    [
        ("iso-8601", @"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+\-]\d{2}:\d{2})\b"),
        ("unix-epoch", @"\b\d{10,13}\b"),
        ("sql-datetime", @"\b\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\b")
    ];

    public string BlockType => "VolatilityFilter";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("VolatilityFilter block requires a 'data' input."));

        try
        {
            var config = ReadConfig(context);
            var buffer = new ArrayBufferWriter<byte>();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteElement(dataElement, writer, config.Patterns, config.Replacement, context);
            }

            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return Task.FromResult(BlockResult.Succeeded(document.RootElement.Clone()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"VolatilityFilter failed: {ex.Message}"));
        }
    }

    private static FilterConfig ReadConfig(BlockContext context)
    {
        var replacement = string.Empty;
        var stripTimestamps = true;
        var patterns = new List<CompiledPattern>();

        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return new FilterConfig(patterns, replacement);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return new FilterConfig(patterns, replacement);

        if (config.TryGetProperty("replacement", out var replacementElem) &&
            replacementElem.ValueKind == JsonValueKind.String)
        {
            replacement = replacementElem.GetString() ?? string.Empty;
        }

        if (config.TryGetProperty("stripTimestamps", out var stripTimestampsElem) &&
            stripTimestampsElem.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            stripTimestamps = stripTimestampsElem.GetBoolean();
        }

        if (config.TryGetProperty("stripPatterns", out var stripPatternsElem) &&
            stripPatternsElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var patternElem in stripPatternsElem.EnumerateArray())
            {
                if (patternElem.ValueKind != JsonValueKind.Object)
                    continue;

                var name = patternElem.TryGetProperty("name", out var nameElem) && nameElem.ValueKind == JsonValueKind.String
                    ? nameElem.GetString() ?? "custom"
                    : "custom";

                if (!patternElem.TryGetProperty("pattern", out var regexElem) || regexElem.ValueKind != JsonValueKind.String)
                    continue;

                var pattern = regexElem.GetString();
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

                TryAddPattern(patterns, name, pattern, context);
            }
        }

        if (stripTimestamps)
        {
            foreach (var (name, pattern) in DefaultTimestampPatterns)
                TryAddPattern(patterns, name, pattern, context);
        }

        return new FilterConfig(patterns, replacement);
    }

    private static void TryAddPattern(List<CompiledPattern> patterns, string name, string pattern, BlockContext context)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.NonBacktracking, RegexTimeout);
            patterns.Add(new CompiledPattern(name, regex));
        }
        catch (ArgumentException ex)
        {
            context.Logger.LogWarning(ex,
                "VolatilityFilterBlock: strip pattern '{PatternName}' skipped because it could not be compiled.",
                name);
        }
    }

    private static void WriteElement(
        JsonElement element,
        Utf8JsonWriter writer,
        IReadOnlyList<CompiledPattern> patterns,
        string replacement,
        BlockContext context)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(property.Value, writer, patterns, replacement, context);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(item, writer, patterns, replacement, context);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(ApplyPatterns(element.GetString() ?? string.Empty, patterns, replacement, context));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string ApplyPatterns(
        string value,
        IReadOnlyList<CompiledPattern> patterns,
        string replacement,
        BlockContext context)
    {
        var current = value;

        foreach (var pattern in patterns)
        {
            var replaced = SafeRegex.TryReplace(current, pattern.Regex, replacement);
            if (replaced is null)
            {
                context.Logger.LogWarning(
                    "VolatilityFilterBlock: strip pattern '{PatternName}' timed out while processing a string value.",
                    pattern.Name);
                continue;
            }

            current = replaced;
        }

        return current;
    }

    private sealed record FilterConfig(
        IReadOnlyList<CompiledPattern> Patterns,
        string Replacement);

    private sealed record CompiledPattern(string Name, Regex Regex);
}
