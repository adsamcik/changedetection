using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Stage 4: Uses LLM to generate specific CSS/XPath selectors for the target content.
/// Generates multiple candidates for validation.
/// </summary>
public class SelectorGenerationStage(
    ILlmProviderChain llmChain,
    IDomCompactor domCompactor,
    ILogger<SelectorGenerationStage> logger)
{
    /// <summary>
    /// Generates selector candidates based on content analysis.
    /// </summary>
    public async Task<List<GeneratedSelector>> GenerateSelectorsAsync(
        FetchedContent content,
        ContentAnalysis analysis,
        CancellationToken ct = default)
    {
        logger.LogInformation("Generating selectors for {Url}", content.Url);

        var selectors = new List<GeneratedSelector>();

        // Get selectors from identified sections
        foreach (var section in analysis.IdentifiedSections.Where(s => s.IsLikelyTarget && !string.IsNullOrEmpty(s.SuggestedSelector)))
        {
            selectors.Add(new GeneratedSelector
            {
                Selector = section.SuggestedSelector!,
                Type = DetermineSelectorType(section.SuggestedSelector!),
                Description = section.Description,
                Reasoning = $"Identified as '{section.Name}' section during content analysis",
                Confidence = 0.7f,
                Priority = 1
            });
        }

        // Generate additional candidates using LLM (no heuristic fallbacks)
        var llmSelectors = await GenerateWithLlmAsync(content, analysis, ct);
        selectors.AddRange(llmSelectors);

        // Deduplicate and prioritize
        return DeduplicateAndPrioritize(selectors);
    }

    /// <summary>
    /// Refines selectors based on validation feedback.
    /// </summary>
    public async Task<List<GeneratedSelector>> RefineSelectorsAsync(
        FetchedContent content,
        ContentAnalysis analysis,
        List<SelectorValidation> previousValidations,
        CancellationToken ct = default)
    {
        logger.LogInformation("Refining selectors based on validation feedback");

        var failedSelectors = previousValidations.Where(v => !v.IsValid || v.MatchQuality < 0.5f).ToList();
        var partialSelectors = previousValidations.Where(v => v.IsValid && v.MatchQuality >= 0.3f && v.MatchQuality < 0.7f).ToList();

        if (failedSelectors.Count == 0 && partialSelectors.Count == 0)
        {
            return [];
        }

        // Build compact feedback
        var feedbackBuilder = new System.Text.StringBuilder();
        foreach (var validation in previousValidations.Take(3))
        {
            feedbackBuilder.AppendLine($"- {validation.Selector.Selector}: {(validation.IsValid ? "OK" : "FAILED")}, {validation.MatchCount} matches");
        }

        var htmlSample = TruncateText(content.CleanedHtml ?? content.Html ?? "", 4000);

        // Compact prompt optimized for small models
        var prompt = $$"""
            Fix failed selectors. User wants: {{analysis.UserIntent}}
            Previous: {{feedbackBuilder}}
            HTML: {{htmlSample}}
            
            JSON: [{"selector":"x","type":"CssSelector|XPath","reasoning":"x"}]
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.3f,
            MaxTokens = 300,
            UsageType = LlmUsageType.EntityExtraction,
            ExpectJson = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
            return [];

        try
        {
            var json = CleanJsonResponse(response.Content);
            if (json is null)
            {
                logger.LogWarning("LLM response did not contain valid JSON array for refined selectors: {Response}", TruncateText(response.Content, 200));
                return [];
            }
            
            var dtos = JsonSerializer.Deserialize<List<SelectorDto>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return dtos?.Select((s, i) => new GeneratedSelector
            {
                Selector = s.Selector ?? "",
                Type = ParseSelectorType(s.Type),
                Description = "Refined selector",
                Reasoning = s.Reasoning,
                Confidence = 0.6f,
                Priority = 10 + i
            }).Where(s => !string.IsNullOrEmpty(s.Selector)).ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse refined selectors JSON");
            return [];
        }
    }

    private async Task<List<GeneratedSelector>> GenerateWithLlmAsync(
        FetchedContent content,
        ContentAnalysis analysis,
        CancellationToken ct)
    {
        // Use DomCompactor to reduce HTML size while preserving selector-relevant structure.
        // Scale token budget with page complexity — larger pages need more context for accurate selectors.
        var rawHtml = content.CleanedHtml ?? content.Html ?? "";
        var targetTokens = rawHtml.Length > 20_000 ? 3000 : 2000;
        var compactionResult = domCompactor.CompactToTokenBudget(rawHtml, targetTokens: targetTokens);
        var htmlSample = compactionResult.Html;
        
        logger.LogDebug(
            "Generating selectors with LLM for intent: {Intent}, ContentType: {Type}, HTML: {Original} -> {Compacted} chars ({Ratio:P0})",
            analysis.UserIntent, analysis.ContentType, 
            compactionResult.OriginalSize, compactionResult.CompactedSize, compactionResult.CompressionRatio);

        // More detailed prompt to help LLM understand the task
        var prompt = $$"""
            Generate 2-3 CSS selectors to extract content matching this goal: {{analysis.UserIntent}}
            
            Content type: {{analysis.ContentType}}
            Page title: {{content.Title ?? "Unknown"}}
            
            HTML structure:
            {{htmlSample}}
            
            CRITICAL SELECTOR RULES:
            - Use ONLY simple, standard CSS selectors that work with any CSS parser
            - BEST: Simple tag selectors: h3, a, li, div
            - GOOD: Attribute selectors for complex class names: [class*="shadow"], [class*="bg-white"]
            - GOOD: Simple class selectors without special characters: .shadow, .container
            - AVOID: Class names with colons (lg:, md:, sm:) - these are Tailwind responsive prefixes
            - AVOID: Child combinators (>) with positional selectors (:nth-child)
            - Target the repeating items (e.g., event titles, list items) not the container
            
            EXAMPLES of good selectors:
            - "h3" - selects all headings (good for event titles)
            - "[class*='shadow']" - selects elements containing 'shadow' class
            - "a[href*='seminar']" - selects links containing 'seminar' in URL
            
            Return JSON array: [{"selector":"css selector","type":"CssSelector","description":"what it selects","reasoning":"why this selector"}]
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 400,
            UsageType = LlmUsageType.EntityExtraction,
            ExpectJson = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
        {
            logger.LogWarning("LLM returned no response for selector generation. IsSuccess={Success}, HasContent={HasContent}",
                response.IsSuccess, !string.IsNullOrEmpty(response.Content));
            return [];
        }

        logger.LogDebug("LLM selector response: {Response}", TruncateText(response.Content, 500));

        try
        {
            var json = CleanJsonResponse(response.Content);
            if (json is null)
            {
                logger.LogWarning("LLM response did not contain valid JSON array for selectors: {Response}", TruncateText(response.Content, 200));
                return [];
            }
            
            var dtos = JsonSerializer.Deserialize<List<SelectorDto>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var result = dtos?.Select((s, i) => new GeneratedSelector
            {
                Selector = s.Selector ?? "",
                Type = ParseSelectorType(s.Type),
                Description = s.Description,
                Reasoning = s.Reasoning,
                Confidence = 0.8f - (i * 0.1f),
                Priority = i + 5
            }).Where(s => !string.IsNullOrEmpty(s.Selector)).ToList() ?? [];
            
            logger.LogInformation("Generated {Count} selectors from LLM", result.Count);
            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse generated selectors JSON: {Response}", TruncateText(response.Content, 200));
            return [];
        }
    }

    private static SelectorType DetermineSelectorType(string selector)
    {
        if (selector.StartsWith("//") || selector.StartsWith("(//") || selector.Contains("::"))
            return SelectorType.XPath;
        if (selector.StartsWith("$"))
            return SelectorType.JsonPath;
        if (selector.StartsWith("/") || selector.StartsWith("^"))
            return SelectorType.TextPattern;
        return SelectorType.CssSelector;
    }

    private static SelectorType ParseSelectorType(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return SelectorType.CssSelector;

        return type.ToLowerInvariant() switch
        {
            "xpath" => SelectorType.XPath,
            "jsonpath" => SelectorType.JsonPath,
            "textpattern" or "regex" => SelectorType.TextPattern,
            _ => SelectorType.CssSelector
        };
    }

    private static List<GeneratedSelector> DeduplicateAndPrioritize(List<GeneratedSelector> selectors)
    {
        return selectors
            .GroupBy(s => s.Selector.Trim().ToLowerInvariant())
            .Select(g => g.OrderBy(s => s.Priority).First())
            .OrderBy(s => s.Priority)
            .ThenByDescending(s => s.Confidence)
            .ToList();
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        return text[..maxChars] + "...";
    }

    private static string? CleanJsonResponse(string response)
    {
        var json = response.Trim();
        
        if (json.StartsWith("```"))
        {
            var lines = json.Split('\n');
            json = string.Join('\n', lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
        }
        
        var start = json.IndexOf('[');
        var end = json.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            return json[start..(end + 1)];
        }
        
        // No valid JSON array found - return null to indicate failure
        return null;
    }

    private class SelectorDto
    {
        public string? Selector { get; set; }
        public string? Type { get; set; }
        public string? Description { get; set; }
        public string? Reasoning { get; set; }
    }
}
