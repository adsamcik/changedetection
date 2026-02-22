using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Comparison;

/// <summary>
/// Pipeline block that extracts SERP ranking data from search results.
/// Produces a position-focused diffable text format so downstream TextDiff
/// can detect rank movements: "URL moved from position 3 to position 7".
///
/// Output format (diffable, sorted by position):
///   #1  https://example.com/page-a  "Page A Title"
///   #2  https://example.com/page-b  "Page B Title"
///   #3  https://example.com/page-c  "Page C Title"
///
/// When this output is compared over time via TextDiff, rank changes, new entries,
/// and dropped results all surface as standard line diffs.
/// </summary>
public class RankingSnapshotBlock : IPipelineBlock
{
    public string BlockType => "RankingSnapshot";

    public IReadOnlyList<PortDescriptor> InputPorts =>
    [
        new PortDescriptor
        {
            Name = "searchResults",
            Type = PortType.SearchResults,
            Description = "Search results from SearchBlock"
        }
    ];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
    [
        new PortDescriptor
        {
            Name = "rankingText",
            Type = PortType.PlainText,
            Description = "Position-sorted ranking snapshot for diffing"
        },
        new PortDescriptor
        {
            Name = "rankingData",
            Type = PortType.SearchResults,
            Description = "Structured ranking data as JSON"
        }
    ];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("searchResults", out var input))
            return Task.FromResult(BlockResult.Failed("Missing required input: searchResults"));

        try
        {
            var results = ParseSearchResults(input);

            if (results.Count == 0)
                return Task.FromResult(BlockResult.Failed("No search results to rank"));

            var rankingText = BuildRankingText(results);
            var rankingData = BuildRankingData(results);

            var output = JsonSerializer.SerializeToElement(new
            {
                rankingText,
                rankingData,
                resultCount = results.Count,
                topUrl = results.Count > 0 ? results[0].Url : null
            });

            return Task.FromResult(BlockResult.Succeeded(output));
        }
        catch (Exception ex)
        {
            return Task.FromResult(BlockResult.Failed($"Failed to extract rankings: {ex.Message}"));
        }
    }

    /// <summary>
    /// Builds a position-sorted diffable text representation.
    /// Format: "#N  URL  "Title""
    /// Sorted by position (ascending) for stable, meaningful diffs.
    /// </summary>
    internal static string BuildRankingText(IReadOnlyList<RankedResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results.OrderBy(r => r.Position))
        {
            sb.AppendLine($"#{r.Position,-3} {r.Url}  \"{r.Title}\"");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds structured ranking data for downstream processing.
    /// </summary>
    internal static IReadOnlyList<RankedResult> BuildRankingData(IReadOnlyList<RankedResult> results)
    {
        return results.OrderBy(r => r.Position).ToList();
    }

    internal static IReadOnlyList<RankedResult> ParseSearchResults(JsonElement input)
    {
        var results = new List<RankedResult>();

        // SearchBlock output has "results" array with position/url/title
        if (input.TryGetProperty("results", out var resultsArray) &&
            resultsArray.ValueKind == JsonValueKind.Array)
        {
            var position = 1;
            foreach (var item in resultsArray.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrWhiteSpace(url)) continue;

                var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var snippet = item.TryGetProperty("snippet", out var s) ? s.GetString() : null;
                var engine = item.TryGetProperty("engine", out var e) ? e.GetString() : null;
                var pos = item.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetInt32()
                    : position;

                results.Add(new RankedResult
                {
                    Position = pos,
                    Url = url,
                    Title = title,
                    Snippet = snippet,
                    Engine = engine
                });

                position++;
            }
        }

        return results;
    }

    internal record RankedResult
    {
        public int Position { get; init; }
        public required string Url { get; init; }
        public required string Title { get; init; }
        public string? Snippet { get; init; }
        public string? Engine { get; init; }
    }
}
