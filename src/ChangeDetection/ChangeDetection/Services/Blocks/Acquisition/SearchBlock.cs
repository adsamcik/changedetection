using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Pipeline block that executes a search query and outputs results as structured text.
/// Results are formatted as a stable, diffable document (sorted by URL) so downstream
/// comparison blocks can detect new/removed results without false positives from reordering.
/// </summary>
public class SearchBlock : IPipelineBlock
{
    public string BlockType => "Search";

    public IReadOnlyList<PortDescriptor> InputPorts =>
    [
        new PortDescriptor { Name = "query", Type = PortType.PlainText, Description = "Search query string" }
    ];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
    [
        new PortDescriptor { Name = "results", Type = PortType.SearchResults, Description = "Structured search results" },
        new PortDescriptor { Name = "text", Type = PortType.PlainText, Description = "Diffable text representation of results" }
    ];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        var query = ResolveQuery(context);
        if (string.IsNullOrWhiteSpace(query))
            return BlockResult.Failed("Search block requires a 'query' input or config.");

        var config = ResolveConfig(context);
        var logger = context.Logger;

        var providers = context.Services.GetServices<ISearchProvider>().ToList();
        var provider = ResolveProvider(providers, config.ProviderId);

        if (provider is null)
        {
            var available = string.Join(", ", providers.Select(p => p.ProviderId));
            return BlockResult.Failed(
                $"Search provider '{config.ProviderId ?? "default"}' not found. Available: [{available}]");
        }

        if (!provider.IsAvailable)
            return BlockResult.Failed($"Search provider '{provider.ProviderId}' is not configured or available.");

        logger.LogDebug("SearchBlock executing query '{Query}' via {Provider}",
            query, provider.ProviderId);

        var searchQuery = new SearchQuery
        {
            Query = query,
            MaxResults = config.MaxResults,
            Category = config.Category,
            Language = config.Language,
            TimeRange = config.TimeRange
        };

        var resultSet = await provider.SearchAsync(searchQuery, context.CancellationToken);

        if (!resultSet.IsSuccess)
            return BlockResult.Failed($"Search failed: {resultSet.ErrorMessage}");

        if (resultSet.Results.Count == 0)
        {
            logger.LogDebug("Search returned no results for '{Query}'", query);
            return BlockResult.Succeeded(JsonSerializer.SerializeToElement(new
            {
                results = Array.Empty<object>(),
                text = $"No results found for: {query}",
                query,
                provider = provider.ProviderId,
                resultCount = 0
            }));
        }

        var diffableText = BuildDiffableText(resultSet);

        var output = JsonSerializer.SerializeToElement(new
        {
            results = resultSet.Results.Select(r => new
            {
                r.Url,
                r.Title,
                r.Snippet,
                r.Engine,
                r.Position,
                r.PublishedDate,
                r.Category
            }),
            text = diffableText,
            query,
            provider = provider.ProviderId,
            resultCount = resultSet.Results.Count,
            durationMs = resultSet.DurationMs
        });

        logger.LogDebug("SearchBlock found {Count} results for '{Query}' in {Duration}ms",
            resultSet.Results.Count, query, resultSet.DurationMs);

        return BlockResult.Succeeded(output);
    }

    /// <summary>
    /// Builds a stable text representation of search results, sorted by URL
    /// to eliminate false diffs from result reordering.
    /// </summary>
    internal static string BuildDiffableText(SearchResultSet resultSet)
    {
        var lines = resultSet.Results
            .OrderBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .Select(r => $"[{r.Title}]({r.Url})")
            .ToList();

        return string.Join('\n', lines);
    }

    private static string? ResolveQuery(BlockContext context)
    {
        if (context.Inputs.TryGetValue("query", out var queryElement))
        {
            if (queryElement.ValueKind == JsonValueKind.String)
                return queryElement.GetString();
        }

        if (context.PipelineDefinition is PipelineDefinition pipeline)
        {
            var blockDef = pipeline.Blocks.FirstOrDefault(
                b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

            if (blockDef?.Config is { ValueKind: JsonValueKind.Object } config &&
                config.TryGetProperty("query", out var configQuery) &&
                configQuery.ValueKind == JsonValueKind.String)
            {
                return configQuery.GetString();
            }
        }

        return null;
    }

    private static SearchBlockConfig ResolveConfig(BlockContext context)
    {
        var config = new SearchBlockConfig();

        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return config;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } configElement)
            return config;

        if (configElement.TryGetProperty("provider", out var provider) &&
            provider.ValueKind == JsonValueKind.String)
            config.ProviderId = provider.GetString();

        if (configElement.TryGetProperty("maxResults", out var maxResults) &&
            maxResults.TryGetInt32(out var maxResultsValue))
            config.MaxResults = maxResultsValue;

        if (configElement.TryGetProperty("category", out var category) &&
            category.ValueKind == JsonValueKind.String)
            config.Category = category.GetString();

        if (configElement.TryGetProperty("language", out var language) &&
            language.ValueKind == JsonValueKind.String)
            config.Language = language.GetString();

        if (configElement.TryGetProperty("timeRange", out var timeRange) &&
            timeRange.ValueKind == JsonValueKind.String)
            config.TimeRange = timeRange.GetString();

        return config;
    }

    private static ISearchProvider? ResolveProvider(
        List<ISearchProvider> providers, string? requestedId)
    {
        if (providers.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(requestedId))
            return providers.FirstOrDefault(p =>
                string.Equals(p.ProviderId, requestedId, StringComparison.OrdinalIgnoreCase));

        return providers.FirstOrDefault(p => p.IsAvailable) ?? providers[0];
    }

    private sealed class SearchBlockConfig
    {
        public string? ProviderId { get; set; }
        public int MaxResults { get; set; } = 20;
        public string? Category { get; set; }
        public string? Language { get; set; }
        public string? TimeRange { get; set; }
    }
}
