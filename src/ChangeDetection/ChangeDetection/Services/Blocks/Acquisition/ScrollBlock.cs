using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Microsoft.Playwright;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Scrolls the browser page a configurable number of times with optional delay between scrolls.
/// </summary>
public class ScrollBlock : IPipelineBlock
{
    public string BlockType => "Scroll";

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

        var direction = "down";
        var times = 1;
        var delayMs = 500;

        if (config is { ValueKind: JsonValueKind.Object } cfg)
        {
            if (cfg.TryGetProperty("direction", out var dir) && dir.ValueKind == JsonValueKind.String)
                direction = dir.GetString() ?? "down";

            if (cfg.TryGetProperty("times", out var t) && t.TryGetInt32(out var n) && n > 0)
                times = n;
            times = Math.Clamp(times, 1, 100);

            if (cfg.TryGetProperty("delay", out var d) && d.TryGetInt32(out var ms) && ms >= 0)
                delayMs = ms;
        }

        var scrollScript = string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase)
            ? "window.scrollBy(0, -window.innerHeight)"
            : "window.scrollBy(0, window.innerHeight)";

        context.Logger.LogDebug("ScrollBlock scrolling {Direction} {Times} times with {Delay}ms delay",
            direction, times, delayMs);

        for (var i = 0; i < times; i++)
        {
            await page.EvaluateAsync(scrollScript);

            if (i < times - 1 && delayMs > 0)
                await Task.Delay(delayMs, context.CancellationToken);
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
