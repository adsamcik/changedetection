using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Content;

/// <summary>
/// LLM-powered content enrichment service for enhancing fetched content with metadata.
/// Extracts entities, topics, sentiment, and structured insights from raw content.
/// </summary>
public class ContentEnricher(
    ILlmProviderChain llmChain,
    ILogger<ContentEnricher> logger) : IContentEnricher
{
    /// <inheritdoc />
    public async Task<ContentEnrichmentResult> EnrichContentAsync(
        ContentEnrichmentRequest request,
        CancellationToken ct = default)
    {
        logger.LogInformation("Enriching content from {Url}", request.Url);

        var truncatedContent = TruncateText(request.Content, request.MaxContentLength);
        
        try
        {
            // Single comprehensive LLM call for efficiency
            var result = await PerformEnrichmentAsync(request, truncatedContent, ct);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Content enrichment failed for {Url}", request.Url);
            return new ContentEnrichmentResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    /// <inheritdoc />
    public async Task<QuickClassificationResult> QuickClassifyAsync(
        string content,
        CancellationToken ct = default)
    {
        var sample = TruncateText(content, 1500);

        var prompt = $$"""
            Quickly classify this content.
            
            Content sample:
            {{sample}}
            
            Respond in JSON format:
            {
                "contentType": "Article|ProductPage|EventPage|NewsList|StatusPage|ECommerce|Blog|Documentation|Forum|Other",
                "language": "en|es|fr|de|zh|ja|etc",
                "hasStructuredData": true/false (contains prices, dates, quantities, tables),
                "isTimeSensitive": true/false (events, sales, deadlines),
                "suggestedEnrichments": ["entities", "topics", "sentiment", "structuredData"],
                "confidence": 0.0-1.0
            }
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 256,
            ExpectJson = true,
            UsageType = LlmUsageType.ContentClassification
        }, ct);

        if (!response.IsSuccess)
        {
            logger.LogWarning("Quick classification failed: {Error}", response.ErrorMessage);
            return new QuickClassificationResult
            {
                ContentType = "Unknown",
                Language = "en",
                Confidence = 0
            };
        }

        try
        {
            var result = JsonSerializer.Deserialize<QuickClassifyResponse>(
                ExtractJson(response.Content ?? ""),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new QuickClassificationResult
            {
                ContentType = result?.ContentType ?? "Unknown",
                Language = result?.Language ?? "en",
                HasStructuredData = result?.HasStructuredData ?? false,
                IsTimeSensitive = result?.IsTimeSensitive ?? false,
                SuggestedEnrichments = result?.SuggestedEnrichments ?? [],
                Confidence = result?.Confidence ?? 0.5f
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse quick classification response");
            return new QuickClassificationResult
            {
                ContentType = "Unknown",
                Language = "en",
                Confidence = 0
            };
        }
    }

    /// <inheritdoc />
    public async Task<ContentFingerprint> GenerateFingerprintAsync(
        string content,
        CancellationToken ct = default)
    {
        var sample = TruncateText(content, 2000);

        var prompt = $$"""
            Generate a semantic fingerprint for this content.
            
            Content:
            {{sample}}
            
            Extract the key identifying elements that would help compare content similarity.
            
            Respond in JSON format:
            {
                "keyTopics": ["topic1", "topic2", "topic3"],
                "keyEntities": ["entity1", "entity2", "entity3"],
                "structureSignature": "brief description of content structure (list, article, table, etc.)",
                "semanticHash": "5-10 word summary that captures the essence"
            }
            
            Return 3-5 key topics and entities each.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.1f,
            MaxTokens = 256,
            ExpectJson = true,
            UsageType = LlmUsageType.ContentClassification
        }, ct);

        if (!response.IsSuccess)
        {
            logger.LogWarning("Fingerprint generation failed: {Error}", response.ErrorMessage);
            // Fall back to simple hash-based fingerprint
            return CreateFallbackFingerprint(content);
        }

        try
        {
            var result = JsonSerializer.Deserialize<FingerprintResponse>(
                ExtractJson(response.Content ?? ""),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new ContentFingerprint
            {
                SemanticHash = result?.SemanticHash ?? ComputeSimpleHash(content),
                KeyTopics = result?.KeyTopics ?? [],
                KeyEntities = result?.KeyEntities ?? [],
                StructureSignature = result?.StructureSignature
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse fingerprint response");
            return CreateFallbackFingerprint(content);
        }
    }

    private async Task<ContentEnrichmentResult> PerformEnrichmentAsync(
        ContentEnrichmentRequest request,
        string truncatedContent,
        CancellationToken ct)
    {
        var requestedParts = new List<string>();
        if (request.GenerateSummary) requestedParts.Add("summary");
        if (request.ExtractEntities) requestedParts.Add("entities");
        if (request.IdentifyTopics) requestedParts.Add("topics");
        if (request.AnalyzeSentiment) requestedParts.Add("sentiment");
        if (request.ExtractStructuredData) requestedParts.Add("structuredData");

        var contextInfo = new StringBuilder();
        if (!string.IsNullOrEmpty(request.Title))
            contextInfo.AppendLine($"Page title: {request.Title}");
        if (!string.IsNullOrEmpty(request.UserIntent))
            contextInfo.AppendLine($"User is monitoring for: {request.UserIntent}");

        var prompt = $$"""
            Analyze and enrich this content with metadata.
            
            {{(contextInfo.Length > 0 ? $"Context:\n{contextInfo}\n" : "")}}
            URL: {{request.Url}}
            
            Content:
            {{truncatedContent}}
            
            Extract the following: {{string.Join(", ", requestedParts)}}
            
            Respond in JSON format:
            {
                "summary": "Concise 2-3 sentence summary of the content",
                "contentType": "Article|ProductPage|EventPage|NewsList|StatusPage|ECommerce|Blog|Documentation|Forum|Other",
                "language": "en|es|fr|de|zh|ja|etc",
                "entities": [
                    {
                        "type": "Person|Organization|Location|Date|Product|Price|Event|Other",
                        "text": "entity text as it appears",
                        "normalizedValue": "standardized form if applicable",
                        "count": 1,
                        "confidence": 0.0-1.0,
                        "isProminent": true/false
                    }
                ],
                "topics": [
                    {
                        "name": "topic name",
                        "relevance": 0.0-1.0,
                        "category": "optional category",
                        "keywords": ["keyword1", "keyword2"]
                    }
                ],
                "sentiment": {
                    "overall": "Positive|Neutral|Negative|Mixed",
                    "score": -1.0 to 1.0,
                    "confidence": 0.0-1.0,
                    "dominantEmotion": "Joy|Sadness|Anger|Fear|Surprise|Neutral|null"
                },
                "structuredData": [
                    {
                        "type": "Date|Price|Quantity|Duration|Percentage|PhoneNumber|Email|Address|Other",
                        "rawText": "as it appears in content",
                        "normalizedValue": "standardized form",
                        "label": "what this value represents",
                        "unit": "USD|kg|hours|%|null",
                        "confidence": 0.0-1.0
                    }
                ],
                "keyPhrases": ["phrase1", "phrase2", "phrase3"],
                "readingLevel": "Elementary|MiddleSchool|HighSchool|College|Professional",
                "confidence": 0.0-1.0
            }
            
            Return only the requested fields. Keep entities and structured data to most important items (max 10 each).
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 2048,
            ExpectJson = true,
            UsageType = LlmUsageType.EntityEnrichment,
            WatchedSiteId = request.WatchId
        }, ct);

        if (!response.IsSuccess)
        {
            return new ContentEnrichmentResult
            {
                IsSuccess = false,
                ErrorMessage = response.ErrorMessage
            };
        }

        try
        {
            var result = JsonSerializer.Deserialize<EnrichmentResponse>(
                ExtractJson(response.Content ?? ""),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new ContentEnrichmentResult
            {
                IsSuccess = true,
                Summary = result?.Summary,
                ContentType = result?.ContentType,
                Language = result?.Language,
                Entities = result?.Entities?.Select(e => new ContentEntity
                {
                    Type = e.Type ?? "Unknown",
                    Text = e.Text ?? "",
                    NormalizedValue = e.NormalizedValue,
                    Count = e.Count,
                    Confidence = e.Confidence,
                    IsProminent = e.IsProminent
                }).ToList() ?? [],
                Topics = result?.Topics?.Select(t => new ContentTopic
                {
                    Name = t.Name ?? "",
                    Relevance = t.Relevance,
                    Category = t.Category,
                    Keywords = t.Keywords ?? []
                }).ToList() ?? [],
                Sentiment = result?.Sentiment != null ? new ContentSentiment
                {
                    Overall = result.Sentiment.Overall ?? "Neutral",
                    Score = result.Sentiment.Score,
                    Confidence = result.Sentiment.Confidence,
                    DominantEmotion = result.Sentiment.DominantEmotion
                } : null,
                StructuredData = result?.StructuredData?.Select(s => new StructuredDataItem
                {
                    Type = s.Type ?? "Other",
                    RawText = s.RawText ?? "",
                    NormalizedValue = s.NormalizedValue ?? "",
                    Label = s.Label,
                    Unit = s.Unit,
                    Confidence = s.Confidence
                }).ToList() ?? [],
                KeyPhrases = result?.KeyPhrases ?? [],
                ReadingLevel = result?.ReadingLevel,
                Confidence = result?.Confidence ?? 0.5f,
                InputTokens = response.InputTokens,
                OutputTokens = response.OutputTokens
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse enrichment response");
            return new ContentEnrichmentResult
            {
                IsSuccess = false,
                ErrorMessage = "Failed to parse enrichment response"
            };
        }
    }

    private static ContentFingerprint CreateFallbackFingerprint(string content)
    {
        return new ContentFingerprint
        {
            SemanticHash = ComputeSimpleHash(content),
            KeyTopics = [],
            KeyEntities = [],
            StructureSignature = "unknown"
        };
    }

    private static string ComputeSimpleHash(string content)
    {
        var words = content.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)
            .Take(50)
            .ToArray();
        var hashInput = string.Join(" ", words);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(hashInput));
        return Convert.ToHexString(hashBytes)[..16];
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }

    private static string ExtractJson(string content)
    {
        content = content.Trim();
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return content[start..(end + 1)];
        }
        return content;
    }
}

// Internal response DTOs for JSON deserialization
internal class QuickClassifyResponse
{
    public string? ContentType { get; set; }
    public string? Language { get; set; }
    public bool HasStructuredData { get; set; }
    public bool IsTimeSensitive { get; set; }
    public List<string>? SuggestedEnrichments { get; set; }
    public float Confidence { get; set; }
}

internal class FingerprintResponse
{
    public List<string>? KeyTopics { get; set; }
    public List<string>? KeyEntities { get; set; }
    public string? StructureSignature { get; set; }
    public string? SemanticHash { get; set; }
}

internal class EnrichmentResponse
{
    public string? Summary { get; set; }
    public string? ContentType { get; set; }
    public string? Language { get; set; }
    public List<EntityResponse>? Entities { get; set; }
    public List<TopicResponse>? Topics { get; set; }
    public SentimentResponseDto? Sentiment { get; set; }
    public List<StructuredDataResponse>? StructuredData { get; set; }
    public List<string>? KeyPhrases { get; set; }
    public string? ReadingLevel { get; set; }
    public float Confidence { get; set; }
}

internal class EntityResponse
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public string? NormalizedValue { get; set; }
    public int Count { get; set; } = 1;
    public float Confidence { get; set; }
    public bool IsProminent { get; set; }
}

internal class TopicResponse
{
    public string? Name { get; set; }
    public float Relevance { get; set; }
    public string? Category { get; set; }
    public List<string>? Keywords { get; set; }
}

internal class SentimentResponseDto
{
    public string? Overall { get; set; }
    public float Score { get; set; }
    public float Confidence { get; set; }
    public string? DominantEmotion { get; set; }
}

internal class StructuredDataResponse
{
    public string? Type { get; set; }
    public string? RawText { get; set; }
    public string? NormalizedValue { get; set; }
    public string? Label { get; set; }
    public string? Unit { get; set; }
    public float Confidence { get; set; }
}
