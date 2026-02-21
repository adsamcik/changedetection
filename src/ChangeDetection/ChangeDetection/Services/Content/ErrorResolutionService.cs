using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using HtmlAgilityPack;

namespace ChangeDetection.Services.Content;

/// <summary>
/// LLM-based error resolution service for diagnosing and fixing extraction failures.
/// Uses LLM to analyze page structure changes and generate corrected selectors.
/// </summary>
public class ErrorResolutionService(
    ILlmProviderChain llmChain,
    IContentExtractor contentExtractor,
    ILogger<ErrorResolutionService> logger) : IErrorResolutionService
{
    private const int MaxHtmlSampleLength = 6000;
    private const int MaxContentSampleLength = 1000;
    private const float AutoFixConfidenceThreshold = 0.85f;

    /// <inheritdoc />
    public async Task<ErrorResolutionResult> TryResolveAsync(
        ErrorResolutionContext context,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "Attempting error resolution for watch {WatchId} ({Url}), error type: {ErrorType}",
            context.Watch.Id, context.Watch.Url, context.ErrorType);

        try
        {
            // Build diagnostic prompt based on error type
            var prompt = BuildDiagnosticPrompt(context);
            
            var response = await llmChain.ExecuteAsync(prompt, new LlmRequestOptions
            {
                Temperature = 0.2f,
                MaxTokens = 800,
                UsageType = LlmUsageType.ErrorResolution,
                WatchedSiteId = context.Watch.Id,
                ExpectJson = true
            }, ct);

            if (!response.IsSuccess || string.IsNullOrEmpty(response.Content))
            {
                logger.LogWarning("LLM returned no response for error resolution");
                return CreateFailedResult("Unable to analyze the error - LLM unavailable");
            }

            // Parse the LLM response
            var resolution = ParseResolutionResponse(response.Content, context);
            
            // Validate proposed selector if one was generated
            if (!string.IsNullOrEmpty(resolution.NewCssSelector) || 
                !string.IsNullOrEmpty(resolution.NewXPathSelector))
            {
                var selector = resolution.NewCssSelector ?? resolution.NewXPathSelector!;
                var selectorType = !string.IsNullOrEmpty(resolution.NewCssSelector) 
                    ? SelectorType.CssSelector 
                    : SelectorType.XPath;
                
                var validation = await ValidateSelectorFixAsync(
                    context.CurrentHtml, selector, selectorType, ct);

                if (!validation.IsValid)
                {
                    logger.LogWarning(
                        "Proposed selector fix failed validation: {Error}", 
                        validation.ErrorMessage);
                    
                    return resolution with
                    {
                        IsResolved = false,
                        AutoFixApplied = false,
                        SuggestedAction = $"The proposed selector fix didn't work. {resolution.SuggestedAction}",
                        RequiresUserApproval = true
                    };
                }

                // Update resolution with validation results
                resolution = resolution with
                {
                    ExtractedSample = validation.ExtractedSample,
                    MatchCount = validation.MatchCount,
                    AutoFixApplied = resolution.Confidence >= AutoFixConfidenceThreshold && 
                                     !resolution.MajorStructureChange,
                    RequiresUserApproval = resolution.Confidence < AutoFixConfidenceThreshold || 
                                           resolution.MajorStructureChange
                };

                logger.LogInformation(
                    "Error resolution successful for watch {WatchId}: {Diagnosis}, confidence: {Confidence}",
                    context.Watch.Id, resolution.Diagnosis, resolution.Confidence);
            }
            
            // Validate NewItemSelector for schema drift recovery
            if (!string.IsNullOrEmpty(resolution.NewItemSelector))
            {
                var itemValidation = await ValidateSelectorFixAsync(
                    context.CurrentHtml, resolution.NewItemSelector, SelectorType.CssSelector, ct);

                if (!itemValidation.IsValid)
                {
                    logger.LogWarning(
                        "Proposed item selector fix failed validation: {Error}",
                        itemValidation.ErrorMessage);
                    
                    resolution = resolution with
                    {
                        NewItemSelector = null,
                        IsResolved = !string.IsNullOrEmpty(resolution.NewCssSelector) || 
                                     !string.IsNullOrEmpty(resolution.NewXPathSelector)
                    };
                }
                else
                {
                    resolution = resolution with
                    {
                        AutoFixApplied = resolution.Confidence >= AutoFixConfidenceThreshold && 
                                         !resolution.MajorStructureChange,
                        RequiresUserApproval = resolution.Confidence < AutoFixConfidenceThreshold || 
                                               resolution.MajorStructureChange
                    };

                    logger.LogInformation(
                        "Schema drift resolution found new item selector for watch {WatchId}, matches: {Count}",
                        context.Watch.Id, itemValidation.MatchCount);
                }
            }

            return resolution;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during resolution attempt for watch {WatchId}", context.Watch.Id);
            return CreateFailedResult($"Resolution failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Task<SelectorValidationResult> ValidateSelectorFixAsync(
        string html,
        string proposedSelector,
        SelectorType selectorType,
        CancellationToken ct = default)
    {
        try
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            HtmlNodeCollection? nodes;
            
            if (selectorType == SelectorType.XPath)
            {
                nodes = doc.DocumentNode.SelectNodes(proposedSelector);
            }
            else
            {
                // Convert CSS to XPath (simplified)
                var xpath = CssToXPath(proposedSelector);
                nodes = doc.DocumentNode.SelectNodes(xpath);
            }

            if (nodes == null || nodes.Count == 0)
            {
                return Task.FromResult(new SelectorValidationResult
                {
                    IsValid = false,
                    MatchCount = 0,
                    ErrorMessage = "Selector matched no elements"
                });
            }

            // Extract sample content
            var firstNode = nodes[0];
            var sample = contentExtractor.ExtractText(firstNode.OuterHtml);
            var truncatedSample = TruncateText(sample, MaxContentSampleLength);

            return Task.FromResult(new SelectorValidationResult
            {
                IsValid = true,
                MatchCount = nodes.Count,
                ExtractedSample = truncatedSample
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new SelectorValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Selector validation error: {ex.Message}"
            });
        }
    }

    private string BuildDiagnosticPrompt(ErrorResolutionContext context)
    {
        var watch = context.Watch;
        var currentHtmlSample = TruncateText(context.CurrentHtml, MaxHtmlSampleLength);
        var previousContentSample = context.PreviousContent != null 
            ? TruncateText(context.PreviousContent, MaxContentSampleLength) 
            : "Not available";

        var currentSelector = !string.IsNullOrEmpty(watch.CssSelector) 
            ? $"CSS: {watch.CssSelector}" 
            : !string.IsNullOrEmpty(watch.XPathSelector) 
                ? $"XPath: {watch.XPathSelector}" 
                : "Full page (no selector)";

        var prompt = $$"""
            Diagnose why content extraction failed and suggest a fix.
            
            WATCH INFO:
            - URL: {{watch.Url}}
            - Name: {{watch.Name ?? "Unnamed"}}
            - Description: {{watch.Description ?? "None"}}
            - Current selector: {{currentSelector}}
            - Consecutive failures: {{context.ConsecutiveFailures}}
            
            ERROR:
            - Type: {{context.ErrorType}}
            - Message: {{context.ErrorMessage}}
            
            PREVIOUS SUCCESSFUL CONTENT SAMPLE:
            {{previousContentSample}}
            
            CURRENT HTML STRUCTURE:
            {{currentHtmlSample}}
            
            TASK:
            1. Diagnose why the selector no longer works
            2. Determine if the website structure fundamentally changed
            3. Generate a new CSS or XPath selector that would extract similar content
            4. Explain your reasoning
            
            Return JSON:
            {
              "diagnosis": "Brief explanation of the problem",
              "majorStructureChange": true/false,
              "newCssSelector": "new CSS selector or null",
              "newXPathSelector": "new XPath selector or null", 
              "confidence": 0.0-1.0,
              "reasoning": "Why this fix should work",
              "suggestedAction": "What user should do if auto-fix fails"
            }
            
            IMPORTANT:
            - Prefer CSS selectors over XPath when possible
            - Use stable attributes (id, data-*, semantic classes) over positional selectors
            - Set confidence below 0.8 if unsure about the fix
            - Set majorStructureChange=true if the page layout changed significantly
            """;

        // If schema drift, add schema-specific context to help LLM recover the item selector
        if (context.ErrorType == ErrorType.SchemaDrift && context.Watch.Schema != null)
        {
            var schema = context.Watch.Schema;
            var fieldDescriptions = string.Join("\n",
                schema.Fields.Select(f => $"  - {f.Name}: selector=\"{f.Selector}\""));

            prompt += $$"""
            
            
            SCHEMA DRIFT CONTEXT:
            This watch uses schema-based object extraction. The item container selector no longer matches.
            - Current ItemSelector: {{schema.ItemSelector}}
            - Schema fields:
            {{fieldDescriptions}}
            
            ADDITIONAL TASK:
            Find a new CSS selector for the repeating item container that groups these fields together.
            The field selectors are relative to the item container.
            
            Include in your JSON response:
              "newItemSelector": "new CSS selector for the repeating item container"
            """;
        }

        return prompt;
    }

    private ErrorResolutionResult ParseResolutionResponse(string content, ErrorResolutionContext context)
    {
        try
        {
            // Extract JSON from response (handle markdown code blocks)
            var json = ExtractJson(content);
            if (json == null)
            {
                logger.LogWarning("Could not extract JSON from LLM response: {Response}", 
                    TruncateText(content, 200));
                return CreateFailedResult("Could not parse LLM response");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var diagnosis = root.TryGetProperty("diagnosis", out var d) 
                ? d.GetString() ?? "Unknown issue" 
                : "Unknown issue";
            
            var majorChange = root.TryGetProperty("majorStructureChange", out var m) && m.GetBoolean();
            
            var newCss = root.TryGetProperty("newCssSelector", out var css) 
                ? css.GetString() 
                : null;
            
            var newXPath = root.TryGetProperty("newXPathSelector", out var xpath) 
                ? xpath.GetString() 
                : null;
            
            var confidence = root.TryGetProperty("confidence", out var c) 
                ? (float)c.GetDouble() 
                : 0.5f;
            
            var reasoning = root.TryGetProperty("reasoning", out var r) 
                ? r.GetString() 
                : null;
            
            var suggestedAction = root.TryGetProperty("suggestedAction", out var s) 
                ? s.GetString() 
                : null;
            
            var newItemSelector = root.TryGetProperty("newItemSelector", out var nis)
                ? nis.GetString()
                : null;

            // Validate we have at least a diagnosis
            var hasSelector = !string.IsNullOrEmpty(newCss) || !string.IsNullOrEmpty(newXPath);
            var hasItemSelector = !string.IsNullOrEmpty(newItemSelector);

            return new ErrorResolutionResult
            {
                IsResolved = hasSelector || hasItemSelector,
                AutoFixApplied = false, // Will be set after validation
                Diagnosis = diagnosis,
                NewCssSelector = string.IsNullOrEmpty(newCss) ? null : newCss,
                NewXPathSelector = string.IsNullOrEmpty(newXPath) ? null : newXPath,
                NewItemSelector = string.IsNullOrEmpty(newItemSelector) ? null : newItemSelector,
                Confidence = Math.Clamp(confidence, 0f, 1f),
                Reasoning = reasoning,
                SuggestedAction = suggestedAction ?? "Review the watch configuration and update the selector manually",
                MajorStructureChange = majorChange,
                RequiresUserApproval = majorChange || confidence < AutoFixConfidenceThreshold
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse resolution JSON: {Content}", 
                TruncateText(content, 200));
            return CreateFailedResult("Failed to parse LLM diagnosis");
        }
    }

    private static string? ExtractJson(string content)
    {
        // Try to find JSON in markdown code blocks first
        var jsonStart = content.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonStart >= 0)
        {
            var start = content.IndexOf('\n', jsonStart) + 1;
            var end = content.IndexOf("```", start);
            if (end > start)
            {
                return content[start..end].Trim();
            }
        }

        // Try to find raw JSON object
        var braceStart = content.IndexOf('{');
        var braceEnd = content.LastIndexOf('}');
        if (braceStart >= 0 && braceEnd > braceStart)
        {
            return content[braceStart..(braceEnd + 1)];
        }

        return null;
    }

    private static ErrorResolutionResult CreateFailedResult(string diagnosis)
    {
        return new ErrorResolutionResult
        {
            IsResolved = false,
            AutoFixApplied = false,
            Diagnosis = diagnosis,
            SuggestedAction = "Please review the watch configuration and update the selector manually",
            Confidence = 0f,
            RequiresUserApproval = true,
            MajorStructureChange = false
        };
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;
        return text[..maxLength] + "...";
    }

    /// <summary>
    /// Basic CSS to XPath conversion for common selectors.
    /// </summary>
    private static string CssToXPath(string css)
    {
        // Handle ID selector
        if (css.StartsWith('#'))
        {
            var id = css[1..];
            return $"//*[@id='{id}']";
        }

        // Handle class selector
        if (css.StartsWith('.'))
        {
            var className = css[1..];
            return $"//*[contains(@class, '{className}')]";
        }

        // Handle element.class
        if (css.Contains('.'))
        {
            var parts = css.Split('.', 2);
            var element = parts[0];
            var className = parts[1];
            if (string.IsNullOrEmpty(element))
                return $"//*[contains(@class, '{className}')]";
            return $"//{element}[contains(@class, '{className}')]";
        }

        // Handle element#id
        if (css.Contains('#'))
        {
            var parts = css.Split('#', 2);
            var element = parts[0];
            var id = parts[1];
            if (string.IsNullOrEmpty(element))
                return $"//*[@id='{id}']";
            return $"//{element}[@id='{id}']";
        }

        // Fallback: treat as element name
        return $"//{css}";
    }
}
