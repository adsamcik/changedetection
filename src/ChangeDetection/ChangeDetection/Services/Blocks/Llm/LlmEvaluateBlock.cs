using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Llm;

/// <summary>
/// Evaluates extracted data using an LLM with a user-defined prompt and output schema.
/// Returns structured evaluation results as DiffResult-compatible output.
/// </summary>
public class LlmEvaluateBlock : IPipelineBlock
{
    public string BlockType => "LlmEvaluate";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "result", Type = PortType.DiffResult }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return BlockResult.Failed("LlmEvaluate block requires a 'data' input.");

        var (prompt, outputSchema) = ReadConfig(context);

        if (string.IsNullOrWhiteSpace(prompt))
            return BlockResult.Failed("LlmEvaluate block requires a 'prompt' in config.");

        var serializedData = dataElement.GetRawText();
        var fullPrompt = BuildPrompt(prompt, serializedData, outputSchema);

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
                "LlmEvaluate tokens — input: {Input}, output: {Output}",
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
            return BlockResult.Failed($"LlmEvaluate failed: {ex.Message}");
        }
    }

    private static string BuildPrompt(string userPrompt, string data, string? outputSchema)
    {
        var sanitizedData = PromptSanitizer.Sanitize(data, "data");
        var prompt = $"""
            {userPrompt}

            Data to evaluate:
            {sanitizedData}
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
