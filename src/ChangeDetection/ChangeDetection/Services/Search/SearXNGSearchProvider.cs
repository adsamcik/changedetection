using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Search provider that queries a SearXNG instance via its JSON API.
/// SearXNG must have JSON format enabled in settings.yml (formats: [html, json]).
/// </summary>
public class SearXNGSearchProvider(
    HttpClient httpClient,
    IOptions<SearchSettings> settings,
    ILogger<SearXNGSearchProvider> logger) : ISearchProvider
{
    public string ProviderId => "searxng";
    public string DisplayName => "SearXNG";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(settings.Value.SearxngUrl);

    public async Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var baseUrl = settings.Value.SearxngUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = "SearXNG URL is not configured. Set SearchSettings:SearxngUrl in settings."
            };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var requestUrl = BuildRequestUrl(baseUrl, query);
            logger.LogDebug("SearXNG search: {Query} → {Url}", query.Query, requestUrl);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(settings.Value.TimeoutSeconds));

            var response = await httpClient.GetFromJsonAsync<SearXNGResponse>(
                requestUrl, JsonOptions, cts.Token);

            sw.Stop();

            if (response is null)
            {
                return new SearchResultSet
                {
                    ProviderId = ProviderId,
                    Query = query.Query,
                    Results = [],
                    IsSuccess = false,
                    ErrorMessage = "Empty response from SearXNG",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var results = MapResults(response, query.MaxResults);

            logger.LogDebug("SearXNG returned {Count} results in {Duration}ms",
                results.Count, sw.ElapsedMilliseconds);

            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = results,
                IsSuccess = true,
                DurationMs = sw.ElapsedMilliseconds,
                TotalResults = response.NumberOfResults
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(ex, "SearXNG search failed for query: {Query}", query.Query);

            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = $"SearXNG search failed: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    internal static string BuildRequestUrl(string baseUrl, SearchQuery query)
    {
        var url = $"{baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query.Query)}&format=json";

        if (!string.IsNullOrWhiteSpace(query.Category))
            url += $"&categories={Uri.EscapeDataString(query.Category)}";

        if (!string.IsNullOrWhiteSpace(query.Language))
            url += $"&language={Uri.EscapeDataString(query.Language)}";

        if (!string.IsNullOrWhiteSpace(query.TimeRange))
            url += $"&time_range={Uri.EscapeDataString(query.TimeRange)}";

        return url;
    }

    internal static IReadOnlyList<SearchResult> MapResults(SearXNGResponse response, int maxResults)
    {
        if (response.Results is null or { Count: 0 })
            return [];

        return response.Results
            .Where(r => !string.IsNullOrWhiteSpace(r.Url))
            .Take(maxResults)
            .Select((r, i) => new SearchResult
            {
                Url = r.Url!,
                Title = r.Title ?? string.Empty,
                Snippet = r.Content,
                Engine = r.Engine,
                Position = i + 1,
                PublishedDate = r.PublishedDate,
                Category = r.Category
            })
            .ToList();
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal sealed class SearXNGResponse
    {
        [JsonPropertyName("results")]
        public List<SearXNGResult>? Results { get; set; }

        [JsonPropertyName("number_of_results")]
        public long? NumberOfResults { get; set; }

        [JsonPropertyName("query")]
        public string? Query { get; set; }
    }

    internal sealed class SearXNGResult
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("engine")]
        public string? Engine { get; set; }

        [JsonPropertyName("publishedDate")]
        public DateTime? PublishedDate { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }
    }
}
