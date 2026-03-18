using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Comparison;

/// <summary>
/// Scores extracted objects using weighted keyword matching and filters out low-relevance items.
/// </summary>
public class RelevanceScoreBlock : IPipelineBlock
{
    public string BlockType => "RelevanceScore";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "result", Type = PortType.DiffResult }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return Task.FromResult(BlockResult.Failed("RelevanceScore block requires a 'data' input."));

        var (targetFields, positiveKeywords, negativeKeywords, minScore) = ReadConfig(context);
        if (targetFields.Count == 0)
            return Task.FromResult(BlockResult.Failed("RelevanceScore block requires at least one target field."));

        if (dataElement.ValueKind is not JsonValueKind.Array and not JsonValueKind.Object)
            return Task.FromResult(BlockResult.Failed("RelevanceScore block expects a JSON object or array input."));

        var sourceItems = dataElement.ValueKind == JsonValueKind.Array
            ? dataElement.EnumerateArray().Select(x => x.Clone()).ToList()
            : [dataElement.Clone()];

        var scoredItems = new List<JsonElement>();
        var topScore = int.MinValue;

        foreach (var item in sourceItems)
        {
            var searchableText = BuildSearchableText(item, targetFields);
            var score = CalculateScore(searchableText, positiveKeywords, negativeKeywords);
            topScore = Math.Max(topScore, score);

            if (score < minScore)
                continue;

            scoredItems.Add(AddScore(item, score));
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            items = scoredItems,
            totalScored = sourceItems.Count,
            passedFilter = scoredItems.Count,
            topScore = sourceItems.Count > 0 ? topScore : 0,
            scoringConfig = $"{positiveKeywords.Count} positive, {negativeKeywords.Count} negative keywords"
        });

        return Task.FromResult(BlockResult.Succeeded(output));
    }

    private static JsonElement AddScore(JsonElement item, int score)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
            {
                ["value"] = item.Clone(),
                ["relevanceScore"] = JsonSerializer.SerializeToElement(score)
            });
        }

        var dict = new Dictionary<string, JsonElement>();
        foreach (var prop in item.EnumerateObject())
            dict[prop.Name] = prop.Value.Clone();

        dict["relevanceScore"] = JsonSerializer.SerializeToElement(score);
        return JsonSerializer.SerializeToElement(dict);
    }

    private static string BuildSearchableText(JsonElement item, IReadOnlyList<string> targetFields)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return item.ToString().ToLowerInvariant();

        var builder = new StringBuilder();
        foreach (var field in targetFields)
        {
            if (!item.TryGetProperty(field, out var value))
                continue;

            var text = JsonElementToSearchText(value);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (builder.Length > 0)
                builder.Append(' ');

            builder.Append(text);
        }

        return builder.ToString().ToLowerInvariant();
    }

    private static string JsonElementToSearchText(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Array => string.Join(" ", value.EnumerateArray().Select(JsonElementToSearchText)),
            JsonValueKind.Object => string.Join(" ", value.EnumerateObject().Select(x => JsonElementToSearchText(x.Value))),
            _ => string.Empty
        };

    private static int CalculateScore(
        string searchableText,
        IReadOnlyDictionary<string, int> positiveKeywords,
        IReadOnlyDictionary<string, int> negativeKeywords)
    {
        var score = 0;

        foreach (var (keyword, weight) in positiveKeywords)
        {
            if (searchableText.Contains(keyword))
                score += weight;
        }

        foreach (var (keyword, weight) in negativeKeywords)
        {
            if (searchableText.Contains(keyword))
                score += weight;
        }

        return score;
    }

    private static (
        List<string> targetFields,
        Dictionary<string, int> positiveKeywords,
        Dictionary<string, int> negativeKeywords,
        int minScore) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return ([], [], [], 0);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return ([], [], [], 0);

        var targetFields = ReadStringList(config, "targetFields");
        var positiveKeywords = ReadKeywordWeights(config, "positiveKeywords");
        var negativeKeywords = ReadKeywordWeights(config, "negativeKeywords");

        var minScore = 0;
        if (config.TryGetProperty("minScore", out var minScoreElement) && minScoreElement.TryGetInt32(out var configuredMinScore))
            minScore = configuredMinScore;

        return (targetFields, positiveKeywords, negativeKeywords, minScore);
    }

    private static List<string> ReadStringList(JsonElement config, string propertyName)
    {
        var values = new List<string>();
        if (!config.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return values;

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                values.Add(item.GetString()!);
        }

        return values;
    }

    private static Dictionary<string, int> ReadKeywordWeights(JsonElement config, string propertyName)
    {
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!config.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Object)
            return values;

        foreach (var keyword in property.EnumerateObject())
        {
            if (keyword.Value.TryGetInt32(out var weight) && !string.IsNullOrWhiteSpace(keyword.Name))
                values[keyword.Name.ToLowerInvariant()] = weight;
        }

        return values;
    }
}
