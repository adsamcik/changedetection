using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Entities;
using ChangeDetection.Services;
using Fizzler.Systems.HtmlAgilityPack;
using HtmlAgilityPack;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Extraction;

/// <summary>
/// Extracts structured data from HTML using a schema of CSS selectors.
/// </summary>
public class ExtractSchemaBlock : IPipelineBlock
{
    public string BlockType => "ExtractSchema";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
    public bool IsCacheable => true;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("html", out var htmlElement))
            return BlockResult.Failed("ExtractSchema block requires an 'html' input.");

        var html = htmlElement.ValueKind == JsonValueKind.String
            ? htmlElement.GetString()
            : htmlElement.TryGetProperty("html", out var nested) ? nested.GetString() : null;

        if (string.IsNullOrWhiteSpace(html))
            return BlockResult.Failed("ExtractSchema block received empty or invalid HTML.");

        var (scope, schema, preferStructuredData, enableLlmFallback, listMode) = ReadConfig(context);

        if (schema is null || schema.Count == 0)
            return BlockResult.Failed("No extraction schema configured.");

        // List mode: scope selects repeating items, schema fields extracted per item → array output
        if (listMode && scope is not null)
            return await ExecuteListModeAsync(context, html, scope, schema, preferStructuredData, enableLlmFallback);

        context.Logger.LogInformation("ExtractSchemaBlock: Extracting {FieldCount} fields (single mode)", schema.Count);

        var extractor = context.Services.GetRequiredService<IContentExtractor>();
        var structuredDataExtractor = context.Services.GetRequiredService<IStructuredDataExtractor>();

        var scopedHtml = html;
        if (scope is not null)
        {
            var narrowed = extractor.ExtractHtml(html, cssSelector: scope);
            if (!string.IsNullOrEmpty(narrowed))
                scopedHtml = narrowed;
        }

        var data = new Dictionary<string, string?>();
        var sources = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in schema)
        {
            string? value = null;
            string? source = null;

            if (preferStructuredData)
            {
                var structuredResult = structuredDataExtractor.TryExtractFieldWithSource(html, field.Field);
                value = structuredResult.Value;
                source = structuredResult.Source;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                var cssValue = extractor.ExtractText(scopedHtml, cssSelector: field.Selector);
                if (!string.IsNullOrWhiteSpace(cssValue))
                {
                    value = cssValue;
                    source = "css";
                }
            }

            if (string.IsNullOrWhiteSpace(value) && enableLlmFallback)
            {
                var llmValue = await TryExtractWithLlmAsync(context, scopedHtml, field.Field);
                if (!string.IsNullOrWhiteSpace(llmValue))
                {
                    value = llmValue;
                    source = "llm";
                }
            }

            data[field.Field] = string.IsNullOrWhiteSpace(value) ? null : value;
            sources[field.Field] = source;
            data[$"{field.Field}_source"] = source;
        }

        foreach (var (field, value) in data)
        {
            if (field.EndsWith("_source", StringComparison.OrdinalIgnoreCase))
                continue;

            var truncated = value is not null && value.Length > 100 ? value[..100] + "…" : value;
            sources.TryGetValue(field, out var source);
            context.Logger.LogInformation("ExtractSchemaBlock: {Field} ({Source}) = {Value}", field, source ?? "unresolved", truncated ?? "(null)");
        }

        var extractedCount = data.Count(kvp =>
            !kvp.Key.EndsWith("_source", StringComparison.OrdinalIgnoreCase)
            && kvp.Value is not null);
        data["_meta_extractedCount"] = extractedCount.ToString();
        data["_meta_totalFields"] = schema.Count.ToString();

        var output = JsonSerializer.SerializeToElement(data);
        return await Task.FromResult(BlockResult.Succeeded(output));
    }

    /// <summary>
    /// List mode: scope selects repeating container elements, schema fields are extracted per element.
    /// Output is a JSON array of objects, one per matched scope element.
    /// </summary>
    private async Task<BlockResult> ExecuteListModeAsync(
        BlockContext context,
        string html,
        string scope,
        IReadOnlyList<SchemaField> schema,
        bool preferStructuredData,
        bool enableLlmFallback)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);

        List<HtmlNode>? scopeNodes;
        try
        {
            scopeNodes = doc.DocumentNode.QuerySelectorAll(scope)?.ToList();
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(ex, "ExtractSchemaBlock (list mode): CSS selector '{Scope}' failed, trying fallback", scope);
            // Fallback: try splitting comma-separated selectors and use the simplest one
            try
            {
                var fallbackSelector = scope.Split(',').Last().Trim();
                scopeNodes = doc.DocumentNode.QuerySelectorAll(fallbackSelector)?.ToList();
            }
            catch
            {
                return BlockResult.Failed($"ExtractSchema list mode: CSS selector '{scope}' is not supported.");
            }
        }

        if (scopeNodes is null || scopeNodes.Count == 0)
        {
            context.Logger.LogWarning("ExtractSchemaBlock (list mode): No elements matched scope '{Scope}'", scope);
            // Return empty array so downstream ListDiff still works
            return BlockResult.Succeeded(JsonSerializer.SerializeToElement(Array.Empty<object>()));
        }

        context.Logger.LogInformation(
            "ExtractSchemaBlock (list mode): Found {Count} elements matching scope '{Scope}', extracting {Fields} fields each",
            scopeNodes.Count, schema.Count);

        var extractor = context.Services.GetRequiredService<IContentExtractor>();
        var items = new List<Dictionary<string, string?>>();

        foreach (var scopeNode in scopeNodes)
        {
            var itemHtml = scopeNode.OuterHtml;
            var item = new Dictionary<string, string?>();

            foreach (var field in schema)
            {
                string? value = null;

                // Try CSS selector within this scope element
                try
                {
                    var fieldNode = scopeNode.QuerySelector(field.Selector);
                    if (fieldNode is not null)
                    {
                        // Check the matched node's element name (not the selector string)
                        // to decide whether to extract href vs text content.
                        value = fieldNode.Name.Equals("a", StringComparison.OrdinalIgnoreCase) || field.Selector.Contains("[href]")
                            ? fieldNode.GetAttributeValue("href", null) ?? fieldNode.InnerText?.Trim()
                            : fieldNode.InnerText?.Trim();
                    }
                }
                catch
                {
                    // Fall back to IContentExtractor
                    var cssValue = extractor.ExtractText(itemHtml, cssSelector: field.Selector);
                    if (!string.IsNullOrWhiteSpace(cssValue))
                        value = cssValue;
                }

                // If field selector didn't match, try common patterns for link-based extraction
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (field.Field.Equals("url", StringComparison.OrdinalIgnoreCase) ||
                        field.Field.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                        field.Field.Equals("link", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract href from first <a> in scope
                        var linkNode = scopeNode.QuerySelector("a[href]");
                        value = linkNode?.GetAttributeValue("href", null);
                    }
                    else if (field.Field.Equals("title", StringComparison.OrdinalIgnoreCase) ||
                             field.Field.Equals("name", StringComparison.OrdinalIgnoreCase))
                    {
                        // Extract text from first heading or link
                        var textNode = scopeNode.QuerySelector("h1, h2, h3, h4, a");
                        value = textNode?.InnerText?.Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(value) && enableLlmFallback && items.Count < 3)
                {
                    // Only use LLM fallback on first 3 items to avoid excessive API calls
                    value = await TryExtractWithLlmAsync(context, itemHtml, field.Field);
                }

                item[field.Field] = string.IsNullOrWhiteSpace(value) ? null : HtmlEntity.DeEntitize(value);
            }

            // Auto-extract url from any link in the scope element if not already found
            if (string.IsNullOrWhiteSpace(item.GetValueOrDefault("url")))
            {
                var autoLink = scopeNode.QuerySelector("a[href]");
                if (autoLink is not null)
                    item["url"] = autoLink.GetAttributeValue("href", null);
            }

            // Auto-extract text as title if not already found
            if (string.IsNullOrWhiteSpace(item.GetValueOrDefault("title")))
            {
                var autoTitle = scopeNode.QuerySelector("h1, h2, h3, h4, a")?.InnerText?.Trim();
                if (!string.IsNullOrWhiteSpace(autoTitle))
                    item["title"] = HtmlEntity.DeEntitize(autoTitle);
            }

            // Skip items with no useful data
            if (item.Values.All(v => string.IsNullOrWhiteSpace(v)))
                continue;

            items.Add(item);
        }

        context.Logger.LogInformation(
            "ExtractSchemaBlock (list mode): Extracted {ItemCount} items with data out of {TotalNodes} scope matches",
            items.Count, scopeNodes.Count);

        var output = JsonSerializer.SerializeToElement(items);
        return BlockResult.Succeeded(output);
    }

    private static (string? scope, IReadOnlyList<SchemaField>? schema, bool preferStructuredData, bool enableLlmFallback, bool listMode) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null, false, true, false);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null, false, true, false);

        string? scope = null;
        if (config.TryGetProperty("scope", out var scopeElem) && scopeElem.ValueKind == JsonValueKind.String)
            scope = scopeElem.GetString();

        var preferStructuredData = false;
        if (config.TryGetProperty("preferStructuredData", out var preferElem) &&
            (preferElem.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            preferStructuredData = preferElem.GetBoolean();
        }

        var enableLlmFallback = true;
        if (config.TryGetProperty("enableLlmFallback", out var llmElem) &&
            (llmElem.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            enableLlmFallback = llmElem.GetBoolean();
        }

        var listMode = false;
        if (config.TryGetProperty("listMode", out var listModeElem) &&
            (listModeElem.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            listMode = listModeElem.GetBoolean();
        }

        List<SchemaField>? fields = null;
        if (config.TryGetProperty("schema", out var schemaElem) && schemaElem.ValueKind == JsonValueKind.Array)
        {
            fields = [];
            foreach (var item in schemaElem.EnumerateArray())
            {
                if (item.TryGetProperty("field", out var fieldName) &&
                    item.TryGetProperty("selector", out var selector) &&
                    fieldName.ValueKind == JsonValueKind.String &&
                    selector.ValueKind == JsonValueKind.String)
                {
                    fields.Add(new SchemaField(fieldName.GetString()!, selector.GetString()!));
                }
            }
        }

        return (scope, fields, preferStructuredData, enableLlmFallback, listMode);
    }

    private record SchemaField(string Field, string Selector);

    private static async Task<string?> TryExtractWithLlmAsync(BlockContext context, string html, string fieldName)
    {
        var llmChain = context.Services.GetService<ILlmProviderChain>();
        if (llmChain is null)
            return null;

        var prompt = BuildLlmFallbackPrompt(fieldName, html);
        var options = new LlmRequestOptions
        {
            ExpectJson = true,
            UsageType = LlmUsageType.ObjectExtraction,
            WatchedSiteId = context.WatchId,
            Temperature = 0.1f,
            MaxTokens = 256
        };

        try
        {
            var response = await llmChain.ExecuteAsync(prompt, options, context.CancellationToken);
            if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Content))
                return null;

            using var doc = JsonDocument.Parse(response.Content);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(fieldName, out var fieldValue))
            {
                return fieldValue.ValueKind == JsonValueKind.String
                    ? fieldValue.GetString()
                    : fieldValue.ToString();
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildLlmFallbackPrompt(string fieldName, string html)
    {
        var sanitizedHtml = PromptSanitizer.Sanitize(html, "page_content");
        return $"""
            Extract the value for the field "{fieldName}" from the HTML content below.
            Return a JSON object with exactly one property named "{fieldName}".
            If the value is missing, return null for that property.

            Content to analyze:
            {sanitizedHtml}
            """;
    }
}
