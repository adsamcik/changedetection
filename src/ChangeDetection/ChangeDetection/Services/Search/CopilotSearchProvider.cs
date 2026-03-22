using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Interfaces;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Search provider that uses the GitHub Copilot SDK with tool calling enabled.
/// Registers a custom "web_search" AIFunction tool so the model can request
/// real search results during the session. When the model invokes the tool,
/// we dispatch to the other configured search providers via
/// <see cref="MultiProviderSearchService"/> (the model then summarises/ranks
/// those results in its final answer).
/// </summary>
public class CopilotSearchProvider : ISearchProvider
{
    private readonly CopilotClient? _client;
    private readonly MultiProviderSearchService _multiProvider;
    private readonly ILogger<CopilotSearchProvider> _logger;
    private readonly Lock _startLock = new();
    private bool _clientStarted;

    public string ProviderId => "copilot";
    public string DisplayName => "Copilot Web Search";

    /// <summary>Only available if a CopilotClient was injected (Copilot is configured).</summary>
    public bool IsAvailable => _client is not null;

    public CopilotSearchProvider(
        CopilotClient? client,
        MultiProviderSearchService multiProvider,
        ILogger<CopilotSearchProvider> logger)
    {
        _client = client;
        _multiProvider = multiProvider;
        _logger = logger;
    }

    private async Task EnsureClientStartedAsync()
    {
        if (_clientStarted || _client is null) return;

        lock (_startLock)
        {
            if (_clientStarted) return;
        }

        await _client.StartAsync();
        _clientStarted = true;
    }

    public async Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        if (_client is null)
        {
            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = "Copilot SDK not configured"
            };
        }

        var sw = Stopwatch.StartNew();

        try
        {
            await EnsureClientStartedAsync();

            var results = await ExecuteSearchSessionAsync(query, ct);

            _logger.LogInformation(
                "Copilot search returned {Count} results for query: {Query} in {Duration}ms",
                results.Count, query.Query, sw.ElapsedMilliseconds);

            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = results
                    .Take(query.MaxResults)
                    .Select((r, i) => new SearchResult
                    {
                        Url = r.Url,
                        Title = r.Title ?? ExtractDomainName(r.Url),
                        Snippet = r.Snippet,
                        Engine = "copilot",
                        Position = i + 1
                    })
                    .ToList(),
                IsSuccess = true,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Copilot search failed for query: {Query}", query.Query);
            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = $"Copilot search failed: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    private async Task<List<CopilotSearchResult>> ExecuteSearchSessionAsync(
        SearchQuery query, CancellationToken ct)
    {
        // Register a web_search tool via the public AIFunction API.
        // When the model decides to search, our delegate executes
        // using the other configured providers (SearXNG, Brave, Google, etc.).
        var toolResults = new List<CopilotSearchResult>();

        var searchFunc = AIFunctionFactory.Create(
            async ([Description("The search query")] string searchQuery) =>
            {
                var multiResult = await _multiProvider.SearchAllAsync(
                    new SearchQuery { Query = searchQuery, MaxResults = 20 },
                    ct: ct);

                foreach (var merged in multiResult.MergedResults.Take(20))
                {
                    toolResults.Add(new CopilotSearchResult
                    {
                        Url = merged.Url,
                        Title = merged.Title,
                        Snippet = merged.Snippet
                    });
                }

                return JsonSerializer.Serialize(
                    toolResults.Select(r => new { r.Url, r.Title, r.Snippet }),
                    JsonOptions);
            },
            "web_search",
            "Search the web for current information. Returns JSON array of results with url, title, and snippet.");

        var session = await _client!.CreateSessionAsync(new SessionConfig
        {
            Model = "gpt-5",
            Streaming = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = PermissionHandler.ApproveAll,
            Tools = [searchFunc]
        });

        var sessionId = session.SessionId;
        try
        {
            var responseContent = "";
            var completionSource = new TaskCompletionSource<string>();

            using var subscription = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        responseContent = msg.Data.Content ?? "";
                        break;

                    case SessionIdleEvent:
                        completionSource.TrySetResult(responseContent);
                        break;

                    case SessionErrorEvent err:
                        completionSource.TrySetException(
                            new InvalidOperationException($"Copilot session error: {err.Data.Message}"));
                        break;
                }
            });

            ct.Register(() => completionSource.TrySetCanceled(ct));

            var prompt = $$"""
                Use the web_search tool to search for: "{{query.Query}}"
                
                After getting search results, return them as a JSON array with objects containing "url", "title", and "snippet" fields.
                Focus on real, currently-live web pages. Return up to {{query.MaxResults}} results.
                
                Return ONLY the JSON array, no other text:
                [{"url":"https://...","title":"...","snippet":"..."}]
                """;

            await session.SendAsync(new MessageOptions { Prompt = prompt });

            var result = await completionSource.Task;

            // If we got results from the tool execution, prefer those (grounded in real search).
            // Otherwise parse the assistant's text response for URLs.
            if (toolResults.Count > 0)
                return toolResults;

            return ParseResponseForResults(result);
        }
        finally
        {
            await session.DisposeAsync();
            try { await _client.DeleteSessionAsync(sessionId); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete Copilot search session {SessionId}", sessionId);
            }
        }
    }

    private static List<CopilotSearchResult> ParseResponseForResults(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return [];

        var content = response.Trim();

        // Strip markdown fences
        if (content.StartsWith("```"))
        {
            var firstNl = content.IndexOf('\n');
            if (firstNl > 0) content = content[(firstNl + 1)..];
        }
        if (content.EndsWith("```")) content = content[..^3].TrimEnd();

        // Find the JSON array in the response
        var arrayStart = content.IndexOf('[');
        var arrayEnd = content.LastIndexOf(']');
        if (arrayStart < 0 || arrayEnd <= arrayStart)
            return [];

        content = content[arrayStart..(arrayEnd + 1)];

        try
        {
            var results = JsonSerializer.Deserialize<List<CopilotSearchResult>>(content, JsonOptions);
            return results?
                .Where(r => !string.IsNullOrWhiteSpace(r.Url) &&
                            Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) &&
                            (uri.Scheme == "https" || uri.Scheme == "http"))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ExtractDomainName(string url)
    {
        try { return new Uri(url).Host; }
        catch { return url; }
    }

    private sealed record CopilotSearchResult
    {
        [JsonPropertyName("url")]
        public string Url { get; init; } = "";

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
