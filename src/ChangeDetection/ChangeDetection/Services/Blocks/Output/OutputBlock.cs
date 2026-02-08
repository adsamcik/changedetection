using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Output;

/// <summary>
/// Terminal pipeline node that aggregates upstream data into the final display schema.
/// Currently passes through input data unchanged; display transforms come later.
/// </summary>
public class OutputBlock : IPipelineBlock
{
    public string BlockType => "Output";

    public IReadOnlyList<PortDescriptor> InputPorts =>
        [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];

    public IReadOnlyList<PortDescriptor> OutputPorts => [];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Delivery;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (context.Inputs.TryGetValue("data", out var dataElement))
        {
            var raw = dataElement.GetRawText();
            context.Logger.LogInformation("OutputBlock: Emitting result ({Length} chars)", raw.Length);
            return Task.FromResult(BlockResult.Succeeded(dataElement));
        }

        context.Logger.LogInformation("OutputBlock: No input data, emitting empty result");
        var empty = JsonSerializer.SerializeToElement(new { });
        return Task.FromResult(BlockResult.Succeeded(empty));
    }
}
