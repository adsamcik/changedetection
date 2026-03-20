using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Extraction;

/// <summary>
/// Extracts structured data from JSON payloads using a deliberately constrained JSONPath subset.
/// </summary>
public class JsonExtractBlock : IPipelineBlock
{
    private const int MaxInputBytes = 5 * 1024 * 1024;
    private const int MaxJsonPathLength = 200;
    private const int MaxArrayItems = 1000;
    private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex SafeJsonPathRegex = new(
        @"^\$(\.[a-zA-Z_][a-zA-Z0-9_]*|\[\d+\]|\[\*\]|\[\d+:\d+\])*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);
    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        RegexTimeout);

    public string BlockType => "JsonExtract";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "json", Type = PortType.PlainText }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
    [
        new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects },
        new PortDescriptor { Name = "total", Type = PortType.NumericValue, Required = false }
    ];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
    public bool IsCacheable => true;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("json", out var jsonInput))
            return Task.FromResult(BlockResult.Failed("JsonExtract block requires a 'json' input."));

        var rawJson = ResolveJsonPayload(jsonInput);
        if (string.IsNullOrWhiteSpace(rawJson))
            return Task.FromResult(BlockResult.Failed("JsonExtract block received empty or invalid JSON."));

        var byteCount = Encoding.UTF8.GetByteCount(rawJson);
        if (byteCount > MaxInputBytes)
            return Task.FromResult(BlockResult.Failed($"JsonExtract input exceeds the {MaxInputBytes} byte limit."));

        var config = ReadConfig(context);
        if (config.Extractions.Count == 0)
            return Task.FromResult(BlockResult.Failed("JsonExtract block requires at least one extraction rule."));

        foreach (var extraction in config.Extractions)
        {
            var validationError = ValidateJsonPath(extraction.JsonPath);
            if (validationError is not null)
                return Task.FromResult(BlockResult.Failed(
                    $"JsonExtract block rejected JSONPath for '{extraction.Name}': {validationError}"));
        }

        try
        {
            using var document = JsonDocument.Parse(rawJson, new JsonDocumentOptions
            {
                MaxDepth = 20,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });

            using var evaluationCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            evaluationCts.CancelAfter(EvaluationTimeout);

            var extractedValues = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            string? totalExtractionName = null;

            foreach (var extraction in config.Extractions)
            {
                var evaluated = EvaluateSimpleJsonPath(
                    document.RootElement,
                    extraction.JsonPath,
                    evaluationCts.Token);

                var normalized = NormalizeExtractionValue(evaluated, extraction.Type);
                if (config.StripHtmlFields.Count > 0 &&
                    !string.Equals(extraction.Type, "number", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = StripHtmlFields(normalized, config.StripHtmlFields);
                }

                extractedValues[extraction.Name] = normalized;

                if (totalExtractionName is null &&
                    string.Equals(extraction.Type, "number", StringComparison.OrdinalIgnoreCase))
                {
                    totalExtractionName = extraction.Name;
                }
            }

            var dataOutput = BuildDataOutput(extractedValues, totalExtractionName);

            var output = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["data"] = dataOutput
            };

            if (totalExtractionName is not null &&
                extractedValues.TryGetValue(totalExtractionName, out var totalElement) &&
                totalElement.ValueKind == JsonValueKind.Number)
            {
                output["total"] = totalElement;
            }

            context.Logger.LogInformation(
                "JsonExtractBlock: Extracted {Count} configured fields",
                extractedValues.Count);

            return Task.FromResult(BlockResult.Succeeded(JsonSerializer.SerializeToElement(output)));
        }
        catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(BlockResult.Failed("JsonExtract evaluation timed out after 5 seconds."));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(BlockResult.Failed($"JsonExtract block could not parse JSON: {ex.Message}"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(BlockResult.Failed($"JsonExtract failed: {ex.Message}"));
        }
    }

    private static string? ResolveJsonPayload(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return input.GetString();

        if (input.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            if (input.ValueKind == JsonValueKind.Object)
            {
                if (TryResolveNestedJson(input, "body", out var body))
                    return body;

                if (TryResolveNestedJson(input, "json", out var json))
                    return json;
            }

            return input.GetRawText();
        }

        return null;
    }

    private static bool TryResolveNestedJson(JsonElement parent, string propertyName, out string? value)
    {
        value = null;

        if (!parent.TryGetProperty(propertyName, out var property))
            return false;

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => property.GetRawText(),
            _ => null
        };

        return value is not null;
    }

    private static string? ValidateJsonPath(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
            return "path cannot be empty.";

        if (jsonPath.Length > MaxJsonPathLength)
            return $"path exceeds the {MaxJsonPathLength} character limit.";

        if (jsonPath.Contains("..", StringComparison.Ordinal) ||
            jsonPath.Contains("?(", StringComparison.Ordinal) ||
            jsonPath.Contains('@', StringComparison.Ordinal))
        {
            return "path contains blocked JSONPath operators.";
        }

        return SafeJsonPathRegex.IsMatch(jsonPath)
            ? null
            : "path is outside the supported JSONPath subset.";
    }

    private static JsonElement EvaluateSimpleJsonPath(
        JsonElement root,
        string path,
        CancellationToken cancellationToken)
    {
        var tokens = ParsePath(path);
        var current = new List<JsonElement> { root };
        var producesMany = false;

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var next = new List<JsonElement>();

            switch (token.Kind)
            {
                case PathTokenKind.Field:
                    foreach (var node in current)
                    {
                        if (node.ValueKind == JsonValueKind.Object &&
                            node.TryGetProperty(token.FieldName!, out var property))
                        {
                            next.Add(property);
                        }
                    }
                    break;

                case PathTokenKind.Index:
                    foreach (var node in current)
                    {
                        if (TryGetArrayElement(node, token.Start, out var item))
                            next.Add(item);
                    }
                    break;

                case PathTokenKind.Wildcard:
                    producesMany = true;
                    foreach (var node in current)
                    {
                        if (node.ValueKind != JsonValueKind.Array)
                            continue;

                        foreach (var item in node.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            next.Add(item);
                            if (next.Count > MaxArrayItems)
                                throw new InvalidOperationException(
                                    $"JsonExtract exceeded the maximum of {MaxArrayItems} extracted array items.");
                        }
                    }
                    break;

                case PathTokenKind.Slice:
                    producesMany = true;
                    foreach (var node in current)
                    {
                        if (node.ValueKind != JsonValueKind.Array)
                            continue;

                        var index = 0;
                        foreach (var item in node.EnumerateArray())
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (index >= token.Start && index < token.End)
                            {
                                next.Add(item);
                                if (next.Count > MaxArrayItems)
                                    throw new InvalidOperationException(
                                        $"JsonExtract exceeded the maximum of {MaxArrayItems} extracted array items.");
                            }

                            index++;
                            if (index >= token.End)
                                break;
                        }
                    }
                    break;
            }

            current = next;
        }

        if (current.Count == 0)
            return CreateNullElement();

        if (!producesMany && current.Count == 1)
            return current[0].Clone();

        var results = current.Select(item => item.Clone()).ToArray();
        return JsonSerializer.SerializeToElement(results);
    }

    private static IReadOnlyList<PathToken> ParsePath(string path)
    {
        var tokens = new List<PathToken>();
        var index = 1; // Skip '$'

        while (index < path.Length)
        {
            if (path[index] == '.')
            {
                index++;
                var start = index;
                while (index < path.Length &&
                       (char.IsLetterOrDigit(path[index]) || path[index] == '_'))
                {
                    index++;
                }

                tokens.Add(PathToken.Field(path[start..index]));
                continue;
            }

            if (path[index] == '[')
            {
                var closeIndex = path.IndexOf(']', index);
                var content = path[(index + 1)..closeIndex];

                if (content == "*")
                {
                    tokens.Add(PathToken.Wildcard());
                }
                else if (content.Contains(':', StringComparison.Ordinal))
                {
                    var parts = content.Split(':', 2);
                    tokens.Add(PathToken.Slice(int.Parse(parts[0]), int.Parse(parts[1])));
                }
                else
                {
                    tokens.Add(PathToken.Index(int.Parse(content)));
                }

                index = closeIndex + 1;
                continue;
            }

            throw new InvalidOperationException($"Unsupported JSONPath token near '{path[index..]}'");
        }

        return tokens;
    }

    private static bool TryGetArrayElement(JsonElement node, int index, out JsonElement element)
    {
        element = default;
        if (node.ValueKind != JsonValueKind.Array || index < 0)
            return false;

        var currentIndex = 0;
        foreach (var item in node.EnumerateArray())
        {
            if (currentIndex == index)
            {
                element = item;
                return true;
            }

            currentIndex++;
        }

        return false;
    }

    private static JsonElement NormalizeExtractionValue(JsonElement value, string type) =>
        type.ToLowerInvariant() switch
        {
            "array" => NormalizeArrayValue(value),
            "number" => NormalizeNumberValue(value),
            _ => value.Clone()
        };

    private static JsonElement NormalizeArrayValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
            return JsonSerializer.SerializeToElement(Array.Empty<object>());

        if (value.ValueKind == JsonValueKind.Array)
            return value.Clone();

        return JsonSerializer.SerializeToElement(new[] { value.Clone() });
    }

    private static JsonElement NormalizeNumberValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
            return value.Clone();

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), out var parsed))
        {
            return JsonSerializer.SerializeToElement(parsed);
        }

        return CreateNullElement();
    }

    private static JsonElement BuildDataOutput(
        IReadOnlyDictionary<string, JsonElement> extractedValues,
        string? totalExtractionName)
    {
        if (extractedValues.TryGetValue("items", out var items))
            return items.Clone();

        if (extractedValues.TryGetValue("data", out var data))
            return data.Clone();

        var dataFields = extractedValues
            .Where(kvp => !string.Equals(kvp.Key, totalExtractionName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (dataFields.Count == 1)
            return dataFields[0].Value.Clone();

        var composite = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in dataFields)
            composite[name] = value.Clone();

        return JsonSerializer.SerializeToElement(composite);
    }

    private static JsonElement StripHtmlFields(JsonElement element, ISet<string> stripHtmlFields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteSanitizedElement(element, writer, stripHtmlFields, currentPropertyName: null);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteSanitizedElement(
        JsonElement element,
        Utf8JsonWriter writer,
        ISet<string> stripHtmlFields,
        string? currentPropertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteSanitizedElement(property.Value, writer, stripHtmlFields, property.Name);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteSanitizedElement(item, writer, stripHtmlFields, currentPropertyName);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String when currentPropertyName is not null &&
                                            stripHtmlFields.Contains(currentPropertyName):
                writer.WriteStringValue(StripHtml(element.GetString()));
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static string StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var withoutTags = HtmlTagRegex.Replace(value, " ");
        return WhitespaceRegex.Replace(withoutTags, " ").Trim();
    }

    private static JsonElement CreateNullElement() =>
        JsonSerializer.SerializeToElement<string?>(null);

    private static JsonExtractConfig ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return JsonExtractConfig.Empty;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return JsonExtractConfig.Empty;

        var extractions = new List<ExtractionRule>();
        if (config.TryGetProperty("extractions", out var extractionsElement) &&
            extractionsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var extraction in extractionsElement.EnumerateArray())
            {
                if (extraction.ValueKind != JsonValueKind.Object)
                    continue;

                if (!extraction.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String ||
                    !extraction.TryGetProperty("jsonpath", out var pathElement) ||
                    pathElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var type = extraction.TryGetProperty("type", out var typeElement) &&
                           typeElement.ValueKind == JsonValueKind.String
                    ? typeElement.GetString() ?? "value"
                    : "value";

                extractions.Add(new ExtractionRule(
                    nameElement.GetString()!,
                    pathElement.GetString()!,
                    type));
            }
        }

        var stripHtmlFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (config.TryGetProperty("stripHtmlFields", out var stripHtmlFieldsElement) &&
            stripHtmlFieldsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var field in stripHtmlFieldsElement.EnumerateArray())
            {
                if (field.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(field.GetString()))
                {
                    stripHtmlFields.Add(field.GetString()!);
                }
            }
        }

        return new JsonExtractConfig(extractions, stripHtmlFields);
    }

    private sealed record JsonExtractConfig(
        IReadOnlyList<ExtractionRule> Extractions,
        ISet<string> StripHtmlFields)
    {
        public static JsonExtractConfig Empty { get; } =
            new([], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed record ExtractionRule(string Name, string JsonPath, string Type);

    private enum PathTokenKind
    {
        Field,
        Index,
        Wildcard,
        Slice
    }

    private sealed record PathToken(PathTokenKind Kind, string? FieldName, int Start, int End)
    {
        public static PathToken Field(string fieldName) => new(PathTokenKind.Field, fieldName, 0, 0);
        public static PathToken Index(int index) => new(PathTokenKind.Index, null, index, index);
        public static PathToken Wildcard() => new(PathTokenKind.Wildcard, null, 0, 0);
        public static PathToken Slice(int start, int end) => new(PathTokenKind.Slice, null, start, end);
    }
}
