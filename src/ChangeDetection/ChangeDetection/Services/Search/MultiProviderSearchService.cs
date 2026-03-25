using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Runs the same search query across multiple configured providers,
/// merges results, and deduplicates by URL (keeping the highest-ranked occurrence).
/// Foundation for adversarial comparison across search engines.
/// </summary>
public class MultiProviderSearchService(
    IEnumerable<ISearchProvider> providers,
    ILogger<MultiProviderSearchService> logger)
{
    /// <summary>
    /// Returns true if at least one search provider is configured and available.
    /// </summary>
    public bool HasAvailableProviders() => providers.Any(CanExecuteProvider);

    /// <summary>
    /// Searches across all available providers (or specified subset) and merges results.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="providerIds">Optional subset of provider IDs. Null = all available.</param>
    /// <param name="excludeProviderIds">Optional provider IDs to exclude from execution.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<MultiProviderResultSet> SearchAllAsync(
        SearchQuery query,
        IReadOnlyList<string>? providerIds = null,
        IReadOnlyList<string>? excludeProviderIds = null,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var targetProviders = providers
            .Where(CanExecuteProvider)
            .Where(p => providerIds is null || providerIds.Contains(p.ProviderId))
            .Where(p => excludeProviderIds is null || !excludeProviderIds.Contains(p.ProviderId))
            .ToList();

        if (targetProviders.Count == 0)
        {
            return new MultiProviderResultSet
            {
                Query = query.Query,
                ProviderResults = [],
                MergedResults = [],
                DurationMs = 0
            };
        }

        // Execute all providers in parallel
        var tasks = targetProviders.Select(p => ExecuteProviderAsync(p, query, ct));
        var results = await Task.WhenAll(tasks);

        sw.Stop();

        var providerResults = results
            .Where(r => r is not null)
            .ToList()!;

        var merged = MergeAndDeduplicate(providerResults!);

        logger.LogDebug(
            "Multi-provider search across {Count} providers returned {Total} results ({Unique} unique) in {Duration}ms",
            targetProviders.Count, providerResults.Sum(r => r!.Results.Count), merged.Count, sw.ElapsedMilliseconds);

        return new MultiProviderResultSet
        {
            Query = query.Query,
            ProviderResults = providerResults!,
            MergedResults = merged,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private async Task<SearchResultSet?> ExecuteProviderAsync(
        ISearchProvider provider, SearchQuery query, CancellationToken ct)
    {
        try
        {
            return await provider.SearchAsync(query, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Provider {Id} failed for query: {Query}", provider.ProviderId, query.Query);
            return null;
        }
    }

    private static bool CanExecuteProvider(ISearchProvider provider) =>
        provider.IsAvailable ||
        provider is CopilotSearchProvider { CanInitialize: true };

    /// <summary>
    /// Merges results from multiple providers, deduplicating by URL.
    /// Keeps the highest-ranked (lowest position) occurrence of each URL.
    /// Results are ordered by: number of providers that returned it (desc), then best position (asc).
    /// </summary>
    internal static IReadOnlyList<MergedSearchResult> MergeAndDeduplicate(
        IReadOnlyList<SearchResultSet> providerResults)
    {
        var urlMap = new Dictionary<string, MergedSearchResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var resultSet in providerResults)
        {
            if (!resultSet.IsSuccess) continue;

            foreach (var result in resultSet.Results)
            {
                if (urlMap.TryGetValue(result.Url, out var existing))
                {
                    existing.ProviderIds.Add(resultSet.ProviderId);
                    if (result.Position < existing.BestPosition)
                    {
                        existing.BestPosition = result.Position;
                        existing.Title = result.Title;
                        existing.Snippet = result.Snippet;
                    }
                }
                else
                {
                    urlMap[result.Url] = new MergedSearchResult
                    {
                        Url = result.Url,
                        Title = result.Title,
                        Snippet = result.Snippet,
                        BestPosition = result.Position,
                        ProviderIds = [resultSet.ProviderId],
                        PublishedDate = result.PublishedDate,
                        Category = result.Category
                    };
                }
            }
        }

        return urlMap.Values
            .OrderByDescending(r => r.ProviderIds.Count)
            .ThenBy(r => r.BestPosition)
            .ToList();
    }
}

/// <summary>Results from a multi-provider search with per-provider and merged views.</summary>
public record MultiProviderResultSet
{
    public required string Query { get; init; }
    public required IReadOnlyList<SearchResultSet> ProviderResults { get; init; }
    public required IReadOnlyList<MergedSearchResult> MergedResults { get; init; }
    public long DurationMs { get; init; }
}

/// <summary>A search result merged across multiple providers.</summary>
public class MergedSearchResult
{
    public required string Url { get; set; }
    public required string Title { get; set; }
    public string? Snippet { get; set; }
    public int BestPosition { get; set; }
    public List<string> ProviderIds { get; set; } = [];
    public DateTime? PublishedDate { get; set; }
    public string? Category { get; set; }

    /// <summary>Number of providers that returned this URL.</summary>
    public int ProviderCount => ProviderIds.Count;
}
