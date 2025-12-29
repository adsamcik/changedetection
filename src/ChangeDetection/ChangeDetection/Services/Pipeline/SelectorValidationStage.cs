using System.Text.RegularExpressions;
using ChangeDetection.Core.Interfaces;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Stage 5: Validates generated selectors against the actual content.
/// No LLM needed - pure HTML/selector testing.
/// </summary>
public partial class SelectorValidationStage(
    IContentExtractor contentExtractor,
    ILogger<SelectorValidationStage> logger)
{
    /// <summary>
    /// Validates all selectors against the fetched content.
    /// </summary>
    public List<SelectorValidation> ValidateSelectors(
        FetchedContent content,
        List<GeneratedSelector> selectors,
        ContentAnalysis analysis)
    {
        if (string.IsNullOrEmpty(content.Html))
            return [];

        logger.LogInformation("Validating {Count} selectors", selectors.Count);

        var results = new List<SelectorValidation>();

        foreach (var selector in selectors)
        {
            var validation = ValidateSelector(content, selector, analysis);
            results.Add(validation);
        }

        return results.OrderByDescending(r => r.MatchQuality).ThenBy(r => r.Selector.Priority).ToList();
    }

    /// <summary>
    /// Selects the best selector from validation results.
    /// </summary>
    public GeneratedSelector? SelectBestSelector(
        List<SelectorValidation> validations,
        float minConfidence = 0.5f)
    {
        var validResults = validations
            .Where(v => v.IsValid && v.MatchQuality >= minConfidence)
            .OrderByDescending(v => v.MatchQuality)
            .ThenBy(v => v.Selector.Priority)
            .ToList();

        if (validResults.Count == 0)
        {
            logger.LogWarning("No selectors met the minimum confidence threshold of {Threshold}", minConfidence);
            return null;
        }

        var best = validResults.First();
        logger.LogInformation("Selected best selector: {Selector} with quality {Quality}", 
            best.Selector.Selector, best.MatchQuality);

        return best.Selector;
    }

    /// <summary>
    /// Determines if refinement is needed based on validation results.
    /// </summary>
    public bool NeedsRefinement(List<SelectorValidation> validations, float minQuality = 0.6f)
    {
        var bestQuality = validations.Where(v => v.IsValid).Select(v => v.MatchQuality).DefaultIfEmpty(0).Max();
        return bestQuality < minQuality;
    }

    private SelectorValidation ValidateSelector(
        FetchedContent content,
        GeneratedSelector selector,
        ContentAnalysis analysis)
    {
        try
        {
            return selector.Type switch
            {
                SelectorType.CssSelector => ValidateCssSelector(content, selector, analysis),
                SelectorType.XPath => ValidateXPathSelector(content, selector, analysis),
                SelectorType.TextPattern => ValidateTextPattern(content, selector, analysis),
                _ => new SelectorValidation
                {
                    Selector = selector,
                    IsValid = false,
                    ValidationMessage = $"Unsupported selector type: {selector.Type}"
                }
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error validating selector {Selector}", selector.Selector);
            return new SelectorValidation
            {
                Selector = selector,
                IsValid = false,
                ValidationMessage = $"Validation error: {ex.Message}"
            };
        }
    }

    private SelectorValidation ValidateCssSelector(
        FetchedContent content,
        GeneratedSelector selector,
        ContentAnalysis analysis)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(content.Html ?? string.Empty);

        // Try the original selector first, then normalized alternatives
        var selectorsToTry = GetSelectorAlternatives(selector.Selector);
        
        foreach (var selectorString in selectorsToTry)
        {
            IEnumerable<HtmlNode> nodes;
            try
            {
                // Use Fizzler for native CSS selector support
                nodes = doc.DocumentNode.QuerySelectorAll(selectorString);
            }
            catch (Exception)
            {
                // Try next alternative
                continue;
            }

            var nodeList = nodes.ToList();
            if (nodeList.Count > 0)
            {
                // Found matches with this selector variant
                var firstNode = nodeList[0];
                var sample = CleanText(firstNode.InnerText);
                var quality = CalculateMatchQuality(sample, analysis, nodeList.Count);

                // Create a new selector with the working selector string
                var workingSelector = new GeneratedSelector
                {
                    Selector = selectorString,
                    Type = selector.Type,
                    Description = selector.Description,
                    Reasoning = selector.Reasoning,
                    Confidence = selector.Confidence,
                    Priority = selector.Priority
                };

                return new SelectorValidation
                {
                    Selector = workingSelector,
                    IsValid = true,
                    MatchCount = nodeList.Count,
                    ExtractedSample = TruncateText(sample, 500),
                    MatchQuality = quality,
                    ValidationMessage = nodeList.Count == 1 
                        ? "Unique match found" 
                        : $"Found {nodeList.Count} matches"
                };
            }
        }

        // No alternatives worked
        return new SelectorValidation
        {
            Selector = selector,
            IsValid = false,
            MatchCount = 0,
            ValidationMessage = "No elements matched (tried multiple selector variants)"
        };
    }

    /// <summary>
    /// Generate alternative selectors to handle CSS escape sequences that Fizzler may not support.
    /// </summary>
    private static IEnumerable<string> GetSelectorAlternatives(string selector)
    {
        // First, try the original
        yield return selector;
        
        // If selector has escaped characters, try attribute selectors for class matching
        if (selector.Contains('\\'))
        {
            // Extract simple class names and convert to attribute selectors
            // e.g., "div.lg\:flex.shadow" -> "[class*='shadow']"
            var simpleClasses = ExtractSimpleClassNames(selector);
            foreach (var className in simpleClasses)
            {
                yield return $"[class*='{className}']";
            }
            
            // Try stripping escaped parts and using remaining simple selectors
            var simplified = SimplifySelector(selector);
            if (!string.IsNullOrEmpty(simplified) && simplified != selector)
            {
                yield return simplified;
            }
        }
        
        // Try extracting just the tag name
        var tagMatch = TagNameRegex().Match(selector);
        if (tagMatch.Success)
        {
            yield return tagMatch.Value;
        }
    }

    /// <summary>
    /// Extract simple class names (without special characters) from a CSS selector.
    /// </summary>
    private static IEnumerable<string> ExtractSimpleClassNames(string selector)
    {
        // Match class names like .shadow, .bg-white (but not .lg\:flex)
        var matches = SimpleClassRegex().Matches(selector);
        foreach (Match match in matches)
        {
            var className = match.Groups[1].Value;
            // Skip classes with escaped characters
            if (!className.Contains('\\') && !className.Contains(':'))
            {
                yield return className;
            }
        }
    }

    /// <summary>
    /// Simplify a selector by removing escaped class names but keeping simple ones.
    /// </summary>
    private static string SimplifySelector(string selector)
    {
        // Remove escaped class selectors like .lg\:flex but keep .shadow
        var result = EscapedClassRegex().Replace(selector, "");
        // Clean up any remaining issues
        result = result.Replace(" >  > ", " > ").Replace("  ", " ").Trim();
        return result.TrimEnd('>').Trim();
    }

    [GeneratedRegex(@"^(\w+)")]
    private static partial Regex TagNameRegex();
    
    [GeneratedRegex(@"\.([a-zA-Z][\w-]*)")]
    private static partial Regex SimpleClassRegex();
    
    [GeneratedRegex(@"\.\w+\\[^.\s>]+")]
    private static partial Regex EscapedClassRegex();

    private SelectorValidation ValidateXPathSelector(
        FetchedContent content,
        GeneratedSelector selector,
        ContentAnalysis analysis)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(content.Html ?? string.Empty);

        HtmlNodeCollection? nodes;
        try
        {
            nodes = doc.DocumentNode.SelectNodes(selector.Selector);
        }
        catch (Exception ex)
        {
            return new SelectorValidation
            {
                Selector = selector,
                IsValid = false,
                ValidationMessage = $"Invalid XPath: {ex.Message}"
            };
        }

        if (nodes == null || nodes.Count == 0)
        {
            return new SelectorValidation
            {
                Selector = selector,
                IsValid = false,
                MatchCount = 0,
                ValidationMessage = "No elements matched"
            };
        }

        var firstNode = nodes[0];
        var sample = CleanText(firstNode.InnerText);
        var quality = CalculateMatchQuality(sample, analysis, nodes.Count);

        return new SelectorValidation
        {
            Selector = selector,
            IsValid = true,
            MatchCount = nodes.Count,
            ExtractedSample = TruncateText(sample, 500),
            MatchQuality = quality,
            ValidationMessage = nodes.Count == 1 
                ? "Unique match found" 
                : $"Found {nodes.Count} matches"
        };
    }

    private SelectorValidation ValidateTextPattern(
        FetchedContent content,
        GeneratedSelector selector,
        ContentAnalysis analysis)
    {
        var textContent = content.TextContent ?? contentExtractor.ExtractText(content.Html ?? "");

        try
        {
            var regex = new Regex(selector.Selector, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            var matches = regex.Matches(textContent);

            if (matches.Count == 0)
            {
                return new SelectorValidation
                {
                    Selector = selector,
                    IsValid = false,
                    MatchCount = 0,
                    ValidationMessage = "Pattern did not match any text"
                };
            }

            var sample = matches[0].Value;
            var quality = CalculateMatchQuality(sample, analysis, matches.Count);

            return new SelectorValidation
            {
                Selector = selector,
                IsValid = true,
                MatchCount = matches.Count,
                ExtractedSample = TruncateText(sample, 500),
                MatchQuality = quality,
                ValidationMessage = $"Pattern matched {matches.Count} times"
            };
        }
        catch (RegexParseException ex)
        {
            return new SelectorValidation
            {
                Selector = selector,
                IsValid = false,
                ValidationMessage = $"Invalid regex pattern: {ex.Message}"
            };
        }
    }

    private static float CalculateMatchQuality(string extractedContent, ContentAnalysis analysis, int matchCount)
    {
        float quality = 0.5f;

        // Prefer single matches (more specific)
        if (matchCount == 1)
            quality += 0.2f;
        else if (matchCount <= 5)
            quality += 0.1f;
        else if (matchCount > 20)
            quality -= 0.2f;

        // Content length check
        var contentLength = extractedContent.Length;
        if (contentLength >= 50 && contentLength <= 5000)
            quality += 0.15f;
        else if (contentLength < 10)
            quality -= 0.2f;
        else if (contentLength > 10000)
            quality -= 0.1f;

        // Check if content seems relevant to the content type
        var contentType = analysis.ContentType;
        var lowerContent = extractedContent.ToLowerInvariant();

        switch (contentType)
        {
            case ContentType.EventList:
                if (DateTimePattern().IsMatch(extractedContent))
                    quality += 0.1f;
                break;
            case ContentType.PriceInfo:
                if (PricePattern().IsMatch(extractedContent))
                    quality += 0.1f;
                break;
            case ContentType.NewsList:
                if (lowerContent.Contains("read more") || lowerContent.Contains("continue"))
                    quality += 0.05f;
                break;
        }

        return Math.Clamp(quality, 0f, 1f);
    }

    private static string CleanText(string text)
    {
        // Normalize whitespace
        text = WhitespacePattern().Replace(text, " ");
        return text.Trim();
    }

    private static string TruncateText(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        return text[..maxChars] + "...";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\d{1,2}[./\-]\d{1,2}[./\-]\d{2,4}|\d{4}[./\-]\d{1,2}[./\-]\d{1,2}|(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)", RegexOptions.IgnoreCase)]
    private static partial Regex DateTimePattern();

    [GeneratedRegex(@"[\$€£¥]?\s*\d+[.,]?\d*\s*(?:USD|EUR|GBP|CZK|Kč)?", RegexOptions.IgnoreCase)]
    private static partial Regex PricePattern();
}
