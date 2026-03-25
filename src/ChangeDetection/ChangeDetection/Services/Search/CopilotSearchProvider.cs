using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Interfaces;
using GitHub.Copilot.SDK;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Search provider that uses the GitHub Copilot SDK's native web search capability.
/// The Copilot model can browse the web natively — we simply ask it to search and
/// parse structured results from its response. No custom tool registration needed;
/// the model's built-in search produces grounded, real URLs (not hallucinated).
/// </summary>
public class CopilotSearchProvider : ISearchProvider
{
    private static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan DefaultSearchTimeout = TimeSpan.FromSeconds(TimeoutSeconds);

    private readonly CopilotClient? _client;
    private readonly ILogger<CopilotSearchProvider> _logger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly TimeSpan _startTimeout;
    private readonly TimeSpan _searchTimeout;
    private const int TimeoutSeconds = 60;

    public string ProviderId => "copilot";
    public string DisplayName => "Copilot Web Search";

    /// <summary>Available only after the Copilot client has fully connected.</summary>
    public bool IsAvailable => _client?.State == ConnectionState.Connected;
    internal bool CanInitialize => _client is not null;

    public CopilotSearchProvider(
        CopilotClient? client,
        ILogger<CopilotSearchProvider> logger)
        : this(client, logger, DefaultSearchTimeout, DefaultStartTimeout)
    {
    }

    internal CopilotSearchProvider(
        CopilotClient? client,
        ILogger<CopilotSearchProvider> logger,
        TimeSpan searchTimeout,
        TimeSpan startTimeout)
    {
        _client = client;
        _logger = logger;
        _searchTimeout = searchTimeout;
        _startTimeout = startTimeout;
    }

    private async Task EnsureClientStartedAsync(CancellationToken ct = default)
    {
        if (_client is null) return;
        if (_client.State == ConnectionState.Connected) return;

        await _startLock.WaitAsync(ct);
        try
        {
            if (_client.State == ConnectionState.Connected) return;

            using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            startCts.CancelAfter(_startTimeout);

            try
            {
                await _client.StartAsync(startCts.Token);
            }
            catch (OperationCanceledException) when (startCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Copilot client failed to start within {_startTimeout.TotalSeconds:0} seconds");
            }
        }
        finally
        {
            _startLock.Release();
        }
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
            await EnsureClientStartedAsync(ct);

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
        // Use Copilot's NATIVE web search — the SDK enables all first-party tools
        // by default (equivalent to --allow-all), which includes web_search.
        // We restrict to only web_search to avoid unnecessary tool calls.
        var session = await _client!.CreateSessionAsync(new SessionConfig
        {
            Model = "gpt-5",
            Streaming = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            OnPermissionRequest = PermissionHandler.ApproveAll,
            AvailableTools = ["web_search"]
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
                Search the web for: "{{query.Query}}"
                
                Find real, currently-live career portals and job boards for this search.
                Return results as a JSON array with objects containing "url", "title", and "snippet" fields.
                Return up to {{query.MaxResults}} results. Return ONLY the JSON array, no other text:
                [{"url":"https://...","title":"...","snippet":"..."}]
                """;

            await session.SendAsync(new MessageOptions { Prompt = prompt });

            var parsedResultsTask = completionSource.Task.ContinueWith(
                static task => ParseResponseForResults(task.GetAwaiter().GetResult()),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var (timedOut, results) = await AwaitResultsOrTimeoutAsync(parsedResultsTask, _searchTimeout, ct);
            if (timedOut)
            {
                _logger.LogWarning(
                    "Copilot search timed out after {Timeout}s for query: {Query}",
                    _searchTimeout.TotalSeconds, query.Query);
                return [];
            }

            return results;
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

    internal static async Task<(bool TimedOut, List<T> Results)> AwaitResultsOrTimeoutAsync<T>(
        Task<List<T>> resultsTask,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var completed = await Task.WhenAny(resultsTask, Task.Delay(timeout, ct));
        if (completed != resultsTask)
            return (true, []);

        return (false, await resultsTask);
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

        // Find the JSON array — use bracket matching to find the correct array,
        // not just first '[' and last ']' which breaks on prose like "results [1]..."
        var parsed = TryExtractJsonArray(content);
        if (parsed is null)
            return [];

        return parsed
            .Where(r => !string.IsNullOrWhiteSpace(r.Url) &&
                        Uri.TryCreate(r.Url, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == "https" || uri.Scheme == "http"))
            .ToList();
    }

    private static List<CopilotSearchResult>? TryExtractJsonArray(string content)
    {
        // Try parsing the whole content first (ideal case: pure JSON)
        var list = TryDeserialize(content);
        if (list is not null) return list;

        // Find JSON arrays by bracket matching — try each '[' position
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '[') continue;

            var depth = 0;
            for (var j = i; j < content.Length; j++)
            {
                if (content[j] == '[') depth++;
                else if (content[j] == ']') depth--;

                if (depth != 0) continue;

                var candidate = content[i..(j + 1)];
                list = TryDeserialize(candidate);
                if (list is not null) return list;
                break;
            }
        }

        return null;
    }

    private static List<CopilotSearchResult>? TryDeserialize(string json)
    {
        try
        {
            var results = JsonSerializer.Deserialize<List<CopilotSearchResult>>(json, JsonOptions);
            // Must be non-empty AND contain at least one item with a url — prevents
            // false positives on prose like "[1]" or "[see above]" which parse as JSON arrays
            return results is { Count: > 0 } && results.Any(r => !string.IsNullOrWhiteSpace(r.Url))
                ? results
                : null;
        }
        catch (JsonException)
        {
            return null;
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
