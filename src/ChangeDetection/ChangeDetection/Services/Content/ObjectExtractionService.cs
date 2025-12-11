using System.Text.Json;
using System.Text.Json.Serialization;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;

namespace ChangeDetection.Services.Content;

/// <summary>
/// Service for extracting structured objects from HTML using a schema.
/// Uses LLM for extraction with explicit failure handling - no silent fallbacks.
/// </summary>
public class ObjectExtractionService(
    ILlmProviderChain llmChain,
    ILogger<ObjectExtractionService> logger) : IObjectExtractionService
{
    /// <inheritdoc />
    public async Task<ObjectExtractionResult> ExtractAsync(
        string html,
        ExtractionSchema schema,
        CancellationToken ct = default)
    {
        logger.LogDebug("Extracting objects using schema with {FieldCount} fields", schema.Fields.Count);

        var result = new ObjectExtractionResult();

        try
        {
            // First, try to extract the items using the item selector
            var itemsHtml = ExtractItemsHtml(html, schema.ItemSelector);
            
            if (itemsHtml.Count == 0)
            {
                logger.LogWarning("No items found with selector: {Selector}", schema.ItemSelector);
                result.Success = false;
                result.Error = $"No items found with selector: {schema.ItemSelector}";
                result.DriftDetected = true;
                return result;
            }

            logger.LogDebug("Found {Count} items with item selector", itemsHtml.Count);

            // Build the extraction prompt for LLM
            var (objects, error) = await ExtractObjectsWithLlmAsync(itemsHtml, schema, ct);

            if (error != null)
            {
                result.Success = false;
                result.Error = error;
                return result;
            }

            if (objects == null || objects.Count == 0)
            {
                result.Success = false;
                result.Error = "LLM returned no objects";
                return result;
            }

            // Compute identity keys and check for duplicates
            var identityFields = schema.Fields
                .Where(f => f.IsIdentityField)
                .Select(f => f.Name)
                .ToList();

            if (identityFields.Count == 0)
            {
                // Use all identity field names from schema
                identityFields = schema.IdentityFieldNames;
            }

            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                obj.Index = i;
                obj.IdentityKey = ComputeIdentityKey(obj, identityFields);
            }

            // Check for duplicate identities
            var duplicates = objects
                .Where(o => o.IdentityKey != null)
                .GroupBy(o => o.IdentityKey)
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicates.Count > 0)
            {
                foreach (var dup in duplicates)
                {
                    var warning = $"Multiple objects ({dup.Count()}) share identity key: {dup.Key}";
                    result.AmbiguousIdentityWarnings.Add(warning);
                    logger.LogWarning("{Warning}", warning);
                }
            }

            result.Objects = objects;
            result.Success = true;

            logger.LogInformation("Successfully extracted {Count} objects with {WarningCount} warnings",
                objects.Count, result.AmbiguousIdentityWarnings.Count);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Object extraction failed");
            result.Success = false;
            result.Error = $"Extraction failed: {ex.Message}";
            return result;
        }
    }

    /// <inheritdoc />
    public async Task<SchemaValidationResult> ValidateSchemaAsync(
        string html,
        ExtractionSchema schema,
        CancellationToken ct = default)
    {
        var result = new SchemaValidationResult();

        try
        {
            // Check if item selector still matches
            var itemsHtml = ExtractItemsHtml(html, schema.ItemSelector);
            result.ItemCount = itemsHtml.Count;

            if (itemsHtml.Count == 0)
            {
                result.IsValid = false;
                result.DriftDetected = true;
                result.Issues.Add($"Item selector '{schema.ItemSelector}' no longer matches any elements");
                return result;
            }

            // Sample a few items and check if required fields can be extracted
            var sampleItems = itemsHtml.Take(3).ToList();
            var missingFields = new HashSet<string>();

            foreach (var itemHtml in sampleItems)
            {
                foreach (var field in schema.Fields.Where(f => f.IsRequired))
                {
                    var value = ExtractFieldValue(itemHtml, field);
                    if (string.IsNullOrEmpty(value))
                    {
                        missingFields.Add(field.Name);
                    }
                }
            }

            result.MissingFields = missingFields.ToList();

            if (missingFields.Count > 0)
            {
                result.DriftDetected = true;
                result.Issues.Add($"Required fields not found: {string.Join(", ", missingFields)}");
            }

            result.IsValid = !result.DriftDetected;
            result.Confidence = result.IsValid ? 1.0f : 0.5f;

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Schema validation failed");
            result.IsValid = false;
            result.Issues.Add($"Validation error: {ex.Message}");
            return result;
        }
    }

    private List<string> ExtractItemsHtml(string html, string itemSelector)
    {
        var items = new List<string>();

        try
        {
            // Use content extractor to find items
            // For now, a simplified approach - in production, would use HtmlAgilityPack directly
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);

            // Try CSS selector first (simple conversion to XPath)
            var nodes = TrySelectNodes(doc, itemSelector);

            if (nodes != null)
            {
                foreach (var node in nodes)
                {
                    items.Add(node.OuterHtml);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract items with selector: {Selector}", itemSelector);
        }

        return items;
    }

    private static HtmlAgilityPack.HtmlNodeCollection? TrySelectNodes(
        HtmlAgilityPack.HtmlDocument doc,
        string selector)
    {
        // If it looks like XPath, use directly
        if (selector.StartsWith('/') || selector.StartsWith("//"))
        {
            return doc.DocumentNode.SelectNodes(selector);
        }

        // Convert simple CSS selectors to XPath
        var xpath = CssToXPath(selector);
        return doc.DocumentNode.SelectNodes(xpath);
    }

    private static string CssToXPath(string css)
    {
        // Simple CSS to XPath conversion for common patterns
        var xpath = css.Trim();

        // Class selector: .class
        if (xpath.StartsWith('.'))
        {
            var className = xpath[1..];
            return $"//*[contains(concat(' ', normalize-space(@class), ' '), ' {className} ')]";
        }

        // ID selector: #id
        if (xpath.StartsWith('#'))
        {
            var id = xpath[1..];
            return $"//*[@id='{id}']";
        }

        // Element with class: element.class
        if (xpath.Contains('.'))
        {
            var parts = xpath.Split('.', 2);
            var element = parts[0];
            var className = parts[1];
            return $"//{element}[contains(concat(' ', normalize-space(@class), ' '), ' {className} ')]";
        }

        // Simple element selector
        return $"//{xpath}";
    }

    private string? ExtractFieldValue(string itemHtml, SchemaField field)
    {
        try
        {
            var doc = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(itemHtml);

            // Parse selector - might have @attribute suffix
            var selector = field.Selector;
            string? attribute = null;

            if (selector.Contains('@'))
            {
                var parts = selector.Split('@', 2);
                selector = parts[0].TrimEnd();
                attribute = parts[1];
            }

            var nodes = TrySelectNodes(doc, selector);
            if (nodes == null || nodes.Count == 0)
                return null;

            var node = nodes[0];

            if (!string.IsNullOrEmpty(attribute))
            {
                return node.GetAttributeValue(attribute, null!);
            }

            return field.Type == FieldType.Html
                ? node.InnerHtml
                : node.InnerText.Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task<(List<ExtractedObject>? Objects, string? Error)> ExtractObjectsWithLlmAsync(
        List<string> itemsHtml,
        ExtractionSchema schema,
        CancellationToken ct)
    {
        var fieldsList = string.Join("\n", schema.Fields.Select(f =>
            $"  - {f.Name} ({f.Type}): selector '{f.Selector}'{(f.IsRequired ? " [required]" : "")}"));

        var systemPrompt = $$"""
            You are a data extraction specialist. Extract structured objects from the provided HTML items.
            
            Schema:
            - Item selector: {{schema.ItemSelector}}
            - Fields:
            {{fieldsList}}
            
            For each item, extract the values for each field. Handle missing fields gracefully.
            For URL fields, ensure you extract the full href attribute.
            For date fields, normalize to ISO 8601 format if possible.
            For number fields, extract only the numeric value.
            
            Respond with a JSON array of objects. Each object should have the field names as keys:
            [
                {"fieldName1": "value1", "fieldName2": "value2"},
                ...
            ]
            
            Only output the JSON array, no additional text.
            """;

        // Limit number of items to avoid token limits
        var sampleItems = itemsHtml.Take(50).ToList();
        var itemsText = string.Join("\n\n---ITEM---\n\n", sampleItems);

        var userPrompt = $$"""
            Extract objects from these {{sampleItems.Count}} HTML items:
            
            {{itemsText}}
            """;

        var response = await llmChain.ExecuteAsync(
            $"{systemPrompt}\n\nUser: {userPrompt}",
            new LlmRequestOptions
            {
                Temperature = 0.1f,
                MaxTokens = 4096,
                ExpectJson = true,
                UsageType = LlmUsageType.ObjectExtraction
            },
            ct);

        if (!response.IsSuccess)
        {
            return (null, $"LLM extraction failed: {response.ErrorMessage}");
        }

        try
        {
            var jsonContent = ExtractJsonArray(response.Content ?? "");
            var rawObjects = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(
                jsonContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (rawObjects == null)
            {
                return (null, "Failed to parse LLM response as JSON array");
            }

            var objects = rawObjects.Select(dict =>
            {
                var obj = new ExtractedObject();
                foreach (var kvp in dict)
                {
                    obj.Fields[kvp.Key] = kvp.Value.ValueKind == JsonValueKind.Null
                        ? null
                        : kvp.Value.ToString();
                }
                return obj;
            }).ToList();

            return (objects, null);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse LLM response: {Content}", response.Content);
            return (null, $"Failed to parse extraction response: {ex.Message}");
        }
    }

    private static string ExtractJsonArray(string content)
    {
        // Try to extract JSON array from markdown code blocks or raw content
        content = content.Trim();

        // Check for markdown code block
        if (content.Contains("```"))
        {
            var start = content.IndexOf('[');
            var end = content.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                return content[start..(end + 1)];
            }
        }

        // Already a JSON array
        if (content.StartsWith('[') && content.EndsWith(']'))
        {
            return content;
        }

        // Try to find array in content
        var arrayStart = content.IndexOf('[');
        var arrayEnd = content.LastIndexOf(']');
        if (arrayStart >= 0 && arrayEnd > arrayStart)
        {
            return content[arrayStart..(arrayEnd + 1)];
        }

        return content;
    }

    private static string? ComputeIdentityKey(ExtractedObject obj, List<string> identityFields)
    {
        if (identityFields.Count == 0)
            return null;

        var values = identityFields
            .Select(f => obj.Fields.GetValueOrDefault(f) ?? "")
            .Where(v => !string.IsNullOrEmpty(v))
            .ToList();

        if (values.Count == 0)
            return null;

        return string.Join("|", values);
    }
}
