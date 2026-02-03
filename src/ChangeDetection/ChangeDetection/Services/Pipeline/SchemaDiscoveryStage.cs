using System.Runtime.CompilerServices;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Pipeline;

/// <summary>
/// Stage 6: Uses LLM to discover extraction schema for list-type content.
/// Identifies repeating item containers and the fields within each item.
/// Only executed when ContentType indicates structured list content.
/// </summary>
public class SchemaDiscoveryStage(
    ILlmProviderChain llmChain,
    ILogger<SchemaDiscoveryStage> logger)
{
    /// <summary>
    /// Content types that should trigger schema discovery.
    /// </summary>
    private static readonly HashSet<ContentType> ListContentTypes =
    [
        ContentType.EventList,
        ContentType.ProductListing,
        ContentType.NewsList,
        ContentType.Table,
        ContentType.Feed,
        ContentType.Calendar
    ];

    /// <summary>
    /// Determines if schema discovery should be executed for this content type.
    /// </summary>
    public static bool ShouldDiscoverSchema(ContentType contentType)
        => ListContentTypes.Contains(contentType);

    /// <summary>
    /// Discovers the extraction schema for list-type content.
    /// </summary>
    public async Task<DiscoveredSchema?> DiscoverAsync(
        FetchedContent content,
        ContentAnalysis analysis,
        CancellationToken ct = default)
    {
        DiscoveredSchema? result = null;
        await foreach (var progress in DiscoverStreamingAsync(content, analysis, ct))
        {
            if (progress.Result != null)
            {
                result = progress.Result;
            }
        }
        return result;
    }

    /// <summary>
    /// Discovers the extraction schema with streaming progress.
    /// </summary>
    public async IAsyncEnumerable<SchemaDiscoveryProgress> DiscoverStreamingAsync(
        FetchedContent content,
        ContentAnalysis analysis,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!ShouldDiscoverSchema(analysis.ContentType))
        {
            logger.LogDebug("Skipping schema discovery for content type: {ContentType}", analysis.ContentType);
            yield break;
        }

        logger.LogInformation("Discovering schema for {Url} (content type: {ContentType})",
            content.Url, analysis.ContentType);

        yield return new SchemaDiscoveryProgress
        {
            Step = "ItemContainerDiscovery",
            Status = "Starting",
            Summary = "Identifying repeating item containers..."
        };

        // Step 1: Discover the item container selector
        string? itemSelector = null;
        int sampleItemCount = 0;

        await foreach (var chunk in DiscoverItemContainerAsync(content, analysis, ct))
        {
            if (chunk.IsThinking)
            {
                yield return new SchemaDiscoveryProgress
                {
                    Step = "ItemContainerDiscovery",
                    Status = "Thinking",
                    ThinkingContent = chunk.Content
                };
            }
            else if (chunk.Result != null)
            {
                itemSelector = chunk.Result.Selector;
                sampleItemCount = chunk.Result.ItemCount;
            }
        }

        if (string.IsNullOrEmpty(itemSelector))
        {
            logger.LogWarning("Failed to discover item container selector");
            yield return new SchemaDiscoveryProgress
            {
                Step = "ItemContainerDiscovery",
                Status = "Failed",
                Summary = "Could not identify repeating items on the page"
            };
            yield break;
        }

        yield return new SchemaDiscoveryProgress
        {
            Step = "ItemContainerDiscovery",
            Status = "Completed",
            Summary = $"Found {sampleItemCount} items with selector: {itemSelector}"
        };

        // Step 2: Discover fields within each item
        yield return new SchemaDiscoveryProgress
        {
            Step = "FieldDiscovery",
            Status = "Starting",
            Summary = "Analyzing fields within each item..."
        };

        List<DiscoveredField> fields = [];
        await foreach (var chunk in DiscoverFieldsAsync(content, itemSelector, analysis, ct))
        {
            if (chunk.IsThinking)
            {
                yield return new SchemaDiscoveryProgress
                {
                    Step = "FieldDiscovery",
                    Status = "Thinking",
                    ThinkingContent = chunk.Content
                };
            }
            else if (chunk.Result != null)
            {
                fields = chunk.Result;
            }
        }

        yield return new SchemaDiscoveryProgress
        {
            Step = "FieldDiscovery",
            Status = "Completed",
            Summary = $"Discovered {fields.Count} fields"
        };

        // Step 3: Infer identity fields
        var identityFields = InferIdentityFields(fields, analysis.ContentType);

        // Calculate overall confidence
        var confidence = CalculateConfidence(fields, sampleItemCount);

        var schema = new DiscoveredSchema
        {
            ItemSelector = itemSelector,
            Fields = fields,
            InferredIdentityFields = identityFields,
            Confidence = confidence,
            SampleItemCount = sampleItemCount,
            ContentType = analysis.ContentType.ToString(),
            Explanation = BuildExplanation(analysis.ContentType, fields.Count, sampleItemCount)
        };

        logger.LogInformation("Discovered schema with {FieldCount} fields, {ItemCount} items, confidence: {Confidence:P0}",
            fields.Count, sampleItemCount, confidence);

        yield return new SchemaDiscoveryProgress
        {
            Step = "Complete",
            Status = "Completed",
            Summary = $"Schema discovered: {fields.Count} fields across {sampleItemCount} items",
            Result = schema
        };
    }

    private async IAsyncEnumerable<StreamingResult<ItemContainerResult>> DiscoverItemContainerAsync(
        FetchedContent content,
        ContentAnalysis analysis,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var htmlSample = TruncateText(content.CleanedHtml ?? content.Html ?? "", 12000);
        var contentTypeHint = GetContentTypeHint(analysis.ContentType);

        var prompt = $$"""
            Find the CSS selector for the REPEATING ITEM CONTAINER in this HTML.
            
            Content type: {{analysis.ContentType}}
            User wants to track: {{analysis.UserIntent}}
            
            Look for: {{contentTypeHint}}
            
            HTML:
            {{htmlSample}}
            
            Return JSON only: {"selector": "css-selector", "itemCount": number}
            
            Rules:
            - The selector should match ALL repeating items (not just the first one)
            - Prefer class-based selectors over tag-only selectors
            - Use data attributes if available (e.g., [data-item], [data-event])
            - Count how many items match the selector
            """;

        var fullContent = new System.Text.StringBuilder();

        await foreach (var chunk in llmChain.ExecuteStreamingAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.1f,
            MaxTokens = 150,
            UsageType = LlmUsageType.SchemaDiscovery,
            ExpectJson = true
        }, ct))
        {
            if (chunk.Type == LlmStreamChunkType.Content && !string.IsNullOrEmpty(chunk.Text))
            {
                fullContent.Append(chunk.Text);
                yield return new StreamingResult<ItemContainerResult> { IsThinking = true, Content = chunk.Text };
            }
        }

        var result = ParseItemContainerResult(fullContent.ToString());
        yield return new StreamingResult<ItemContainerResult> { Result = result };
    }

    private async IAsyncEnumerable<StreamingResult<List<DiscoveredField>>> DiscoverFieldsAsync(
        FetchedContent content,
        string itemSelector,
        ContentAnalysis analysis,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var htmlSample = TruncateText(content.CleanedHtml ?? content.Html ?? "", 10000);
        var fieldHints = GetFieldHints(analysis.ContentType);

        var prompt = $$"""
            Analyze the fields within each item selected by "{{itemSelector}}".
            
            Content type: {{analysis.ContentType}}
            Expected fields: {{fieldHints}}
            
            HTML:
            {{htmlSample}}
            
            For each field found, provide:
            - name: descriptive name (e.g., "Title", "Price", "Date")
            - type: one of [String, Date, Url, Number, Currency, Image, Html]
            - selector: CSS selector RELATIVE to the item container
            - isRequired: true if appears in all items
            - isIdentity: true if helps uniquely identify items
            - sampleValue: example value from the page
            
            Return JSON array only: [{"name":"x","type":"String","selector":"css","isRequired":true,"isIdentity":false,"sampleValue":"x"}]
            
            Limit to 10 most important fields.
            """;

        var fullContent = new System.Text.StringBuilder();

        await foreach (var chunk in llmChain.ExecuteStreamingAsync(prompt, new LlmRequestOptions
        {
            Temperature = 0.2f,
            MaxTokens = 800,
            UsageType = LlmUsageType.SchemaDiscovery,
            ExpectJson = true
        }, ct))
        {
            if (chunk.Type == LlmStreamChunkType.Content && !string.IsNullOrEmpty(chunk.Text))
            {
                fullContent.Append(chunk.Text);
                yield return new StreamingResult<List<DiscoveredField>> { IsThinking = true, Content = chunk.Text };
            }
        }

        var fields = ParseFieldsResult(fullContent.ToString());
        yield return new StreamingResult<List<DiscoveredField>> { Result = fields };
    }

    private ItemContainerResult? ParseItemContainerResult(string response)
    {
        try
        {
            var json = CleanJsonResponse(response);
            var result = JsonSerializer.Deserialize<ItemContainerDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result != null && !string.IsNullOrEmpty(result.Selector))
            {
                return new ItemContainerResult
                {
                    Selector = result.Selector,
                    ItemCount = result.ItemCount
                };
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse item container JSON: {Content}", response);
        }

        return null;
    }

    private List<DiscoveredField> ParseFieldsResult(string response)
    {
        try
        {
            var json = CleanJsonResponse(response);
            var dtos = JsonSerializer.Deserialize<List<FieldDto>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return dtos?.Select(dto => new DiscoveredField
            {
                Name = dto.Name ?? "Unknown",
                Type = dto.Type ?? "String",
                Selector = dto.Selector ?? "",
                IsRequired = dto.IsRequired,
                IsIdentityField = dto.IsIdentity,
                SampleValues = string.IsNullOrEmpty(dto.SampleValue) ? [] : [dto.SampleValue],
                Confidence = 0.7f // Default confidence for LLM-discovered fields
            }).ToList() ?? [];
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse fields JSON: {Content}", response);
            return [];
        }
    }

    private static List<string> InferIdentityFields(List<DiscoveredField> fields, ContentType contentType)
    {
        // Use fields marked as identity by LLM
        var identityFields = fields
            .Where(f => f.IsIdentityField)
            .Select(f => f.Name)
            .ToList();

        // If none marked, infer based on content type
        if (identityFields.Count == 0)
        {
            identityFields = contentType switch
            {
                ContentType.EventList => fields
                    .Where(f => f.Name.Contains("title", StringComparison.OrdinalIgnoreCase) ||
                               f.Name.Contains("date", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Name)
                    .Take(2)
                    .ToList(),
                ContentType.ProductListing => fields
                    .Where(f => f.Name.Contains("name", StringComparison.OrdinalIgnoreCase) ||
                               f.Name.Contains("title", StringComparison.OrdinalIgnoreCase) ||
                               f.Name.Contains("sku", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Name)
                    .Take(1)
                    .ToList(),
                _ => fields
                    .Where(f => f.IsRequired && f.Type == "String")
                    .Select(f => f.Name)
                    .Take(1)
                    .ToList()
            };
        }

        return identityFields;
    }

    private static float CalculateConfidence(List<DiscoveredField> fields, int itemCount)
    {
        float confidence = 0.5f;

        // More fields = higher confidence (up to a point)
        if (fields.Count >= 3) confidence += 0.1f;
        if (fields.Count >= 5) confidence += 0.1f;

        // More items = higher confidence
        if (itemCount >= 3) confidence += 0.1f;
        if (itemCount >= 10) confidence += 0.1f;

        // Required fields present = higher confidence
        if (fields.Any(f => f.IsRequired)) confidence += 0.1f;

        return Math.Min(1.0f, confidence);
    }

    private static string GetContentTypeHint(ContentType contentType) => contentType switch
    {
        ContentType.EventList => "Event cards, event rows, calendar entries with dates and titles",
        ContentType.ProductListing => "Product cards, product grid items, shopping items with prices",
        ContentType.NewsList => "Article cards, news items, blog post entries",
        ContentType.Table => "Table rows (tr), data rows within a table body",
        ContentType.Feed => "Feed items, post entries, timeline items",
        ContentType.Calendar => "Calendar events, date entries, schedule items",
        _ => "Repeating content items or list entries"
    };

    private static string GetFieldHints(ContentType contentType) => contentType switch
    {
        ContentType.EventList => "Title, Date, Time, Location, Description, URL, Image",
        ContentType.ProductListing => "Name, Price, Image, Description, URL, Stock Status, Rating",
        ContentType.NewsList => "Title, Date, Author, Summary, URL, Image",
        ContentType.Table => "Column values matching table headers",
        ContentType.Feed => "Title, Content, Author, Date, URL",
        ContentType.Calendar => "Event Name, Date, Time, Description",
        _ => "Title, Description, URL, Date, Price"
    };

    private static string BuildExplanation(ContentType contentType, int fieldCount, int itemCount)
    {
        var typeName = contentType switch
        {
            ContentType.EventList => "events",
            ContentType.ProductListing => "products",
            ContentType.NewsList => "articles",
            ContentType.Table => "rows",
            ContentType.Feed => "items",
            ContentType.Calendar => "calendar entries",
            _ => "items"
        };

        return $"Found {itemCount} {typeName} with {fieldCount} trackable fields. " +
               "Changes to individual items will be detected and reported.";
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

        // Find JSON object or array
        var objectStart = json.IndexOf('{');
        var arrayStart = json.IndexOf('[');

        if (objectStart >= 0 && (arrayStart < 0 || objectStart < arrayStart))
        {
            var end = json.LastIndexOf('}');
            if (end > objectStart)
                json = json[objectStart..(end + 1)];
        }
        else if (arrayStart >= 0)
        {
            var end = json.LastIndexOf(']');
            if (end > arrayStart)
                json = json[arrayStart..(end + 1)];
        }

        return json;
    }

    private class ItemContainerDto
    {
        public string? Selector { get; set; }
        public int ItemCount { get; set; }
    }

    private class ItemContainerResult
    {
        public required string Selector { get; set; }
        public int ItemCount { get; set; }
    }

    private class FieldDto
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Selector { get; set; }
        public bool IsRequired { get; set; }
        public bool IsIdentity { get; set; }
        public string? SampleValue { get; set; }
    }
}

/// <summary>
/// Progress update during schema discovery.
/// </summary>
public class SchemaDiscoveryProgress
{
    /// <summary>Discovery step name.</summary>
    public required string Step { get; init; }

    /// <summary>Step status: Starting, Thinking, Completed, Failed.</summary>
    public required string Status { get; init; }

    /// <summary>Human-readable summary.</summary>
    public string? Summary { get; init; }

    /// <summary>Streamed thinking content from LLM.</summary>
    public string? ThinkingContent { get; init; }

    /// <summary>Final discovered schema (only on complete).</summary>
    public DiscoveredSchema? Result { get; init; }
}
