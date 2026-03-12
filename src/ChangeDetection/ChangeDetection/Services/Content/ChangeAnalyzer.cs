using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Content;

/// <summary>
/// LLM-powered change analyzer for advanced semantic understanding of detected changes.
/// Provides intelligent summarization, relevance scoring, entity extraction, and anomaly detection.
/// </summary>
public class ChangeAnalyzer(
    ILlmProviderChain llmChain,
    IEnumerable<IProfileRelevanceScorer> profileScorers,
    ILogger<ChangeAnalyzer> logger) : IChangeAnalyzer
{
    /// <inheritdoc />
    public async Task<ChangeAnalysisResult> AnalyzeChangeAsync(
        ChangeAnalysisRequest request,
        CancellationToken ct = default)
    {
        ChangeAnalysisResult? result = null;
        
        await foreach (var progress in AnalyzeChangeStreamingAsync(request, ct))
        {
            if (progress.Result != null)
            {
                result = progress.Result;
            }
        }

        return result ?? new ChangeAnalysisResult
        {
            IsSuccess = false,
            ErrorMessage = "Analysis failed to produce a result"
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChangeAnalysisProgress> AnalyzeChangeStreamingAsync(
        ChangeAnalysisRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        logger.LogInformation("Analyzing change for {Url}", request.Url);

        // Step 1: Generate semantic summary
        yield return new ChangeAnalysisProgress { Step = "SemanticSummary", Status = "Starting" };

        string? semanticSummary = null;
        string? briefSummary = null;
        float confidence = 0.5f;
        ChangeAnalysisProgress? step1Result = null;

        try
        {
            var summaryResult = await GenerateSemanticSummaryAsync(request, ct);
            semanticSummary = summaryResult.SemanticSummary;
            briefSummary = summaryResult.BriefSummary;
            confidence = summaryResult.Confidence;
            step1Result = new ChangeAnalysisProgress { Step = "SemanticSummary", Status = "Completed" };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Semantic summary generation failed");
            step1Result = new ChangeAnalysisProgress 
            { 
                Step = "SemanticSummary", 
                Status = "Failed",
                ThinkingContent = ex.Message 
            };
        }
        yield return step1Result;

        // Step 2: Calculate relevance score (if user intent provided)
        yield return new ChangeAnalysisProgress { Step = "RelevanceScoring", Status = "Starting" };

        float relevanceScore = 0.5f;
        string? relevanceReason = null;
        string? matchDimensionsJson = null;
        string? extractedEntitiesJson = null;
        string? profileBriefSummary = null;
        ChangeAnalysisProgress? step2Result;

        if (!string.IsNullOrEmpty(request.UserIntent) || !string.IsNullOrEmpty(request.AnalysisProfileJson))
        {
            try
            {
                var relevanceResult = await CalculateRelevanceAsync(request, semanticSummary, ct);
                relevanceScore = relevanceResult.Score;
                relevanceReason = relevanceResult.Reason;
                matchDimensionsJson = relevanceResult.DimensionsJson;
                extractedEntitiesJson = relevanceResult.ExtractedEntitiesJson;
                profileBriefSummary = relevanceResult.BriefSummary;

                step2Result = new ChangeAnalysisProgress { Step = "RelevanceScoring", Status = "Completed" };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Relevance scoring failed");
                step2Result = new ChangeAnalysisProgress 
                { 
                    Step = "RelevanceScoring", 
                    Status = "Failed",
                    ThinkingContent = ex.Message 
                };
            }
        }
        else
        {
            step2Result = new ChangeAnalysisProgress { Step = "RelevanceScoring", Status = "Skipped" };
        }
        yield return step2Result;

        // Step 3: Categorize change
        yield return new ChangeAnalysisProgress { Step = "Categorization", Status = "Starting" };

        List<ChangeCategory> categories = [];
        ChangeAnalysisProgress? step3Result;

        if (request.CategorizeChange)
        {
            try
            {
                categories = await CategorizeChangeAsync(request, semanticSummary, ct);
                step3Result = new ChangeAnalysisProgress { Step = "Categorization", Status = "Completed" };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Change categorization failed");
                step3Result = new ChangeAnalysisProgress 
                { 
                    Step = "Categorization", 
                    Status = "Failed",
                    ThinkingContent = ex.Message 
                };
            }
        }
        else
        {
            step3Result = new ChangeAnalysisProgress { Step = "Categorization", Status = "Skipped" };
        }
        yield return step3Result;

        // Step 4: Extract entities
        yield return new ChangeAnalysisProgress { Step = "EntityExtraction", Status = "Starting" };

        List<ExtractedEntity> entities = [];
        List<KeyFact> keyFacts = [];
        ChangeAnalysisProgress? step4Result;

        if (request.ExtractEntities)
        {
            try
            {
                var extractionResult = await ExtractEntitiesAndFactsAsync(request, ct);
                entities = extractionResult.Entities;
                keyFacts = extractionResult.KeyFacts;
                step4Result = new ChangeAnalysisProgress { Step = "EntityExtraction", Status = "Completed" };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Entity extraction failed");
                step4Result = new ChangeAnalysisProgress 
                { 
                    Step = "EntityExtraction", 
                    Status = "Failed",
                    ThinkingContent = ex.Message 
                };
            }
        }
        else
        {
            step4Result = new ChangeAnalysisProgress { Step = "EntityExtraction", Status = "Skipped" };
        }
        yield return step4Result;

        // Step 5: Analyze sentiment (if enabled)
        yield return new ChangeAnalysisProgress { Step = "SentimentAnalysis", Status = "Starting" };

        SentimentAnalysis? sentiment = null;
        ChangeAnalysisProgress? step5Result;

        if (request.AnalyzeSentiment && 
            !string.IsNullOrEmpty(request.PreviousContent) && 
            !string.IsNullOrEmpty(request.CurrentContent))
        {
            try
            {
                sentiment = await AnalyzeSentimentAsync(request, ct);
                step5Result = new ChangeAnalysisProgress { Step = "SentimentAnalysis", Status = "Completed" };
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Sentiment analysis failed");
                step5Result = new ChangeAnalysisProgress 
                { 
                    Step = "SentimentAnalysis", 
                    Status = "Failed",
                    ThinkingContent = ex.Message 
                };
            }
        }
        else
        {
            step5Result = new ChangeAnalysisProgress { Step = "SentimentAnalysis", Status = "Skipped" };
        }
        yield return step5Result;

        // Step 6: Generate suggested actions
        yield return new ChangeAnalysisProgress { Step = "SuggestedActions", Status = "Starting" };

        List<string> suggestedActions = [];
        ChangeAnalysisProgress? step6Result;

        try
        {
            suggestedActions = await GenerateSuggestedActionsAsync(
                request, semanticSummary, categories, relevanceScore, ct);
            step6Result = new ChangeAnalysisProgress { Step = "SuggestedActions", Status = "Completed" };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Suggested actions generation failed");
            step6Result = new ChangeAnalysisProgress 
            { 
                Step = "SuggestedActions", 
                Status = "Failed",
                ThinkingContent = ex.Message 
            };
        }
        yield return step6Result;

        // Return final result
        yield return new ChangeAnalysisProgress
        {
            Step = "Complete",
            Status = "Completed",
            Result = new ChangeAnalysisResult
            {
                IsSuccess = true,
                SemanticSummary = semanticSummary,
                BriefSummary = profileBriefSummary ?? briefSummary,
                RelevanceScore = relevanceScore,
                RelevanceReason = relevanceReason,
                MatchDimensionsJson = matchDimensionsJson,
                Categories = categories,
                ExtractedEntities = entities,
                ExtractedEntitiesJson = extractedEntitiesJson,
                Sentiment = sentiment,
                KeyFacts = keyFacts,
                SuggestedActions = suggestedActions,
                Confidence = confidence
            }
        };
    }

    /// <inheritdoc />
    public async Task<AnomalyDetectionResult> DetectAnomaliesAsync(
        AnomalyDetectionRequest request,
        CancellationToken ct = default)
    {
        if (request.HistoricalChanges.Count < 3)
        {
            // Not enough history for meaningful anomaly detection
            return new AnomalyDetectionResult
            {
                HasAnomalies = false,
                AnomalyScore = 0,
                Explanation = "Insufficient historical data for anomaly detection (minimum 3 changes required)"
            };
        }

        var prompt = BuildAnomalyDetectionPrompt(request);

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 1024,
            ExpectJson = true,
            UsageType = LlmUsageType.AnomalyDetection,
            WatchedSiteId = request.CurrentChange.WatchId
        }, ct);

        if (!response.IsSuccess)
        {
            logger.LogWarning("Anomaly detection failed: {Error}", response.ErrorMessage);
            return new AnomalyDetectionResult
            {
                HasAnomalies = false,
                AnomalyScore = 0,
                Explanation = response.ErrorMessage
            };
        }

        try
        {
            var result = JsonSerializer.Deserialize<AnomalyDetectionResponse>(
                ExtractJson(response.Content ?? ""),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new AnomalyDetectionResult
            {
                HasAnomalies = result?.HasAnomalies ?? false,
                AnomalyScore = result?.AnomalyScore ?? 0,
                Anomalies = result?.Anomalies?.Select(a => new DetectedAnomaly
                {
                    Type = a.Type ?? "Unknown",
                    Severity = a.Severity ?? "Low",
                    Description = a.Description ?? "",
                    Reason = a.Reason
                }).ToList() ?? [],
                Explanation = result?.Explanation
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse anomaly detection response");
            return new AnomalyDetectionResult
            {
                HasAnomalies = false,
                AnomalyScore = 0,
                Explanation = "Failed to parse analysis response"
            };
        }
    }

    private async Task<(string? SemanticSummary, string? BriefSummary, float Confidence)> GenerateSemanticSummaryAsync(
        ChangeAnalysisRequest request,
        CancellationToken ct)
    {
        var contextInfo = new StringBuilder();
        if (!string.IsNullOrEmpty(request.WatchName))
            contextInfo.AppendLine($"Monitoring: {request.WatchName}");
        if (!string.IsNullOrEmpty(request.UserIntent))
            contextInfo.AppendLine($"User wants to track: {request.UserIntent}");
        if (request.Tags.Count > 0)
            contextInfo.AppendLine($"Categories: {string.Join(", ", request.Tags)}");

        var prompt = $$"""
            Analyze this content change and provide a semantic summary.
            
            {{(contextInfo.Length > 0 ? $"Context:\n{contextInfo}\n" : "")}}
            URL: {{request.Url}}
            Change size: +{{request.LinesAdded}} / -{{request.LinesRemoved}} lines
            
            Diff content:
            {{TruncateText(request.DiffContent, 3000)}}
            
            Respond in JSON format:
            {
                "semanticSummary": "A detailed 2-3 sentence summary explaining WHAT changed and WHY it might matter",
                "briefSummary": "A single short sentence (under 100 chars) for notifications",
                "confidence": 0.0-1.0
            }
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.3f,
            MaxTokens = 512,
            ExpectJson = true,
            UsageType = LlmUsageType.SemanticSummary,
            WatchedSiteId = request.WatchId
        }, ct);

        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(response.ErrorMessage);
        }

        var result = JsonSerializer.Deserialize<SemanticSummaryResponse>(
            ExtractJson(response.Content ?? ""),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return (result?.SemanticSummary, result?.BriefSummary, result?.Confidence ?? 0.5f);
    }

    private async Task<(float Score, string? Reason, string? DimensionsJson, string? ExtractedEntitiesJson, string? BriefSummary)> CalculateRelevanceAsync(
        ChangeAnalysisRequest request,
        string? semanticSummary,
        CancellationToken ct)
    {
        // When an analysis profile is present, delegate to registered profile scorers
        if (!string.IsNullOrEmpty(request.AnalysisProfileJson))
        {
            var scorer = profileScorers.FirstOrDefault(s => s.CanScore(request.AnalysisProfileJson));
            if (scorer is not null)
            {
                var profileResult = await scorer.ScoreAsync(request, semanticSummary, ct);
                return (
                    Math.Clamp(profileResult.Score, 0f, 1f),
                    profileResult.Reason,
                    profileResult.DimensionsJson,
                    profileResult.ExtractedEntitiesJson,
                    profileResult.BriefSummary);
            }
            logger.LogWarning("No profile relevance scorer found for the given profile");
        }

        var prompt = $$"""
            Score how relevant this change is to the user's monitoring goal.
            
            User's goal: {{request.UserIntent}}
            
            Change summary: {{semanticSummary ?? request.DiffContent[..Math.Min(500, request.DiffContent.Length)]}}
            
            Respond in JSON format:
            {
                "score": 0.0-1.0 (0 = completely irrelevant, 1 = exactly what user wants),
                "reason": "Brief explanation of relevance score"
            }
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 256,
            ExpectJson = true,
            UsageType = LlmUsageType.RelevanceScoring,
            WatchedSiteId = request.WatchId
        }, ct);

        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(response.ErrorMessage);
        }

        var result = JsonSerializer.Deserialize<RelevanceResponse>(
            ExtractJson(response.Content ?? ""),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return (Math.Clamp(result?.Score ?? 0.5f, 0f, 1f), result?.Reason, null, null, null);
    }

    private async Task<List<ChangeCategory>> CategorizeChangeAsync(
        ChangeAnalysisRequest request,
        string? semanticSummary,
        CancellationToken ct)
    {
        var prompt = $$"""
            Categorize this content change into semantic categories.
            
            Change: {{semanticSummary ?? TruncateText(request.DiffContent, 1000)}}
            URL: {{request.Url}}
            
            Common categories: PriceChange, NewContent, RemovedContent, DateUpdate, StatusChange, 
            ProductUpdate, EventAnnouncement, PolicyChange, ErrorFixed, LayoutChange, MinorEdit, Other
            
            Respond in JSON format:
            {
                "categories": [
                    {"name": "CategoryName", "confidence": 0.0-1.0, "reason": "why this applies"}
                ]
            }
            
            Return 1-3 most relevant categories.
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 512,
            ExpectJson = true,
            UsageType = LlmUsageType.ContentClassification,
            WatchedSiteId = request.WatchId
        }, ct);

        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(response.ErrorMessage);
        }

        var result = JsonSerializer.Deserialize<CategoriesResponse>(
            ExtractJson(response.Content ?? ""),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return result?.Categories?.Select(c => new ChangeCategory
        {
            Name = c.Name ?? "Unknown",
            Confidence = c.Confidence,
            Reason = c.Reason
        }).ToList() ?? [];
    }

    private async Task<(List<ExtractedEntity> Entities, List<KeyFact> KeyFacts)> ExtractEntitiesAndFactsAsync(
        ChangeAnalysisRequest request,
        CancellationToken ct)
    {
        var prompt = $$"""
            Extract entities and key facts from this content change.
            
            Diff content:
            {{TruncateText(request.DiffContent, 2500)}}
            
            Identify:
            - Entities: People, Organizations, Dates, Locations, Products, Prices, etc.
            - Key facts: Important data points that changed (prices, dates, quantities, statuses)
            
            Respond in JSON format:
            {
                "entities": [
                    {
                        "type": "Person|Organization|Date|Location|Product|Price|Quantity|Status|Other",
                        "value": "the entity value",
                        "changeType": "Added|Removed|Modified|Unchanged",
                        "previousValue": "if modified, the old value",
                        "confidence": 0.0-1.0
                    }
                ],
                "keyFacts": [
                    {
                        "type": "Price|Date|Quantity|Status|Other",
                        "label": "human readable label",
                        "value": "current value",
                        "previousValue": "old value if changed",
                        "isSignificant": true/false
                    }
                ]
            }
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 1024,
            ExpectJson = true,
            UsageType = LlmUsageType.EntityEnrichment,
            WatchedSiteId = request.WatchId
        }, ct);

        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(response.ErrorMessage);
        }

        var result = JsonSerializer.Deserialize<EntityExtractionResponse>(
            ExtractJson(response.Content ?? ""),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var entities = result?.Entities?.Select(e => new ExtractedEntity
        {
            Type = e.Type ?? "Unknown",
            Value = e.Value ?? "",
            ChangeType = Enum.TryParse<EntityChangeType>(e.ChangeType, true, out var ct2) ? ct2 : EntityChangeType.Unchanged,
            PreviousValue = e.PreviousValue,
            Confidence = e.Confidence
        }).ToList() ?? [];

        var keyFacts = result?.KeyFacts?.Select(f => new KeyFact
        {
            Type = f.Type ?? "Other",
            Label = f.Label ?? "",
            Value = f.Value ?? "",
            PreviousValue = f.PreviousValue,
            IsSignificant = f.IsSignificant
        }).ToList() ?? [];

        return (entities, keyFacts);
    }

    private async Task<SentimentAnalysis?> AnalyzeSentimentAsync(
        ChangeAnalysisRequest request,
        CancellationToken ct)
    {
        var prompt = $$"""
            Analyze the sentiment shift between previous and current content.
            
            Previous content (excerpt):
            {{TruncateText(request.PreviousContent ?? "", 1000)}}
            
            Current content (excerpt):
            {{TruncateText(request.CurrentContent ?? "", 1000)}}
            
            Respond in JSON format:
            {
                "previousSentiment": "Positive|Neutral|Negative",
                "currentSentiment": "Positive|Neutral|Negative",
                "sentimentShift": -1.0 to 1.0 (negative = became more negative, positive = became more positive),
                "description": "Brief description of the sentiment change, or null if no significant change"
            }
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 256,
            ExpectJson = true,
            UsageType = LlmUsageType.SentimentAnalysis,
            WatchedSiteId = request.WatchId
        }, ct);

        if (!response.IsSuccess)
        {
            throw new InvalidOperationException(response.ErrorMessage);
        }

        var result = JsonSerializer.Deserialize<SentimentResponse>(
            ExtractJson(response.Content ?? ""),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (result == null) return null;

        return new SentimentAnalysis
        {
            PreviousSentiment = result.PreviousSentiment,
            CurrentSentiment = result.CurrentSentiment,
            SentimentShift = result.SentimentShift,
            Description = result.Description
        };
    }

    private async Task<List<string>> GenerateSuggestedActionsAsync(
        ChangeAnalysisRequest request,
        string? semanticSummary,
        List<ChangeCategory> categories,
        float relevanceScore,
        CancellationToken ct)
    {
        var categoryNames = string.Join(", ", categories.Select(c => c.Name));

        var prompt = $$"""
            Based on this detected change, suggest 1-3 actionable next steps for the user.
            
            Change summary: {{semanticSummary ?? "Content changed"}}
            Categories: {{(string.IsNullOrEmpty(categoryNames) ? "General" : categoryNames)}}
            Relevance to user goal: {{relevanceScore:P0}}
            URL: {{request.Url}}
            
            Respond in JSON format:
            {
                "actions": [
                    "Suggested action 1",
                    "Suggested action 2"
                ]
            }
            
            Keep actions brief and actionable. Examples:
            - "Review the price change before making a purchase"
            - "Check if the new event date works for your schedule"
            - "No action needed - minor formatting change"
            """;

        var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.4f,
            MaxTokens = 256,
            ExpectJson = true,
            UsageType = LlmUsageType.Other,
            WatchedSiteId = request.WatchId
        }, ct);

        if (!response.IsSuccess)
        {
            return [];
        }

        var result = JsonSerializer.Deserialize<SuggestedActionsResponse>(
            ExtractJson(response.Content ?? ""),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        return result?.Actions ?? [];
    }

    private static string BuildAnomalyDetectionPrompt(AnomalyDetectionRequest request)
    {
        var historyDescription = new StringBuilder();
        foreach (var change in request.HistoricalChanges.TakeLast(10))
        {
            historyDescription.AppendLine($"- {change.DetectedAt:g}: {change.Summary} ({change.LinesChanged} lines)");
        }

        var linesChanged = request.CurrentChange.LinesAdded + request.CurrentChange.LinesRemoved;
        var summary = TruncateText(request.CurrentChange.DiffContent, 500);
        var historyCount = request.HistoricalChanges.Count;
        var avgInterval = request.AverageChangeInterval.HasValue 
            ? $"Average time between changes: {request.AverageChangeInterval.Value.TotalHours:F1} hours" 
            : "";
        var typicalSize = request.TypicalChangeSize.HasValue 
            ? $"Typical change size: ~{request.TypicalChangeSize.Value} lines" 
            : "";

        return $$"""
            Analyze if this change is anomalous compared to historical patterns.
            
            Current change:
            - Lines changed: {{linesChanged}}
            - Summary: {{summary}}
            
            Historical changes (last {{historyCount}}):
            {{historyDescription}}
            
            {{avgInterval}}
            {{typicalSize}}
            
            Look for:
            - Unusually large or small changes
            - Unusual timing patterns
            - Content that doesn't match historical patterns
            - Potential errors or issues
            
            Respond in JSON format:
            {
                "hasAnomalies": true/false,
                "anomalyScore": 0.0-1.0,
                "anomalies": [
                    {
                        "type": "UnusualSize|UnusualTiming|UnexpectedContent|PatternBreak|PotentialError",
                        "severity": "Low|Medium|High",
                        "description": "what is anomalous",
                        "reason": "why this is considered anomalous"
                    }
                ],
                "explanation": "overall assessment"
            }
            """;
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
internal class SemanticSummaryResponse
{
    public string? SemanticSummary { get; set; }
    public string? BriefSummary { get; set; }
    public float Confidence { get; set; }
}

internal class RelevanceResponse
{
    public float Score { get; set; }
    public string? Reason { get; set; }
}

internal class CategoriesResponse
{
    public List<CategoryItem>? Categories { get; set; }
}

internal class CategoryItem
{
    public string? Name { get; set; }
    public float Confidence { get; set; }
    public string? Reason { get; set; }
}

internal class EntityExtractionResponse
{
    public List<EntityItem>? Entities { get; set; }
    public List<KeyFactItem>? KeyFacts { get; set; }
}

internal class EntityItem
{
    public string? Type { get; set; }
    public string? Value { get; set; }
    public string? ChangeType { get; set; }
    public string? PreviousValue { get; set; }
    public float Confidence { get; set; }
}

internal class KeyFactItem
{
    public string? Type { get; set; }
    public string? Label { get; set; }
    public string? Value { get; set; }
    public string? PreviousValue { get; set; }
    public bool IsSignificant { get; set; }
}

internal class SentimentResponse
{
    public string? PreviousSentiment { get; set; }
    public string? CurrentSentiment { get; set; }
    public float SentimentShift { get; set; }
    public string? Description { get; set; }
}

internal class SuggestedActionsResponse
{
    public List<string>? Actions { get; set; }
}

internal class AnomalyDetectionResponse
{
    public bool HasAnomalies { get; set; }
    public float AnomalyScore { get; set; }
    public List<AnomalyItem>? Anomalies { get; set; }
    public string? Explanation { get; set; }
}

internal class AnomalyItem
{
    public string? Type { get; set; }
    public string? Severity { get; set; }
    public string? Description { get; set; }
    public string? Reason { get; set; }
}
