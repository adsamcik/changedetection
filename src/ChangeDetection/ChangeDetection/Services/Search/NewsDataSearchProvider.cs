using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Search provider using NewsData.io API for news-specific searches.
/// Free tier: 200 credits/day. Paid: $99/month for 30,000 credits.
/// Covers 90,000+ news sources in 80+ languages.
/// </summary>
public class NewsDataSearchProvider(
    HttpClient httpClient,
    IOptions<SearchSettings> settings,
    ILogger<NewsDataSearchProvider> logger) : ISearchProvider
{
    private const string BaseUrl = "https://newsdata.io/api/1/latest";

    public string ProviderId => "newsdata";
    public string DisplayName => "NewsData.io";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(settings.Value.NewsDataApiKey);

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
                ErrorMessage = "NewsData.io is not configured. Set NewsDataApiKey in SearchSettings."
            };
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var requestUrl = BuildRequestUrl(query);
            var response = await httpClient.GetAsync(requestUrl, ct);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync<NewsDataResponse>(cancellationToken: ct);
            sw.Stop();

            if (apiResponse?.Status != "success" || apiResponse.Results is null)
            {
                return new SearchResultSet
                {
                    ProviderId = ProviderId,
                    Query = query.Query,
                    Results = [],
                    IsSuccess = false,
                    ErrorMessage = apiResponse?.Status ?? "Unknown API error",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var results = MapResults(apiResponse.Results, query.MaxResults);

            logger.LogDebug("NewsData.io returned {Count} results for '{Query}' in {Duration}ms",
                results.Count, query.Query, sw.ElapsedMilliseconds);

            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = results,
                IsSuccess = true,
                DurationMs = sw.ElapsedMilliseconds,
                TotalResults = apiResponse.TotalResults
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            logger.LogWarning(ex, "NewsData.io search failed for '{Query}'", query.Query);
            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    internal static string BuildRequestUrl(SearchQuery query)
    {
        var parameters = new List<string>
        {
            $"q={Uri.EscapeDataString(query.Query)}"
        };

        if (!string.IsNullOrWhiteSpace(query.Language))
            parameters.Add($"language={Uri.EscapeDataString(query.Language)}");

        if (!string.IsNullOrWhiteSpace(query.Category) && query.Category != "general")
            parameters.Add($"category={Uri.EscapeDataString(query.Category)}");

        if (!string.IsNullOrWhiteSpace(query.TimeRange))
        {
            var hours = query.TimeRange switch
            {
                "day" => 24,
                "week" => 168,
                "month" => 720,
                _ => (int?)null
            };
            if (hours.HasValue)
                parameters.Add($"timeframe={hours.Value}");
        }

        parameters.Add($"size={Math.Min(query.MaxResults, 50)}");

        return $"{BaseUrl}?{string.Join("&", parameters)}";
    }

    internal static IReadOnlyList<SearchResult> MapResults(
        IReadOnlyList<NewsDataArticle> articles, int maxResults)
    {
        return articles.Take(maxResults).Select((article, index) => new SearchResult
        {
            Url = article.Link ?? "",
            Title = article.Title ?? "Untitled",
            Snippet = article.Description,
            Engine = "newsdata.io",
            Position = index + 1,
            PublishedDate = article.PubDate,
            Category = article.Category?.FirstOrDefault()
        }).Where(r => !string.IsNullOrEmpty(r.Url)).ToList();
    }

    // --- Internal API response DTOs ---

    internal class NewsDataResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("totalResults")]
        public long? TotalResults { get; set; }

        [JsonPropertyName("results")]
        public IReadOnlyList<NewsDataArticle>? Results { get; set; }
    }

    internal class NewsDataArticle
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("pubDate")]
        public DateTime? PubDate { get; set; }

        [JsonPropertyName("category")]
        public List<string>? Category { get; set; }

        [JsonPropertyName("source_name")]
        public string? SourceName { get; set; }

        [JsonPropertyName("language")]
        public string? Language { get; set; }
    }
}
