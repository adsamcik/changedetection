using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Playwright;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Clicks an element on the current browser page using a CSS selector.
/// </summary>
public class ClickBlock : IPipelineBlock
{
    public string BlockType => "Click";

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

        if (config is not { ValueKind: JsonValueKind.Object } cfg ||
            !cfg.TryGetProperty("selector", out var sel) ||
            sel.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(sel.GetString()))
        {
            return BlockResult.Failed("Click block requires a 'selector' in its configuration.");
        }

        var selector = sel.GetString()!;
        context.Logger.LogDebug("ClickBlock clicking selector: {Selector}", selector);
        await page.ClickAsync(selector);

        if (cfg.TryGetProperty("waitAfter", out var wait) && wait.TryGetInt32(out var waitMs) && waitMs > 0)
        {
            context.Logger.LogDebug("ClickBlock waiting {WaitMs}ms after click", waitMs);
            await Task.Delay(waitMs, context.CancellationToken);
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
