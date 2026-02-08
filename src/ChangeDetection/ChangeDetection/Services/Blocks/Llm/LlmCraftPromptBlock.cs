using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace ChangeDetection.Services.Blocks.Llm;

/// <summary>
/// Uses an LLM to generate a prompt for downstream LLM blocks based on instructions and context data.
/// Returns plain text (not JSON).
/// </summary>
public class LlmCraftPromptBlock : IPipelineBlock
{
    public string BlockType => "LlmCraftPrompt";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.PlainText }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (!context.Inputs.TryGetValue("data", out var dataElement))
            return BlockResult.Failed("LlmCraftPrompt block requires a 'data' input.");

        var instructions = ReadConfig(context);

        if (string.IsNullOrWhiteSpace(instructions))
            return BlockResult.Failed("LlmCraftPrompt block requires 'instructions' in config.");

        var serializedData = dataElement.GetRawText();
        var sanitizedData = PromptSanitizer.Sanitize(serializedData, "data");
        var fullPrompt = $"""
            {instructions}

            Context data:
            {sanitizedData}
            """;

        var llmChain = context.Services.GetRequiredService<ILlmProviderChain>();
        var options = new LlmRequestOptions
        {
            ExpectJson = false,
            WatchedSiteId = context.WatchId
        };

        try
        {
            var response = await llmChain.ExecuteAsync(fullPrompt, options, context.CancellationToken);

            if (!response.IsSuccess)
                return BlockResult.Failed($"LLM call failed: {response.ErrorMessage}");

            context.Logger.LogDebug(
                "LlmCraftPrompt tokens — input: {Input}, output: {Output}",
                response.InputTokens, response.OutputTokens);

            if (string.IsNullOrWhiteSpace(response.Content))
                return BlockResult.Failed("LLM returned empty content.");

            var output = JsonSerializer.SerializeToElement(response.Content);
            return BlockResult.Succeeded(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BlockResult.Failed($"LlmCraftPrompt failed: {ex.Message}");
        }
    }

    private static string? ReadConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return null;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return null;

        if (config.TryGetProperty("instructions", out var instructionsElem) &&
            instructionsElem.ValueKind == JsonValueKind.String)
            return instructionsElem.GetString();

        return null;
    }
}
