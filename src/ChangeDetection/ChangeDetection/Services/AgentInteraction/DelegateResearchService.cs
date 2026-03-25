using System.Text.Json;
using System.Text.RegularExpressions;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.AgentInteraction;

public interface IDelegateResearchService
{
    Task<ResearchResult> RequestResearchAsync(
        string userIntent,
        string? additionalContext,
        CancellationToken ct = default);
}

public sealed partial class DelegateResearchService(
    ILlmProviderChain llmProviderChain,
    IAskUserService askUserService,
    ILogger<DelegateResearchService> logger) : IDelegateResearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly TimeSpan ResearchPromptTimeout = TimeSpan.FromSeconds(60);

    public async Task<ResearchResult> RequestResearchAsync(
        string userIntent,
        string? additionalContext,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userIntent);

        var prompts = await GenerateResearchPromptsAsync(userIntent, additionalContext, ct);
        if (prompts.Count == 0)
        {
            logger.LogInformation("No delegated research prompts were generated for intent '{Intent}'", userIntent);
            return new ResearchResult();
        }

        var collectedResponses = new List<CollectedResearchResponse>();
        foreach (var prompt in prompts)
        {
            var response = await askUserService.AskOptionalAsync(new AgentQuestion
            {
                Message = $"Paste the results from external research for '{prompt.Title}'.",
                Context = BuildQuestionContext(prompt),
                Input = new ResourceInput(["paste"]),
                Priority = QuestionPriority.Optional
            }, ResearchPromptTimeout, ct);

            if (response is null || response.Skipped || string.IsNullOrWhiteSpace(response.ResourceContent))
                continue;

            collectedResponses.Add(new CollectedResearchResponse(prompt.Title, prompt.Prompt, response.ResourceContent.Trim()));
        }

        if (collectedResponses.Count == 0)
        {
            return new ResearchResult
            {
                GeneratedPrompts = prompts.Select(p => p.Prompt).ToList(),
                Summary = "No delegated research results were provided."
            };
        }

        var summarized = await SummarizeResearchAsync(userIntent, additionalContext, prompts, collectedResponses, ct);
        return summarized with
        {
            GeneratedPrompts = prompts.Select(p => p.Prompt).ToList()
        };
    }

    private async Task<List<DelegatedPrompt>> GenerateResearchPromptsAsync(
        string userIntent,
        string? additionalContext,
        CancellationToken ct)
    {
        var prompt = $$"""
            You create concise prompts that a user can paste into an external AI research tool.

            User intent:
            {{userIntent}}

            Additional context:
            {{additionalContext ?? "None"}}

            Return JSON with this exact shape:
            {
              "prompts": [
                {
                  "title": "short label",
                  "prompt": "full copy-paste prompt for the external AI tool",
                  "expectedFormat": "bullet list or JSON format the user should paste back"
                }
              ]
            }

            Requirements:
            - Create 1 to 2 prompts max.
            - Focus on discovering authoritative URLs relevant to the intent.
            - Ask the external AI to return direct URLs plus one-line reasoning.
            - Prompts must be safe to paste as-is.
            - Keep titles under 6 words.

            Respond ONLY with JSON.
            """;

        var response = await llmProviderChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.2f,
            MaxTokens = 900,
            UsageType = LlmUsageType.WatchSetup,
            PreferLargeModel = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            logger.LogWarning("Failed to generate delegated research prompts: {Error}", response.ErrorMessage ?? "empty response");
            return [];
        }

        var parsed = DeserializeOrThrow<DelegatedPromptEnvelope>(response.Content, nameof(DelegatedPromptEnvelope));
        return (parsed.Prompts ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p.Title) && !string.IsNullOrWhiteSpace(p.Prompt))
            .Take(2)
            .Select(p => p with
            {
                Title = p.Title.Trim(),
                Prompt = p.Prompt.Trim(),
                ExpectedFormat = string.IsNullOrWhiteSpace(p.ExpectedFormat) ? "- https://example.com — why it matters" : p.ExpectedFormat.Trim()
            })
            .ToList();
    }

    private async Task<ResearchResult> SummarizeResearchAsync(
        string userIntent,
        string? additionalContext,
        IReadOnlyList<DelegatedPrompt> prompts,
        IReadOnlyList<CollectedResearchResponse> responses,
        CancellationToken ct)
    {
        var summarizedResponse = await llmProviderChain.ExecuteAsync($$"""
            You are consolidating user-provided research into structured URL candidates.

            User intent:
            {{userIntent}}

            Additional context:
            {{additionalContext ?? "None"}}

            Prompt plan:
            {{JsonSerializer.Serialize(prompts, JsonOptions)}}

            Pasted research:
            {{JsonSerializer.Serialize(responses, JsonOptions)}}

            Return JSON:
            {
              "summary": "one short paragraph",
              "candidates": [
                {
                  "url": "https://...",
                  "title": "human readable title if present",
                  "reasoning": "why this URL is relevant",
                  "source": "which pasted result or prompt it came from"
                }
              ]
            }

            Rules:
            - Extract only direct URLs.
            - Ignore items without a URL.
            - Deduplicate equivalent URLs.
            - Keep reasoning short and specific.
            - Return at most 8 candidates.

            Respond ONLY with JSON.
            """, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.1f,
            MaxTokens = 1200,
            UsageType = LlmUsageType.ContentAnalysis,
            PreferLargeModel = true
        }, ct);

        if (summarizedResponse.IsSuccess && !string.IsNullOrWhiteSpace(summarizedResponse.Content))
        {
            var parsed = DeserializeOrThrow<ResearchSummaryEnvelope>(summarizedResponse.Content, nameof(ResearchSummaryEnvelope));
            var candidates = (parsed.Candidates ?? [])
                .Where(c => Uri.TryCreate(c.Url, UriKind.Absolute, out _))
                .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(8)
                .Select(c => new ResearchCandidate(
                    c.Url.Trim(),
                    string.IsNullOrWhiteSpace(c.Reasoning) ? "Extracted from delegated research." : c.Reasoning.Trim(),
                    string.IsNullOrWhiteSpace(c.Title) ? null : c.Title.Trim(),
                    string.IsNullOrWhiteSpace(c.Source) ? "delegated-research" : c.Source.Trim()))
                .ToList();

            return new ResearchResult
            {
                Candidates = candidates,
                Summary = parsed.Summary?.Trim() ?? $"Summarized {responses.Count} delegated research response(s)."
            };
        }

        logger.LogWarning("Failed to summarize delegated research via LLM, falling back to raw URL extraction");
        var fallbackCandidates = responses
            .SelectMany(response => ExtractUrls(response.Content)
                .Select(url => new ResearchCandidate(url, $"Mentioned in pasted research for '{response.Title}'.", Source: response.Title)))
            .GroupBy(candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(8)
            .ToList();

        return new ResearchResult
        {
            Candidates = fallbackCandidates,
            Summary = fallbackCandidates.Count > 0
                ? $"Extracted {fallbackCandidates.Count} URL candidate(s) from pasted research."
                : "No URLs could be extracted from the pasted research."
        };
    }

    private static string BuildQuestionContext(DelegatedPrompt prompt) => $$"""
        Prompt to paste into your external AI tool:

        {{prompt.Prompt}}

        Paste the raw answer back here when you have it.
        Expected format:
        {{prompt.ExpectedFormat}}
        """;

    private static IEnumerable<string> ExtractUrls(string input)
        => UrlRegex().Matches(input)
            .Select(match => match.Value.Trim().TrimEnd('.', ',', ';', ')', ']'))
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static T DeserializeOrThrow<T>(string json, string typeName)
    {
        try
        {
            var cleaned = StripMarkdownFences(json);
            var result = JsonSerializer.Deserialize<T>(cleaned, JsonOptions);
            return result ?? throw new InvalidOperationException($"{typeName} deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to deserialize {typeName}: {ex.Message}", ex);
        }
    }

    private static string StripMarkdownFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return trimmed.Trim('`');

        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewline)
            return trimmed[(firstNewline + 1)..].Trim();

        return trimmed[(firstNewline + 1)..lastFence].Trim();
    }

    [GeneratedRegex("""https?://[^\s<>"']+""", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UrlRegex();

    private sealed record DelegatedPromptEnvelope
    {
        public List<DelegatedPrompt>? Prompts { get; init; }
    }

    private sealed record DelegatedPrompt(string Title, string Prompt, string? ExpectedFormat);

    private sealed record CollectedResearchResponse(string Title, string Prompt, string Content);

    private sealed record ResearchSummaryEnvelope
    {
        public string? Summary { get; init; }
        public List<ResearchSummaryCandidate>? Candidates { get; init; }
    }

    private sealed record ResearchSummaryCandidate
    {
        public required string Url { get; init; }
        public string? Title { get; init; }
        public string? Reasoning { get; init; }
        public string? Source { get; init; }
    }
}
