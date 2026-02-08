using System.Text.Json;
using ChangeDetection.Core.Pipeline;

namespace ChangeDetection.Services.Blocks.Acquisition;

/// <summary>
/// Entry-point block that reads URL and configuration from the pipeline definition.
/// </summary>
public class InputBlock : IPipelineBlock
{
    public string BlockType => "Input";

    public IReadOnlyList<PortDescriptor> InputPorts => [];

    public IReadOnlyList<PortDescriptor> OutputPorts =>
    [
        new PortDescriptor { Name = "url", Type = PortType.Url },
        new PortDescriptor { Name = "config", Type = PortType.Configuration }
    ];

    public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

    public Task<BlockResult> ExecuteAsync(BlockContext context)
    {
        if (context.PipelineDefinition is not PipelineDefinition pipeline)
            return Task.FromResult(BlockResult.Failed("No pipeline definition available."));

        var blockDef = pipeline.Blocks.FirstOrDefault(
            b => string.Equals(b.Id, context.BlockInstanceId, StringComparison.OrdinalIgnoreCase));

        if (blockDef?.Config is not { ValueKind: JsonValueKind.Object } config)
            return Task.FromResult(BlockResult.Failed("Input block has no configuration."));

        if (!config.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(urlElement.GetString()))
        {
            return Task.FromResult(BlockResult.Failed("Input block configuration is missing a 'url' property."));
        }

        var output = JsonSerializer.SerializeToElement(new
        {
            url = urlElement.GetString(),
            config
        });

        context.Logger.LogInformation("InputBlock: Providing URL {Url} to pipeline", urlElement.GetString());
        return Task.FromResult(BlockResult.Succeeded(output));
    }
}
