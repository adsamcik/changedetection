using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.BlockExecution;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

[Category("Unit")]
public class BlockRegistryTests
{
    [Test]
    public async Task RegisterCoreBlocks_RelevanceScore_ExposesResultAndItemsPorts()
    {
        var registry = new BlockRegistry();

        BlockRegistry.RegisterCoreBlocks(registry);

        var outputPorts = registry.GetOutputPorts("RelevanceScore");
        outputPorts.ShouldContain(p => p.Name == "result" && p.Type == PortType.DiffResult);
        outputPorts.ShouldContain(p => p.Name == "items" && p.Type == PortType.ExtractedObjects);
        await Task.CompletedTask;
    }
}
