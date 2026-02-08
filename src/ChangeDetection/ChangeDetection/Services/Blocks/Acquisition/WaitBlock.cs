using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Playwright;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Waits for a selector, a fixed time, or network idle on the current browser page.
/// </summary>
public class WaitBlock : IPipelineBlock
{
    public string BlockType => "Wait";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "page", Type = PortType.PageReference }];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
        [new PortDescriptor { Name = "page", Type = PortType.PageReference }];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

    public async Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (context.Page is not IPage page)
            return BlockResult.Failed("No browser page available.");

        var config = GetBlockConfig(context);

        if (config is { ValueKind: JsonValueKind.Object } cfg)
        {
            if (cfg.TryGetProperty("forSelector", out var sel) &&
                sel.ValueKind == JsonValueKind.String &&
                sel.GetString() is { Length: > 0 } selector)
            {
                context.Logger.LogDebug("WaitBlock waiting for selector: {Selector}", selector);
                await page.WaitForSelectorAsync(selector);
            }

            if (cfg.TryGetProperty("forTime", out var time) && time.TryGetInt32(out var delayMs) && delayMs > 0)
            {
                context.Logger.LogDebug("WaitBlock waiting {DelayMs}ms", delayMs);
                await Task.Delay(delayMs, context.CancellationToken);
            }

            if (cfg.TryGetProperty("forNetworkIdle", out var idle) && idle.ValueKind == JsonValueKind.True)
            {
                context.Logger.LogDebug("WaitBlock waiting for network idle");
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            }
        }

        var output = JsonSerializer.SerializeToElement(new { page = "reference" });
        return BlockResult.Succeeded(output);
    }

    private static JsonElement? GetBlockConfig(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return null;

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        return blockDef?.Config;
    }
}
