using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
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

        var (scope, schema) = ReadConfig(context);

        if (schema is null || schema.Count == 0)
            return BlockResult.Failed("No extraction schema configured.");

        context.Logger.LogInformation("ExtractSchemaBlock: Extracting {FieldCount} fields", schema.Count);

        var extractor = context.Services.GetRequiredService<IContentExtractor>();

        var scopedHtml = html;
        if (scope is not null)
        {
            var narrowed = extractor.ExtractHtml(html, cssSelector: scope);
            if (!string.IsNullOrEmpty(narrowed))
                scopedHtml = narrowed;
        }

        var data = new Dictionary<string, string?>();
        foreach (var field in schema)
        {
            var value = extractor.ExtractText(scopedHtml, cssSelector: field.Selector);
            data[field.Field] = string.IsNullOrEmpty(value) ? null : value;
        }

        foreach (var (field, value) in data)
        {
            var truncated = value is not null && value.Length > 100 ? value[..100] + "…" : value;
            context.Logger.LogInformation("ExtractSchemaBlock: {Field} = {Value}", field, truncated ?? "(null)");
        }

        var output = JsonSerializer.SerializeToElement(data);
        return await Task.FromResult(BlockResult.Succeeded(output));
    }

    private static (string? scope, IReadOnlyList<SchemaField>? schema) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null);

        string? scope = null;
        if (config.TryGetProperty("scope", out var scopeElem) && scopeElem.ValueKind == JsonValueKind.String)
            scope = scopeElem.GetString();

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

        return (scope, fields);
    }

    private record SchemaField(string Field, string Selector);
}
