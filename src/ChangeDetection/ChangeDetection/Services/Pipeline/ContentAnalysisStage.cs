using System.Runtime.CompilerServices;
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
        ContentAnalysis? result = null;
        await foreach (var progress in AnalyzeStreamingAsync(content, userIntent, ct))
        {
            if (progress.Result != null)
            {
                result = progress.Result;
            }
        }
        return result ?? new ContentAnalysis
        {
            PageDescription = $"Page titled '{content.Title ?? "Unknown"}'",
            UserIntent = userIntent,
            ContentType = ContentType.Unknown,
            IdentifiedSections = [],
            RecommendedApproach = MonitoringApproach.FullPage,
            Confidence = 0.3f
        };
    }
    
    /// <summary>
    /// Analyzes the page content with streaming progress for thinking.
    /// </summary>
    public async IAsyncEnumerable<ContentAnalysisProgress> AnalyzeStreamingAsync(
        FetchedContent content,
        string userIntent,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Analyzing content for {Url}", content.Url);

        // Step 1: Classify the content type (simple classification)
        yield return new ContentAnalysisProgress { Step = "ContentClassification", Status = "Starting" };
        
        ContentType contentType = ContentType.Unknown;
        await foreach (var chunk in ClassifyContentTypeStreamingAsync(content, ct))
        {
            if (chunk.IsThinking)
            {
                yield return new ContentAnalysisProgress 
                { 
                    Step = "ContentClassification", 
                    Status = "Thinking",
                    ThinkingContent = chunk.Content
                };
            }
            else
            {
                contentType = chunk.Result;
            }
        }
        yield return new ContentAnalysisProgress { Step = "ContentClassification", Status = "Completed" };
        
        // Step 2: Extract user intent from their input
        yield return new ContentAnalysisProgress { Step = "IntentExtraction", Status = "Starting" };
        
        string intent = "Monitor for any changes";
        await foreach (var chunk in ExtractUserIntentStreamingAsync(userIntent, content.Title, ct))
        {
            if (chunk.IsThinking)
            {
                yield return new ContentAnalysisProgress 
                { 
                    Step = "IntentExtraction", 
                    Status = "Thinking",
                    ThinkingContent = chunk.Content
                };
            }
            else
            {
                intent = chunk.Result ?? intent;
            }
        }
        yield return new ContentAnalysisProgress { Step = "IntentExtraction", Status = "Completed" };
        
        // Step 2.5: Extract filter keywords from user intent
        yield return new ContentAnalysisProgress { Step = "FilterKeywordExtraction", Status = "Starting" };
        
        List<string> filterKeywords = [];
        await foreach (var chunk in ExtractFilterKeywordsStreamingAsync(userIntent, content.Title, ct))
        {
            if (chunk.IsThinking)
            {
                yield return new ContentAnalysisProgress
                {
                    Step = "FilterKeywordExtraction",
                    Status = "Thinking",
                    ThinkingContent = chunk.Content
                };
            }
            else if (chunk.Result != null)
            {
                filterKeywords = chunk.Result;
            }
        }
        yield return new ContentAnalysisProgress { Step = "FilterKeywordExtraction", Status = "Completed" };
        
        // Step 3: Identify page sections relevant to the intent
        yield return new ContentAnalysisProgress { Step = "SectionIdentification", Status = "Starting" };
        
        List<PageSection> sections = [];
        await foreach (var chunk in IdentifyPageSectionsStreamingAsync(content, intent, contentType, ct))
        {
            if (chunk.IsThinking)
            {
                yield return new ContentAnalysisProgress 
                { 
                    Step = "SectionIdentification", 
                    Status = "Thinking",
                    ThinkingContent = chunk.Content
                };
            }
            else
            {
                sections = chunk.Result ?? [];
            }
        }
        yield return new ContentAnalysisProgress { Step = "SectionIdentification", Status = "Completed" };
        
        // Step 4: Determine the best monitoring approach
        var approach = DetermineApproach(contentType, sections);
        var confidence = CalculateConfidence(contentType, sections, intent);

        yield return new ContentAnalysisProgress
        {
            Step = "Complete",
            Status = "Completed",
            Result = new ContentAnalysis
            {
                PageDescription = $"Page titled '{content.Title ?? "Unknown"}' containing {contentType} content",
                UserIntent = intent,
                ContentType = contentType,
                IdentifiedSections = sections,
                RecommendedApproach = approach,
                Confidence = confidence,
                FilterKeywords = filterKeywords
            }
        };
    }

    private async IAsyncEnumerable<StreamingResult<ContentType>> ClassifyContentTypeStreamingAsync(
        FetchedContent content, 
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sample = TruncateText(content.TextContent ?? "", 2000);
        
        var prompt = $"""
            Webpage category? Reply ONE word only.
            Options: NewsList, EventList, ProductListing, PriceInfo, Article, Table, Feed, StatusPage, Other

            Title: {content.Title ?? "Unknown"}
            Content: {sample}

            Category:
            """;

        var fullContent = new System.Text.StringBuilder();
        
        await foreach (var chunk in llmChain.ExecuteStreamingAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.1f,
            MaxTokens = 20,
            UsageType = LlmUsageType.ContentAnalysis
        }, ct))
        {
            if (chunk.Type == LlmStreamChunkType.Content && !string.IsNullOrEmpty(chunk.Text))
            {
                fullContent.Append(chunk.Text);
                yield return new StreamingResult<ContentType> { IsThinking = true, Content = chunk.Text };
            }
        }

        var responseText = fullContent.ToString().Trim().ToLowerInvariant();
        var result = ParseContentType(responseText);
        yield return new StreamingResult<ContentType> { Result = result };
    }
    
    private static ContentType ParseContentType(string responseText)
    {
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

    private async IAsyncEnumerable<StreamingResult<string>> ExtractUserIntentStreamingAsync(
        string userInput, 
        string? pageTitle, 
        [EnumeratorCancellation] CancellationToken ct)
    {
        var prompt = $"""
            User monitors: "{userInput}"
            Page: "{pageTitle ?? "Unknown"}"
            Summarize goal in <15 words. Plain text only, no quotes or markdown:
            """;

        var fullContent = new System.Text.StringBuilder();

        await foreach (var chunk in llmChain.ExecuteStreamingAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.3f,
            MaxTokens = 50,
            UsageType = LlmUsageType.ContentAnalysis
        }, ct))
        {
            if (chunk.Type == LlmStreamChunkType.Content && !string.IsNullOrEmpty(chunk.Text))
            {
                fullContent.Append(chunk.Text);
                yield return new StreamingResult<string> { IsThinking = true, Content = chunk.Text };
            }
        }

        var intent = fullContent.ToString().Trim();
        intent = intent.Replace("**", "").Replace("*", "");
        intent = intent.Trim('"', '\'');
        intent = intent.TrimStart('\u201C', '\u201D', '\u2018', '\u2019');
        intent = intent.TrimEnd('\u201C', '\u201D', '\u2018', '\u2019');
        intent = intent.Trim();
        
        yield return new StreamingResult<string> 
        { 
            Result = string.IsNullOrEmpty(intent) ? "Monitor for any changes" : intent 
        };
    }

    private async IAsyncEnumerable<StreamingResult<List<string>>> ExtractFilterKeywordsStreamingAsync(
        string userInput,
        string? pageTitle,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var prompt = $"""
            Extract 0-5 specific filter keywords from this monitoring request.
            These are concrete values the user wants to match in page content changes.
            
            User request: "{userInput}"
            Page: "{pageTitle ?? "Unknown"}"
            
            Rules:
            - Only include specific nouns, names, places, numbers, or values
            - Do NOT include generic words like "change", "monitor", "notify", "alert", "update"
            - If the user wants to track ANY change (no specific filter), return empty: []
            - Return JSON array of strings only: ["keyword1", "keyword2"]
            
            Examples:
            - "alert when tour comes to Prague" → ["Prague"]
            - "notify when price drops below $20" → ["20"]
            - "let me know about any changes" → []
            - "watch for new feature X commits" → ["feature X"]
            
            Keywords:
            """;

        var fullContent = new System.Text.StringBuilder();

        await foreach (var chunk in llmChain.ExecuteStreamingAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.1f,
            MaxTokens = 100,
            UsageType = LlmUsageType.ContentAnalysis,
            ExpectJson = true
        }, ct))
        {
            if (chunk.Type == LlmStreamChunkType.Content && !string.IsNullOrEmpty(chunk.Text))
            {
                fullContent.Append(chunk.Text);
                yield return new StreamingResult<List<string>> { IsThinking = true, Content = chunk.Text };
            }
        }

        var keywords = ParseKeywordsJson(fullContent.ToString());
        logger.LogDebug("Extracted {Count} filter keywords: {Keywords}", keywords.Count, string.Join(", ", keywords));
        yield return new StreamingResult<List<string>> { Result = keywords };
    }

    private List<string> ParseKeywordsJson(string responseContent)
    {
        if (string.IsNullOrEmpty(responseContent))
            return [];

        try
        {
            var json = CleanJsonResponse(responseContent);
            var keywords = JsonSerializer.Deserialize<List<string>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return keywords?.Where(k => !string.IsNullOrWhiteSpace(k)).ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse filter keywords JSON: {Content}", responseContent);
            return [];
        }
    }

    private async IAsyncEnumerable<StreamingResult<List<PageSection>>> IdentifyPageSectionsStreamingAsync(
        FetchedContent content,
        string userIntent,
        ContentType contentType,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var htmlSample = TruncateText(content.CleanedHtml ?? content.Html ?? "", 8000);
        
        logger.LogDebug("Identifying sections in {Length} chars of HTML for intent: {Intent}",
            htmlSample.Length, userIntent);
        
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

        var fullContent = new System.Text.StringBuilder();

        await foreach (var chunk in llmChain.ExecuteStreamingAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 300,
            UsageType = LlmUsageType.ContentAnalysis,
            ExpectJson = true
        }, ct))
        {
            if (chunk.Type == LlmStreamChunkType.Content && !string.IsNullOrEmpty(chunk.Text))
            {
                fullContent.Append(chunk.Text);
                yield return new StreamingResult<List<PageSection>> { IsThinking = true, Content = chunk.Text };
            }
        }

        var sections = ParseSectionsJson(fullContent.ToString());
        yield return new StreamingResult<List<PageSection>> { Result = sections };
    }
    
    private List<PageSection> ParseSectionsJson(string responseContent)
    {
        if (string.IsNullOrEmpty(responseContent))
            return [];

        try
        {
            var json = CleanJsonResponse(responseContent);
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
            logger.LogWarning(ex, "Failed to parse section JSON: {Content}", responseContent);
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

/// <summary>
/// Progress update during content analysis.
/// </summary>
public class ContentAnalysisProgress
{
    /// <summary>Analysis step name.</summary>
    public required string Step { get; init; }
    
    /// <summary>Step status: Starting, Thinking, Completed.</summary>
    public required string Status { get; init; }
    
    /// <summary>Streamed thinking content from LLM.</summary>
    public string? ThinkingContent { get; init; }
    
    /// <summary>Final analysis result (only on complete).</summary>
    public ContentAnalysis? Result { get; init; }
}

/// <summary>
/// Generic streaming result for LLM responses.
/// </summary>
/// <typeparam name="T">The result type.</typeparam>
public class StreamingResult<T>
{
    /// <summary>True if this is thinking content.</summary>
    public bool IsThinking { get; init; }
    
    /// <summary>Streamed content token.</summary>
    public string? Content { get; init; }
    
    /// <summary>Final result (only set on last chunk).</summary>
    public T? Result { get; init; }
}
