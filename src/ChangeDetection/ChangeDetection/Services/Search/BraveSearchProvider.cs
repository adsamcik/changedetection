using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Search provider using Brave Web Search API.
/// Brave has its own independent search index (not Google/Bing scraping).
/// Pricing: $5 per 1,000 queries. Free tier: 2,000 queries/month.
/// </summary>
public class BraveSearchProvider(
    HttpClient httpClient,
    IOptions<SearchSettings> settings,
    ILogger<BraveSearchProvider> logger) : ISearchProvider
{
    private const string BaseUrl = "https://api.search.brave.com/res/v1/web/search";

    public string ProviderId => "brave";
    public string DisplayName => "Brave Search";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(settings.Value.BraveApiKey);

    public async Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = "Brave Search is not configured. Set BraveApiKey in SearchSettings."
            };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var requestUrl = BuildRequestUrl(query);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Add("X-Subscription-Token", settings.Value.BraveApiKey);
            request.Headers.Add("Accept", "application/json");

            logger.LogDebug("Brave search: {Query}", query.Query);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(settings.Value.TimeoutSeconds));

            var response = await httpClient.SendAsync(request, cts.Token);
            response.EnsureSuccessStatusCode();

            var braveResponse = await response.Content.ReadFromJsonAsync<BraveSearchResponse>(cts.Token);

            sw.Stop();

            if (braveResponse is null)
            {
                return new SearchResultSet
                {
                    ProviderId = ProviderId,
                    Query = query.Query,
                    Results = [],
                    IsSuccess = false,
                    ErrorMessage = "Empty response from Brave Search",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var results = MapResults(braveResponse, query.MaxResults);

            logger.LogDebug("Brave search returned {Count} results in {Duration}ms",
                results.Count, sw.ElapsedMilliseconds);

            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = results,
                IsSuccess = true,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(ex, "Brave search failed for query: {Query}", query.Query);

            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = $"Brave search failed: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    internal static string BuildRequestUrl(SearchQuery query)
    {
        var url = $"{BaseUrl}?q={Uri.EscapeDataString(query.Query)}";

        var count = Math.Min(query.MaxResults, 20); // Brave max is 20
        url += $"&count={count}";

        if (!string.IsNullOrWhiteSpace(query.Language))
            url += $"&search_lang={Uri.EscapeDataString(query.Language)}";

        if (string.Equals(query.TimeRange, "day", StringComparison.OrdinalIgnoreCase))
            url += "&freshness=pd";
        else if (string.Equals(query.TimeRange, "week", StringComparison.OrdinalIgnoreCase))
            url += "&freshness=pw";
        else if (string.Equals(query.TimeRange, "month", StringComparison.OrdinalIgnoreCase))
            url += "&freshness=pm";
        else if (string.Equals(query.TimeRange, "year", StringComparison.OrdinalIgnoreCase))
            url += "&freshness=py";

        return url;
    }

    internal static IReadOnlyList<SearchResult> MapResults(BraveSearchResponse response, int maxResults)
    {
        if (response.Web?.Results is null or { Count: 0 })
            return [];

        return response.Web.Results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .Take(maxResults)
            .Select((r, i) => new SearchResult
            {
                Url = r.Url!,
                Title = r.Title ?? string.Empty,
                Snippet = r.Description,
                Engine = "brave",
                Position = i + 1,
                PublishedDate = r.Age is not null && DateTime.TryParse(r.Age, out var dt) ? dt : null,
                Category = "general"
            })
            .ToList();
    }

    // Brave Search API response DTOs

    internal sealed class BraveSearchResponse
    {
        [JsonPropertyName("web")]
        public BraveWebResults? Web { get; set; }

        [JsonPropertyName("query")]
        public BraveQueryInfo? Query { get; set; }
    }

    internal sealed class BraveWebResults
    {
        [JsonPropertyName("results")]
        public List<BraveWebResult>? Results { get; set; }
    }

    internal sealed class BraveWebResult
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("age")]
        public string? Age { get; set; }
    }

    internal sealed class BraveQueryInfo
    {
        [JsonPropertyName("original")]
        public string? Original { get; set; }
    }
}
