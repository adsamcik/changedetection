using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Pipeline;
using Microsoft.Extensions.Logging;

namespace ChangeDetection.Services.SetupPipeline;

public partial class ComposableSetupPipeline
{
    private static SetupProgress Progress(SetupPhase phase, SetupProgressType type, string message, string? detail = null) =>
        new() { Phase = phase, Type = type, Message = message, Detail = detail };

    private static SetupProgress FailedProgress(SetupPhase phase, string message, string? error = null) =>
        new() { Phase = phase, Type = SetupProgressType.Failed, Message = message, Error = error };

    /// <summary>
    /// Strips markdown code fences from LLM responses that wrap JSON in markdown.
    /// </summary>
    private static string StripMarkdownFences(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith('`')) return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline > 0)
            trimmed = trimmed[(firstNewline + 1)..];
        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..^3].TrimEnd();
        return trimmed;
    }

    /// <summary>
    /// Prepares a focused HTML excerpt for the pipeline builder LLM prompt.
    /// Without this, the LLM must guess CSS selectors blind (no DOM visibility) — the #1 cause
    /// of headless pipeline failures.
    ///
    /// Strategy:
    /// 1. Sanitize (strip scripts/styles/injection patterns)
    /// 2. If a recommended selector region exists in the HTML, try to extract that region
    /// 3. Collapse whitespace and strip data attributes to maximize signal density
    /// 4. Truncate to ~8KB — large enough to show repeating item structure, small enough for token budget
    /// </summary>
    private string PrepareHtmlExcerptForPipelineBuilder(string html, string? recommendedSelector)
    {
        const int maxExcerptLength = 8_000;

        if (string.IsNullOrWhiteSpace(html))
            return "<!-- empty page -->";

        // Sanitize to remove script/style/injection — reuse the existing sanitizer
        var sanitized = contentSanitizer.SanitizeHtml(html);
        var clean = sanitized.Content;

        // Collapse runs of whitespace to single spaces (maximizes information density)
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s{2,}", " ",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1));

        // Strip common noise: data-* attributes, inline styles, aria labels
        // (keeps class/id/href which are essential for CSS selectors)
        clean = System.Text.RegularExpressions.Regex.Replace(
            clean, @"\s+data-[a-z0-9-]+=""[^""]*""", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        clean = System.Text.RegularExpressions.Regex.Replace(
            clean, @"\s+style=""[^""]*""", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        if (clean.Length <= maxExcerptLength)
            return clean;

        // Try to find content-rich region: look for the densest area of repeated elements
        // This heuristic finds the region with the most <tr>, <li>, or <article> tags
        var contentStart = FindContentRegionStart(clean, recommendedSelector);
        if (contentStart > 0 && contentStart < clean.Length - 1000)
        {
            // Take from start of content region, show enough to reveal repeating patterns
            var regionExcerpt = clean[contentStart..Math.Min(clean.Length, contentStart + maxExcerptLength)];
            // Include some preceding context for the parent element
            var preContext = clean[Math.Max(0, contentStart - 500)..contentStart];
            var combined = preContext + regionExcerpt;
            if (combined.Length > maxExcerptLength)
                combined = combined[..maxExcerptLength];

            logger.LogDebug(
                "[Headless] HTML excerpt: content region found at offset {Offset}, excerpt {Length} chars",
                contentStart, combined.Length);
            return combined + "\n<!-- truncated -->";
        }

        // Fallback: take from the middle of the page (header/nav is usually at the start,
        // footer at the end — content is in the middle)
        if (clean.Length > maxExcerptLength * 2)
        {
            var midStart = clean.Length / 4; // Start at 25% to skip header/nav
            var excerpt = clean[midStart..Math.Min(clean.Length, midStart + maxExcerptLength)];
            logger.LogDebug(
                "[Headless] HTML excerpt: using mid-page slice from offset {Offset}, {Length} chars",
                midStart, excerpt.Length);
            return excerpt + "\n<!-- truncated -->";
        }

        // Small enough to just truncate from start
        logger.LogDebug("[Headless] HTML excerpt: truncating from start, {Length} chars", maxExcerptLength);
        return clean[..maxExcerptLength] + "\n<!-- truncated -->";
    }

    /// <summary>
    /// Finds the start offset of the main content region in sanitized HTML.
    /// Looks for clusters of repeating elements (table rows, list items, article cards)
    /// which indicate the data-bearing section of the page.
    /// </summary>
    private static int FindContentRegionStart(string html, string? selectorHint)
    {
        // If we have a selector hint, try to find an element matching it
        if (!string.IsNullOrWhiteSpace(selectorHint))
        {
            // Extract tag/class from simple selectors like "table.vacancies", ".job-list", "#results"
            var selectorTarget = ExtractSelectorSearchTerm(selectorHint);
            if (selectorTarget is not null)
            {
                var idx = html.IndexOf(selectorTarget, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                    return Math.Max(0, idx - 100); // Back up a bit to include the opening tag
            }
        }

        // Heuristic: find the first cluster of repeating elements
        // Look for common list/table patterns that indicate data content
        string[] contentMarkers = ["<tbody", "<ul class", "<ol class", "<article", "<main",
            "class=\"results", "class=\"listings", "class=\"jobs", "class=\"items",
            "class=\"search-results", "class=\"vacancies"];

        var bestIndex = -1;
        foreach (var marker in contentMarkers)
        {
            var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (bestIndex < 0 || idx < bestIndex))
                bestIndex = idx;
        }

        return bestIndex >= 0 ? Math.Max(0, bestIndex - 200) : -1;
    }

    /// <summary>
    /// Extracts a search term from a CSS selector for indexOf-based matching.
    /// e.g. "table.vacancies" → "class=\"vacancies", ".job-list" → "class=\"job-list",
    /// "#results" → "id=\"results"
    /// </summary>
    private static string? ExtractSelectorSearchTerm(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return null;

        // Handle class selector: ".foo" or "tag.foo"
        var dotIndex = selector.IndexOf('.');
        if (dotIndex >= 0 && dotIndex < selector.Length - 1)
        {
            var className = selector[(dotIndex + 1)..].Split(' ', ',', '>', '+', '~', '[', ':')[0];
            return $"class=\"{className}";
        }

        // Handle ID selector: "#foo"
        var hashIndex = selector.IndexOf('#');
        if (hashIndex >= 0 && hashIndex < selector.Length - 1)
        {
            var idName = selector[(hashIndex + 1)..].Split(' ', ',', '>', '+', '~', '[', ':')[0];
            return $"id=\"{idName}";
        }

        // Handle bare tag names: "table", "ul", "main"
        var tag = selector.Split(' ', ',', '>', '+', '~', '[', ':')[0].Trim();
        if (tag.Length is > 0 and < 20 && tag.All(char.IsLetterOrDigit))
            return $"<{tag}";

        return null;
    }

    /// <summary>
    /// Extracts a URL from user input text. Returns null if no valid URL found.
    /// Handles both bare URLs and URLs embedded in natural language.
    /// </summary>
    private static string? ExtractUrlFromInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var trimmed = input.Trim();

        // If the entire input is a URL
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var directUri) &&
            (directUri.Scheme == "http" || directUri.Scheme == "https"))
            return trimmed;

        // Extract URL from natural language
        var match = System.Text.RegularExpressions.Regex.Match(
            trimmed, @"https?://[^\s""'<>]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        return match.Success ? match.Value.TrimEnd('.', ',', ')', ']') : null;
    }

    private static (List<RelevanceKeyword> positive, List<RelevanceKeyword> negative) ExtractKeywordsFromIntent(string input)
    {
        var normalizedInput = input ?? string.Empty;
        var lowerInput = normalizedInput.ToLowerInvariant();

        var positive = IntentPositiveKeywordWeights
            .Select(pattern => new RelevanceKeyword(
                pattern.Key,
                lowerInput.Contains(pattern.Key.ToLowerInvariant(), StringComparison.Ordinal)
                    ? pattern.Value + 5
                    : pattern.Value))
            .ToList();

        var negative = IntentNegativeKeywordWeights
            .Select(pattern => new RelevanceKeyword(pattern.Key, pattern.Value))
            .ToList();

        return (positive, negative);
    }

    // ── LLM interaction methods ─────────────────────────────────────────

    private async Task<ParsedIntent> ParseIntentAsync(string userInput, CancellationToken ct)
    {
        var prompt = $$"""
            You are an assistant that parses natural language requests for website monitoring.

            Analyze the following user input and extract their monitoring intent.

            User input: "{{userInput}}"

            Respond with a JSON object with these fields:
            {
              "url": "the URL to monitor (string, required)",
              "intent": "what the user wants to track in one sentence (string, required)",
              "changeType": "one of: price, content, availability, list, structure, any (string, required)",
              "summary": "a friendly summary like 'I will watch X for Y changes' (string)",
              "thresholds": { "key": "value" } or null if no thresholds mentioned,
              "frequency": "how often to check, e.g. '5m', '1h', '30m' or null if not specified (string or null)",
              "notificationPreference": "email, webhook, or null if not specified (string or null)"
            }

            Rules:
            - Extract the URL exactly as the user provided it
            - Infer changeType from context (price drops → "price", new items → "list", etc.)
            - Include thresholds only if the user mentioned specific values (e.g. "below $50")
            - Keep the summary concise and user-friendly

            Respond ONLY with the JSON object, no additional text.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.2f,
            MaxTokens = 512,
            UsageType = LlmUsageType.WatchSetup
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                $"LLM failed to parse intent: {response.ErrorMessage ?? "empty response"}");
        }

        return DeserializeOrThrow<ParsedIntent>(response.Content, "ParsedIntent");
    }

    private async Task<ContentAnalysisResult> AnalyzeContentAsync(
        string html, ParsedIntent intent, CancellationToken ct)
    {
        // Sanitize HTML to strip prompt injection before LLM processing
        var sanitized = contentSanitizer.SanitizeHtml(html);
        var sanitizedHtml = sanitized.Content;

        // Truncate HTML to avoid exceeding token limits
        var truncatedHtml = sanitizedHtml.Length > 15_000
            ? sanitizedHtml[..15_000] + "\n<!-- truncated -->"
            : sanitizedHtml;

        var prompt = $$"""
            You are a web page analyst. Analyze the following HTML page structure for a monitoring system.

            The user wants to monitor this page for: {{intent.Intent}}
            Change type: {{intent.ChangeType}}

            HTML content (may be truncated):
            ```html
            {{truncatedHtml}}
            ```

            Analyze the page and respond with a JSON object:
            {
              "contentType": "one of: product, article, listing, dashboard, forum, documentation, other (string, required)",
              "regions": ["list of identifiable content regions, e.g. 'price section', 'product details', 'comments'"],
              "hasPagination": true/false,
              "needsJavaScript": true/false (whether JS rendering is likely needed for the monitored content),
              "recommendedSelector": "a CSS selector that targets the most relevant content area, or null",
              "pageSummary": "brief description of what this page contains"
            }

            Respond ONLY with the JSON object.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            ExpectJson = true,
            Temperature = 0.2f,
            MaxTokens = 512,
            UsageType = LlmUsageType.ContentAnalysis
        }, ct);

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException(
                $"LLM failed to analyze content: {response.ErrorMessage ?? "empty response"}");
        }

        return DeserializeOrThrow<ContentAnalysisResult>(response.Content, "ContentAnalysisResult");
    }

}
