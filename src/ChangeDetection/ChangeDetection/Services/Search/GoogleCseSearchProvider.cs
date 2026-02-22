using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Search provider using Google Custom Search JSON API.
/// Requires an API key and Custom Search Engine ID configured in SearchSettings.
/// Free tier: 100 queries/day. Paid: $5 per 1,000 queries.
/// </summary>
public class GoogleCseSearchProvider(
    HttpClient httpClient,
    IOptions<SearchSettings> settings,
    ILogger<GoogleCseSearchProvider> logger) : ISearchProvider
{
    private const string BaseUrl = "https://www.googleapis.com/customsearch/v1";

    public string ProviderId => "google-cse";
    public string DisplayName => "Google Custom Search";

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(settings.Value.GoogleCseApiKey) &&
        !string.IsNullOrWhiteSpace(settings.Value.GoogleCseEngineId);

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
                ErrorMessage = "Google CSE is not configured. Set GoogleCseApiKey and GoogleCseEngineId in SearchSettings."
            };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var requestUrl = BuildRequestUrl(
                settings.Value.GoogleCseApiKey!,
                settings.Value.GoogleCseEngineId!,
                query);

            logger.LogDebug("Google CSE search: {Query}", query.Query);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(settings.Value.TimeoutSeconds));

            var response = await httpClient.GetFromJsonAsync<GoogleCseResponse>(
                requestUrl, cts.Token);

            sw.Stop();

            if (response is null)
            {
                return new SearchResultSet
                {
                    ProviderId = ProviderId,
                    Query = query.Query,
                    Results = [],
                    IsSuccess = false,
                    ErrorMessage = "Empty response from Google CSE",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var results = MapResults(response, query.MaxResults);

            logger.LogDebug("Google CSE returned {Count} results in {Duration}ms",
                results.Count, sw.ElapsedMilliseconds);

            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = results,
                IsSuccess = true,
                DurationMs = sw.ElapsedMilliseconds,
                TotalResults = long.TryParse(
                    response.SearchInformation?.TotalResults, out var total) ? total : null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(ex, "Google CSE search failed for query: {Query}", query.Query);

            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = $"Google CSE search failed: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    internal static string BuildRequestUrl(string apiKey, string engineId, SearchQuery query)
    {
        var url = $"{BaseUrl}?key={Uri.EscapeDataString(apiKey)}" +
                  $"&cx={Uri.EscapeDataString(engineId)}" +
                  $"&q={Uri.EscapeDataString(query.Query)}";

        // Google CSE max is 10 per request
        var num = Math.Min(query.MaxResults, 10);
        url += $"&num={num}";

        if (!string.IsNullOrWhiteSpace(query.Language))
            url += $"&lr=lang_{Uri.EscapeDataString(query.Language)}";

        if (string.Equals(query.TimeRange, "day", StringComparison.OrdinalIgnoreCase))
            url += "&dateRestrict=d1";
        else if (string.Equals(query.TimeRange, "week", StringComparison.OrdinalIgnoreCase))
            url += "&dateRestrict=w1";
        else if (string.Equals(query.TimeRange, "month", StringComparison.OrdinalIgnoreCase))
            url += "&dateRestrict=m1";
        else if (string.Equals(query.TimeRange, "year", StringComparison.OrdinalIgnoreCase))
            url += "&dateRestrict=y1";

        return url;
    }

    internal static IReadOnlyList<SearchResult> MapResults(GoogleCseResponse response, int maxResults)
    {
        if (response.Items is null or { Count: 0 })
            return [];

        return response.Items
            .Where(r => !string.IsNullOrWhiteSpace(r.Link))
            .Take(maxResults)
            .Select((r, i) => new SearchResult
            {
                Url = r.Link!,
                Title = r.Title ?? string.Empty,
                Snippet = r.Snippet,
                Engine = "google",
                Position = i + 1,
                Category = "general"
            })
            .ToList();
    }

    // Google CSE JSON response DTOs

    internal sealed class GoogleCseResponse
    {
        [JsonPropertyName("items")]
        public List<GoogleCseItem>? Items { get; set; }

        [JsonPropertyName("searchInformation")]
        public GoogleCseSearchInfo? SearchInformation { get; set; }
    }

    internal sealed class GoogleCseItem
    {
        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }

        [JsonPropertyName("displayLink")]
        public string? DisplayLink { get; set; }
    }

    internal sealed class GoogleCseSearchInfo
    {
        [JsonPropertyName("totalResults")]
        public string? TotalResults { get; set; }

        [JsonPropertyName("searchTime")]
        public double? SearchTime { get; set; }
    }
}
