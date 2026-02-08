using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Llm;

/// <summary>
/// Extracts structured data from HTML using an LLM with a user-defined prompt and output schema.
/// </summary>
public class LlmExtractBlock : IPipelineBlock
{
    public string BlockType => "LlmExtract";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("html", out var htmlElement))
            return BlockResult.Failed("LlmExtract block requires an 'html' input.");

        var html = htmlElement.ValueKind == JsonValueKind.String
            ? htmlElement.GetString()
            : htmlElement.TryGetProperty("html", out var nested) ? nested.GetString() : null;

        if (string.IsNullOrWhiteSpace(html))
            return BlockResult.Failed("LlmExtract block received empty or invalid HTML.");

        var (prompt, outputSchema) = ReadConfig(context);

        if (string.IsNullOrWhiteSpace(prompt))
            return BlockResult.Failed("LlmExtract block requires a 'prompt' in config.");

        var fullPrompt = BuildPrompt(prompt, html, outputSchema);

        var llmChain = context.Services.GetRequiredService<ILlmProviderChain>();
        var options = new LlmRequestOptions
        {
            ExpectJson = true,
            WatchedSiteId = context.WatchId
        };

        try
        {
            var response = await llmChain.ExecuteAsync(fullPrompt, options, context.CancellationToken);

            if (!response.IsSuccess)
                return BlockResult.Failed($"LLM call failed: {response.ErrorMessage}");

            context.Logger.LogDebug(
                "LlmExtract tokens — input: {Input}, output: {Output}",
                response.InputTokens, response.OutputTokens);

            if (string.IsNullOrWhiteSpace(response.Content))
                return BlockResult.Failed("LLM returned empty content.");

            try
            {
                using var doc = JsonDocument.Parse(response.Content);
                var output = doc.RootElement.Clone();
                return BlockResult.Succeeded(output);
            }
            catch (JsonException ex)
            {
                return BlockResult.Failed($"LLM response is not valid JSON: {ex.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BlockResult.Failed($"LlmExtract failed: {ex.Message}");
        }
    }

    private static string BuildPrompt(string userPrompt, string html, string? outputSchema)
    {
        var sanitizedHtml = PromptSanitizer.Sanitize(html, "page_content");
        var prompt = $"""
            {userPrompt}

            Content to analyze:
            {sanitizedHtml}
            """;

        if (!string.IsNullOrWhiteSpace(outputSchema))
            prompt += $"\n\nRespond with a JSON object matching this schema: {outputSchema}";

        return prompt;
    }

    private static (string? prompt, string? outputSchema) ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return (null, null);

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return (null, null);

        string? prompt = null;
        if (config.TryGetProperty("prompt", out var promptElem) && promptElem.ValueKind == JsonValueKind.String)
            prompt = promptElem.GetString();

        string? outputSchema = null;
        if (config.TryGetProperty("outputSchema", out var schemaElem))
            outputSchema = schemaElem.GetRawText();

        return (prompt, outputSchema);
    }
}
