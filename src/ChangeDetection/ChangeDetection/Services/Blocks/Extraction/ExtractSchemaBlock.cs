using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Entities;
using ChangeDetection.Services;
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

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("html", out var htmlElement))
            return BlockResult.Failed("ExtractSchema block requires an 'html' input.");

        var html = htmlElement.ValueKind == JsonValueKind.String
            ? htmlElement.GetString()
            : htmlElement.TryGetProperty("html", out var nested) ? nested.GetString() : null;

        if (string.IsNullOrWhiteSpace(html))
            return BlockResult.Failed("ExtractSchema block received empty or invalid HTML.");

        var (scope, schema, preferStructuredData, enableLlmFallback) = ReadConfig(context);

        if (schema is null || schema.Count == 0)
            return BlockResult.Failed("No extraction schema configured.");

        context.Logger.LogInformation("ExtractSchemaBlock: Extracting {FieldCount} fields", schema.Count);

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

        var output = JsonSerializer.SerializeToElement(data);
        return await Task.FromResult(BlockResult.Succeeded(output));
    }

    private static (string? scope, IReadOnlyList<SchemaField>? schema, bool preferStructuredData, bool enableLlmFallback) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null, false, true);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null, false, true);

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

        return (scope, fields, preferStructuredData, enableLlmFallback);
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
