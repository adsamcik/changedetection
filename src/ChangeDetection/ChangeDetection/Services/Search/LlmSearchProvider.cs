using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Search provider that uses the LLM to suggest URLs, then validates each one via HTTP HEAD.
/// Always available (only needs the LLM chain, which is required infrastructure).
/// 
/// This is the fallback when no dedicated search engine (SearXNG, Brave, Google) is configured.
/// The LLM may hallucinate URLs, so every suggestion is verified before being returned.
/// Unverified/404 URLs are silently dropped — only confirmed-live results are returned.
/// </summary>
public class LlmSearchProvider(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    IUrlValidator urlValidator,
    ILogger<LlmSearchProvider> logger) : ISearchProvider
{
    private const int MaxSuggestions = 15;
    private const int ValidationTimeoutMs = 4000;
    private const int MaxConcurrentValidations = 8;
    private const int TotalValidationBudgetMs = 15000;

    public string ProviderId => "llm";
    public string DisplayName => "AI-Assisted Discovery";

    // Always available — CopilotSDK is required infrastructure
    public bool IsAvailable => true;

    public async Task<SearchResultSet> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        List<LlmSuggestion> suggestions;
        try
        {
            suggestions = await GetLlmSuggestionsAsync(query.Query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LLM search suggestion failed for query: {Query}", query.Query);
            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = false,
                ErrorMessage = $"LLM suggestion failed: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (suggestions.Count == 0)
        {
            return new SearchResultSet
            {
                ProviderId = ProviderId,
                Query = query.Query,
                Results = [],
                IsSuccess = true,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        // Validate every URL — drop hallucinated/dead ones
        var verified = await ValidateUrlsAsync(suggestions, ct);

        logger.LogInformation(
            "LLM search: {Suggested} suggestions, {Verified} verified live for query: {Query}",
            suggestions.Count, verified.Count, query.Query);

        var results = verified
            .Take(query.MaxResults)
            .Select((s, i) => new SearchResult
            {
                Url = s.Url,
                Title = s.Title ?? ExtractDomainName(s.Url),
                Snippet = s.Reasoning,
                Engine = "llm",
                Position = i + 1
            })
            .ToList();

        return new SearchResultSet
        {
            ProviderId = ProviderId,
            Query = query.Query,
            Results = results,
            IsSuccess = true,
            DurationMs = sw.ElapsedMilliseconds,
            TotalResults = results.Count
        };
    }

    private async Task<List<LlmSuggestion>> GetLlmSuggestionsAsync(string query, CancellationToken ct)
    {
        var prompt = $$"""
            Given this search query, suggest real websites that would match.

            Query: "{{query}}"

            Suggest up to {{MaxSuggestions}} URLs of real, existing websites that match this query.
            
            Prioritize these types of sources (in order):
            1. **Company career pages** — direct employer career/jobs pages (e.g. https://careers.novonordisk.com, https://jobs.takeda.com)
            2. **Major job boards** — StepStone, Indeed, LinkedIn Jobs, Glassdoor, Monster, CareerBuilder, ZipRecruiter
            3. **ATS platforms** — Workday, Greenhouse, Lever, SmartRecruiters, iCIMS career portals
            4. **Regional/country-specific job boards** — based on the query's geographic context:
               - Czech Republic: Jobs.cz, Prace.cz, StartupJobs.cz
               - Germany: StepStone.de, Xing.com, Arbeitsagentur.de
               - Denmark: Jobindex.dk, AcademicPositions.dk, JobsinCopenhagen.com
               - Netherlands: Academictransfer.com, Indeed.nl, NationaleVacaturebank.nl
               - Austria: Karriere.at, StepStone.at, AMS.at
               - Belgium: StepStone.be, VDAB.be, Jobat.be
               - Switzerland: Jobs.ch, SwissDevJobs.ch
               - Scandinavia: Finn.no, AcademicPositions.com, NordicJobBoard.com
               - France: Apec.fr, Indeed.fr, PoleEmploi.fr
            5. **Specialized portals** — NatureJobs, Science Careers, BioSpace, MedJobsCafe, LifeSciencesHub
            6. **University/research vacancy pages** — direct links to university job boards
            7. **Government employment agencies** — national job services with vacancy listings

            For each suggestion, provide:
            - url: the EXACT full URL (https://...) of the relevant SEARCH RESULTS page or job listings page
              (not just the homepage — include search parameters if possible, e.g. ?q=scientist&location=copenhagen)
            - title: the page/site name
            - reasoning: one sentence explaining why this matches the query

            Return ONLY a JSON array:
            [{"url":"https://...","title":"...","reasoning":"..."}]
            
            IMPORTANT: Only suggest URLs you are highly confident actually exist as real websites.
            It is better to return fewer high-confidence suggestions than many uncertain ones.
            """;

        await using var scope = scopeFactory.CreateAsyncScope();
        var llmChain = scope.ServiceProvider.GetRequiredService<ILlmProviderChain>();

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.2f,
            MaxTokens = 1500,
            UsageType = LlmUsageType.ContentAnalysis
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
            return [];

        var content = response.Content.Trim();
        // Strip markdown fences if present
        if (content.StartsWith("```"))
        {
            var firstNl = content.IndexOf('\n');
            if (firstNl > 0) content = content[(firstNl + 1)..];
        }
        if (content.EndsWith("```")) content = content[..^3].TrimEnd();

        try
        {
            var suggestions = JsonSerializer.Deserialize<List<LlmSuggestion>>(content, JsonOptions);
            return suggestions?
                .Where(s => !string.IsNullOrWhiteSpace(s.Url) && Uri.TryCreate(s.Url, UriKind.Absolute, out var uri) && 
                            (uri.Scheme == "https" || uri.Scheme == "http"))
                .Take(MaxSuggestions)
                .ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Failed to parse LLM search suggestions");
            return [];
        }
    }

    private async Task<List<LlmSuggestion>> ValidateUrlsAsync(
        List<LlmSuggestion> suggestions, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient("LlmSearchValidation");

        using var semaphore = new SemaphoreSlim(MaxConcurrentValidations);
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(TotalValidationBudgetMs);

        var pending = suggestions.Select(suggestion => ValidateSuggestionAsync(
                client,
                suggestion,
                semaphore,
                budgetCts.Token,
                ct))
            .ToList();

        var budgetTask = Task.Delay(Timeout.InfiniteTimeSpan, budgetCts.Token);
        var verified = new List<LlmSuggestion>();
        var validatedCount = 0;
        var timedOutCount = 0;

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending.Cast<Task>().Append(budgetTask));
            if (completed == budgetTask)
            {
                ct.ThrowIfCancellationRequested();
                timedOutCount += pending.Count;
                logger.LogInformation(
                    "LLM suggestion validation budget hit after {BudgetMs}ms: {Validated} validated, {TimedOut} timed out, {Verified} verified live",
                    TotalValidationBudgetMs,
                    validatedCount,
                    timedOutCount,
                    verified.Count);
                break;
            }

            var completedTask = (Task<ValidationAttempt>)completed;
            pending.Remove(completedTask);

            var attempt = await completedTask;
            if (attempt.Status == ValidationStatus.TimedOut)
            {
                timedOutCount++;
            }
            else
            {
                validatedCount++;
                if (attempt.Status == ValidationStatus.Live)
                    verified.Add(attempt.Suggestion);
            }
        }

        if (pending.Count == 0)
        {
            logger.LogInformation(
                "LLM suggestion validation completed within budget: {Validated} validated, {TimedOut} timed out, {Verified} verified live",
                validatedCount,
                timedOutCount,
                verified.Count);
        }

        return verified;
    }

    private async Task<ValidationAttempt> ValidateSuggestionAsync(
        HttpClient client,
        LlmSuggestion suggestion,
        SemaphoreSlim semaphore,
        CancellationToken validationCt,
        CancellationToken callerCt)
    {
        var lockTaken = false;
        try
        {
            await semaphore.WaitAsync(validationCt);
            lockTaken = true;

            var status = await ValidateSingleUrlAsync(client, suggestion, validationCt, callerCt);
            return new ValidationAttempt(suggestion, status);
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            return new ValidationAttempt(suggestion, ValidationStatus.TimedOut);
        }
        finally
        {
            if (lockTaken)
                semaphore.Release();
        }
    }

    private async Task<ValidationStatus> ValidateSingleUrlAsync(
        HttpClient client,
        LlmSuggestion suggestion,
        CancellationToken validationCt,
        CancellationToken callerCt)
    {
        // SSRF protection: block private/internal URLs before making HTTP requests
        var ssrfError = urlValidator.Validate(suggestion.Url);
        if (ssrfError is not null)
        {
            logger.LogDebug("LLM suggestion URL blocked by SSRF validator: {Url} — {Error}", suggestion.Url, ssrfError);
            return ValidationStatus.NotLive;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(validationCt);
            cts.CancelAfter(ValidationTimeoutMs);

            using var request = new HttpRequestMessage(HttpMethod.Head, suggestion.Url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; ChangeDetection/1.0)");

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // Accept 2xx and 3xx (redirects to real pages are fine)
            var isLive = (int)response.StatusCode < 400;

            if (!isLive)
            {
                // Some servers reject HEAD — try GET as fallback
                using var getRequest = new HttpRequestMessage(HttpMethod.Get, suggestion.Url);
                getRequest.Headers.Add("User-Agent", "Mozilla/5.0 (compatible; ChangeDetection/1.0)");
                getRequest.Headers.Add("Range", "bytes=0-0"); // Minimize data transfer
                
                using var getResponse = await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                isLive = (int)getResponse.StatusCode < 400;
            }

            if (!isLive)
                logger.LogDebug("LLM suggestion URL not live ({Status}): {Url}", response.StatusCode, suggestion.Url);

            return isLive ? ValidationStatus.Live : ValidationStatus.NotLive;
        }
        catch (OperationCanceledException) when (!callerCt.IsCancellationRequested)
        {
            logger.LogDebug("LLM suggestion URL validation timed out: {Url}", suggestion.Url);
            return ValidationStatus.TimedOut;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("LLM suggestion URL validation failed: {Url} — {Error}", suggestion.Url, ex.Message);
            return ValidationStatus.NotLive;
        }
    }

    private static string ExtractDomainName(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
        }
    }

    private sealed record LlmSuggestion
    {
        [JsonPropertyName("url")]
        public string Url { get; init; } = "";

        [JsonPropertyName("title")]
        public string? Title { get; init; }

        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; init; }
    }

    private sealed record ValidationAttempt(LlmSuggestion Suggestion, ValidationStatus Status);

    private enum ValidationStatus
    {
        NotLive,
        Live,
        TimedOut
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
