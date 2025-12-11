using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Stage 3: Uses LLM to analyze page content and understand what user wants to monitor.
/// Designed for smaller LLMs with focused, simple prompts.
/// </summary>
public class ContentAnalysisStage(
    ILlmProviderChain llmChain,
    ILogger<ContentAnalysisStage> logger)
{
    /// <summary>
    /// Analyzes the page content to understand structure and user intent.
    /// </summary>
    public async Task<ContentAnalysis> AnalyzeAsync(
        FetchedContent content,
        string userIntent,
        CancellationToken ct = default)
    {
        logger.LogInformation("Analyzing content for {Url}", content.Url);

        // Step 1: Classify the content type (simple classification)
        var contentType = await ClassifyContentTypeAsync(content, ct);
        
        // Step 2: Extract user intent from their input
        var intent = await ExtractUserIntentAsync(userIntent, content.Title, ct);
        
        // Step 3: Identify page sections relevant to the intent
        var sections = await IdentifyPageSectionsAsync(content, intent, contentType, ct);
        
        // Step 4: Determine the best monitoring approach
        var approach = DetermineApproach(contentType, sections);

        var confidence = CalculateConfidence(contentType, sections, intent);

        return new ContentAnalysis
        {
            PageDescription = $"Page titled '{content.Title ?? "Unknown"}' containing {contentType} content",
            UserIntent = intent,
            ContentType = contentType,
            IdentifiedSections = sections,
            RecommendedApproach = approach,
            Confidence = confidence
        };
    }

    private async Task<ContentType> ClassifyContentTypeAsync(FetchedContent content, CancellationToken ct)
    {
        var sample = TruncateText(content.TextContent ?? "", 2000);
        
        // Compact prompt optimized for small models
        var prompt = $"""
            Webpage category? Reply ONE word only.
            Options: NewsList, EventList, ProductListing, PriceInfo, Article, Table, Feed, StatusPage, Other

            Title: {content.Title ?? "Unknown"}
            Content: {sample}

            Category:
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.1f,
            MaxTokens = 20,
            UsageType = LlmUsageType.ContentAnalysis
        }, ct);

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
            return ContentType.Unknown;

        var responseText = response.Content.Trim().ToLowerInvariant();
        
        if (responseText.Contains("newslist") || responseText.Contains("news"))
            return ContentType.NewsList;
        if (responseText.Contains("eventlist") || responseText.Contains("event") || responseText.Contains("calendar"))
            return ContentType.EventList;
        if (responseText.Contains("productlisting") || responseText.Contains("product") || responseText.Contains("catalog"))
            return ContentType.ProductListing;
        if (responseText.Contains("priceinfo") || responseText.Contains("price"))
            return ContentType.PriceInfo;
        if (responseText.Contains("article"))
            return ContentType.Article;
        if (responseText.Contains("table"))
            return ContentType.Table;
        if (responseText.Contains("feed"))
            return ContentType.Feed;
        if (responseText.Contains("statuspage") || responseText.Contains("status"))
            return ContentType.StatusPage;
        
        return ContentType.Unknown;
    }

    private async Task<string> ExtractUserIntentAsync(string userInput, string? pageTitle, CancellationToken ct)
    {
        // Compact prompt optimized for small models
        var prompt = $"""
            User monitors: "{userInput}"
            Page: "{pageTitle ?? "Unknown"}"
            Summarize goal in <15 words. Plain text only, no quotes or markdown:
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.3f,
            MaxTokens = 50,
            UsageType = LlmUsageType.ContentAnalysis
        }, ct);

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
            return "Monitor for any changes";
            
        // Clean up LLM response - remove markdown, quotes, etc.
        var intent = response.Content.Trim();
        intent = intent.Replace("**", "").Replace("*", "");  // Remove bold/italic markdown
        intent = intent.Trim('"', '\'');  // Remove basic quotes
        // Also remove fancy unicode quotes
        intent = intent.TrimStart('\u201C', '\u201D', '\u2018', '\u2019');
        intent = intent.TrimEnd('\u201C', '\u201D', '\u2018', '\u2019');
        intent = intent.Trim();
        
        return string.IsNullOrEmpty(intent) ? "Monitor for any changes" : intent;
    }

    private async Task<List<PageSection>> IdentifyPageSectionsAsync(
        FetchedContent content,
        string userIntent,
        ContentType contentType,
        CancellationToken ct)
    {
        // Give LLM more HTML for better section identification (8000 chars)
        var htmlSample = TruncateText(content.CleanedHtml ?? content.Html ?? "", 8000);
        
        logger.LogDebug("Identifying sections in {Length} chars of HTML for intent: {Intent}",
            htmlSample.Length, userIntent);
        
        // More detailed prompt to help LLM find specific content sections
        var prompt = $$"""
            Analyze this HTML and find 1-3 content sections that match the user's goal.
            
            User wants: {{userIntent}}
            Content type: {{contentType}}
            
            HTML:
            {{htmlSample}}
            
            For each section, provide:
            - name: short descriptive name
            - selector: CSS selector to target this section (prefer IDs, data-attributes, or unique classes)
            - isTarget: true if this matches what user wants to monitor
            - description: what content this section contains
            
            Return JSON array only: [{"name":"x","selector":"css","isTarget":true,"description":"x"}]
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 300,
            UsageType = LlmUsageType.ContentAnalysis,
            ExpectJson = true
        }, ct);

        if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
            return [];

        try
        {
            var json = CleanJsonResponse(response.Content);
            var sections = JsonSerializer.Deserialize<List<SectionDto>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return sections?.Select(s => new PageSection
            {
                Name = s.Name ?? "Unknown",
                Description = s.Description,
                SuggestedSelector = s.Selector,
                IsLikelyTarget = s.IsTarget
            }).ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse section JSON: {Content}", response.Content);
            return [];
        }
    }

    private static MonitoringApproach DetermineApproach(ContentType contentType, List<PageSection> sections)
    {
        if (sections.Count == 0)
            return MonitoringApproach.FullPage;

        var targetSections = sections.Where(s => s.IsLikelyTarget).ToList();
        
        if (targetSections.Count == 0)
            return MonitoringApproach.FullPage;

        if (targetSections.Count == 1)
            return MonitoringApproach.SpecificSelector;

        if (targetSections.Count > 1)
            return MonitoringApproach.MultipleSelectors;

        return contentType switch
        {
            ContentType.Table => MonitoringApproach.StructuredData,
            ContentType.PriceInfo => MonitoringApproach.TextPattern,
            _ => MonitoringApproach.SpecificSelector
        };
    }

    private static float CalculateConfidence(ContentType contentType, List<PageSection> sections, string intent)
    {
        float confidence = 0.5f;

        // Higher confidence if we identified specific sections
        if (sections.Any(s => s.IsLikelyTarget))
            confidence += 0.2f;

        // Higher confidence if content type is clear
        if (contentType != ContentType.Unknown && contentType != ContentType.Other)
            confidence += 0.15f;

        // Higher confidence if we have selectors
        if (sections.Any(s => !string.IsNullOrEmpty(s.SuggestedSelector)))
            confidence += 0.15f;

        return Math.Min(1.0f, confidence);
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        return text[..maxChars] + "...";
    }

    private static string CleanJsonResponse(string response)
    {
        var json = response.Trim();
        
        // Remove markdown code blocks
        if (json.StartsWith("```"))
        {
            var lines = json.Split('\n');
            json = string.Join('\n', lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
        }
        
        // Find JSON array
        var start = json.IndexOf('[');
        var end = json.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            json = json[start..(end + 1)];
        }
        
        return json;
    }

    private class SectionDto
    {
        public string? Name { get; set; }
        public string? Selector { get; set; }
        public bool IsTarget { get; set; }
        public string? Description { get; set; }
    }
}
