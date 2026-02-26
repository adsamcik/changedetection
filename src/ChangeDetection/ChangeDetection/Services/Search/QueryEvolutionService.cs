using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.Search;

/// <summary>
/// Uses LLM to analyze search result quality and suggest refined queries.
/// Follows the LLM-only principle — no heuristic fallbacks.
/// </summary>
public class QueryEvolutionService(
    ILlmProviderChain llmChain,
    ILogger<QueryEvolutionService> logger) : IQueryEvolutionService
{
    public async Task<QueryEvolutionResult?> EvolveQueryAsync(
        QueryEvolutionRequest request,
        CancellationToken ct = default)
    {
        if (request.IterationCount >= request.MaxIterations)
        {
            logger.LogDebug("Query evolution reached max iterations ({Max}) for query: {Query}",
                request.MaxIterations, request.OriginalQuery);
            return null;
        }

        if (request.Results.Count == 0)
        {
            return new QueryEvolutionResult
            {
                QualityScore = 0f,
                QualityAssessment = "No results returned — query may be too narrow or provider unavailable.",
                SuggestedQueries = [BuildBroaderQuery(request)],
                ShouldEvolve = true,
                Reasoning = "Zero results indicates the query needs broadening or reformulation."
            };
        }

        var prompt = BuildPrompt(request);
        var options = new LlmRequestOptions
        {
            Temperature = 0.3f,
            MaxTokens = 512,
            ExpectJson = true,
            UsageType = LlmUsageType.ContentAnalysis
        };

        try
        {
            var response = await llmChain.ExecuteAsync(prompt, options, ct);
            if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
            {
                logger.LogWarning("LLM unavailable for query evolution: {Error}", response.ErrorMessage);
                return null;
            }

            return ParseResponse(response.Content, request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Query evolution failed for: {Query}", request.OriginalQuery);
            return null;
        }
    }

    internal static string BuildPrompt(QueryEvolutionRequest request)
    {
        var resultsSummary = string.Join("\n", request.Results.Take(10).Select((r, i) =>
            $"  {i + 1}. \"{r.Title}\" — {r.Url}" +
            (r.Snippet is not null ? $"\n     {r.Snippet[..Math.Min(r.Snippet.Length, 120)]}" : "")));

        return $$"""
            You are a search query optimizer. Analyze these search results and suggest improvements.

            **User's intent:** {{request.UserIntent}}
            **Current query:** {{request.OriginalQuery}}
            **Iteration:** {{request.IterationCount + 1}} of {{request.MaxIterations}}

            **Top results:**
            {{resultsSummary}}

            Evaluate how well these results match the user's intent and suggest refined queries.

            Respond with valid JSON only:
            {
              "qualityScore": 0.0-1.0,
              "qualityAssessment": "brief assessment of result quality",
              "shouldEvolve": true/false,
              "reasoning": "why evolution is or isn't needed",
              "suggestions": [
                {
                  "query": "refined query text",
                  "rationale": "why this query is better",
                  "expectedImprovement": "what should improve",
                  "technique": "add_quotes|site_operator|exclude_terms|add_keywords|narrow_topic|broaden_topic|time_filter"
                }
              ]
            }

            Guidelines:
            - Score > 0.8: results are good, no evolution needed
            - Score 0.5-0.8: results are okay but could improve
            - Score < 0.5: results are poor, evolution recommended
            - Suggest 1-3 refined queries maximum
            - Use search operators: quotes for exact phrases, site: for domains, -term to exclude
            - Keep suggestions practical and specific
            """;
    }

    internal static QueryEvolutionResult? ParseResponse(string content, QueryEvolutionRequest request)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            var qualityScore = root.TryGetProperty("qualityScore", out var qs)
                ? (float)qs.GetDouble() : 0.5f;
            var qualityAssessment = root.TryGetProperty("qualityAssessment", out var qa)
                ? qa.GetString() ?? "No assessment" : "No assessment";
            var shouldEvolve = root.TryGetProperty("shouldEvolve", out var se)
                && se.GetBoolean();
            var reasoning = root.TryGetProperty("reasoning", out var r)
                ? r.GetString() : null;

            var suggestions = new List<SuggestedQuery>();
            if (root.TryGetProperty("suggestions", out var suggestionsArray) &&
                suggestionsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in suggestionsArray.EnumerateArray())
                {
                    var query = item.TryGetProperty("query", out var q) ? q.GetString() : null;
                    var rationale = item.TryGetProperty("rationale", out var rat) ? rat.GetString() : null;
                    if (query is null || rationale is null) continue;

                    suggestions.Add(new SuggestedQuery
                    {
                        Query = query,
                        Rationale = rationale,
                        ExpectedImprovement = item.TryGetProperty("expectedImprovement", out var ei) ? ei.GetString() : null,
                        Technique = item.TryGetProperty("technique", out var t) ? t.GetString() : null
                    });
                }
            }

            return new QueryEvolutionResult
            {
                QualityScore = Math.Clamp(qualityScore, 0f, 1f),
                QualityAssessment = qualityAssessment,
                SuggestedQueries = suggestions,
                ShouldEvolve = shouldEvolve && suggestions.Count > 0,
                Reasoning = reasoning
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SuggestedQuery BuildBroaderQuery(QueryEvolutionRequest request)
    {
        // Remove quotes and operators to broaden
        var broader = request.OriginalQuery
            .Replace("\"", "")
            .Replace("site:", "")
            .Replace("-", " ")
            .Trim();

        return new SuggestedQuery
        {
            Query = broader != request.OriginalQuery ? broader : $"{request.OriginalQuery} overview",
            Rationale = "Broadening the query to get initial results",
            ExpectedImprovement = "Should return at least some results to evaluate",
            Technique = "broaden_topic"
        };
    }
}
