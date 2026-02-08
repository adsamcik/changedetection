using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Golden;

[Category("Integration")]
public class GoldenPipelineExecutionTests : TestBase
{
    #region Test Block Implementations

    private class NavigateTestBlock : IPipelineBlock
    {
        public string BlockType => "Navigate";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "url", Type = PortType.Url }];
        public IReadOnlyList<PortDescriptor> OutputPorts =>
        [
            new PortDescriptor { Name = "page", Type = PortType.PageReference },
            new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
        ];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var output = JsonSerializer.SerializeToElement(new
            {
                html = "<html><body><div id=\"content\">Hello World</div></body></html>",
                url = "https://example.com"
            });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class FilterTestBlock : IPipelineBlock
    {
        public string BlockType => "Filter";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var html = context.Inputs.TryGetValue("html", out var input) ? input : JsonSerializer.SerializeToElement("<div>filtered</div>");
            return Task.FromResult(BlockResult.Succeeded(html));
        }
    }

    private class ExtractTestBlock : IPipelineBlock
    {
        public string BlockType => "ExtractSchema";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var output = JsonSerializer.SerializeToElement(new { content = "Hello World" });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class ConditionTestBlock : IPipelineBlock
    {
        public string BlockType => "Condition";
        public IReadOnlyList<PortDescriptor> InputPorts =>
        [
            new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false },
            new PortDescriptor { Name = "result", Type = PortType.DiffResult, Required = false }
        ];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            // Check for changes in the result input
            var hasChanges = false;
            if (context.Inputs.TryGetValue("result", out var result) &&
                result.TryGetProperty("changed", out var changed))
            {
                hasChanges = changed.ValueKind == JsonValueKind.True;
            }

            var output = JsonSerializer.SerializeToElement(new { signal = hasChanges });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class NotifyTestBlock : IPipelineBlock
    {
        public string BlockType => "Notify";
        public IReadOnlyList<PortDescriptor> InputPorts =>
        [
            new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal },
            new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false }
        ];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "notification", Type = PortType.Notification }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Delivery;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var output = JsonSerializer.SerializeToElement(new { sent = true });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    #endregion

    #region Helpers

    private static async Task<string> LoadFixtureAsync(string filename)
    {
        var dir = Path.GetDirectoryName(typeof(GoldenPipelineExecutionTests).Assembly.Location)!;
        var path = Path.Combine(dir, "Pipeline", "Golden", filename);
        return await File.ReadAllTextAsync(path);
    }

    /// <summary>
    /// Creates a registry with core blocks, overriding service-dependent blocks with test implementations.
    /// </summary>
    private static BlockRegistry CreateRegistryWithTestBlocks()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        // Override blocks that need external services (IContentFetcher, IContentExtractor, etc.)
        registry.Register("Navigate",
            inputPorts: [new PortDescriptor { Name = "url", Type = PortType.Url }],
            outputPorts:
            [
                new PortDescriptor { Name = "page", Type = PortType.PageReference },
                new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
            ],
            factory: _ => new NavigateTestBlock());

        registry.Register("Filter",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            factory: _ => new FilterTestBlock());

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new ExtractTestBlock());

        registry.Register("Condition",
            inputPorts:
            [
                new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false },
                new PortDescriptor { Name = "result", Type = PortType.DiffResult, Required = false }
            ],
            outputPorts: [new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal }],
            factory: _ => new ConditionTestBlock());

        registry.Register("Notify",
            inputPorts:
            [
                new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal },
                new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false }
            ],
            outputPorts: [new PortDescriptor { Name = "notification", Type = PortType.Notification }],
            factory: _ => new NotifyTestBlock());

        return registry;
    }

    private PipelineExecutor CreateExecutor(BlockRegistry registry)
    {
        var validator = new PipelineValidator(CreateLogger<PipelineValidator>());
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ILoggerFactory)).Returns(NullLoggerFactory.Instance);
        var executorLogger = CreateLogger<PipelineExecutor>();
        return new PipelineExecutor(registry, validator, sp, executorLogger);
    }

    private static IBlockStateStore CreateEmptyStateStore()
    {
        var store = Substitute.For<IBlockStateStore>();
        store.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);
        return store;
    }

    #endregion

    [Test]
    public async Task SimpleHashCheck_ExecutesSuccessfully()
    {
        var json = await LoadFixtureAsync("simple-hash-check.json");
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        var registry = CreateRegistryWithTestBlocks();
        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        Log($"Success={result.Success}, WasBaseline={result.WasBaseline}, Error={result.Error}");
        Log($"Block results: {result.BlockResults.Count}, Skipped: {result.SkippedBlockIds.Count}");

        result.Success.ShouldBeTrue(result.Error ?? "Pipeline failed with no error message");
        result.WasBaseline.ShouldBeTrue("First run should be baseline");
        result.Error.ShouldBeNull();
    }

    [Test]
    public async Task SimpleHashCheck_AllBlocksExecute()
    {
        var json = await LoadFixtureAsync("simple-hash-check.json");
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        var registry = CreateRegistryWithTestBlocks();
        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.BlockResults.Count.ShouldBe(pipeline.Blocks.Count,
            $"Expected results for all {pipeline.Blocks.Count} blocks but got {result.BlockResults.Count}");

        foreach (var block in pipeline.Blocks)
        {
            result.BlockResults.ShouldContainKey(block.Id,
                $"Missing result for block '{block.Id}' ({block.Type})");
        }
    }

    [Test]
    public async Task SimpleHashCheck_NoBlockErrors()
    {
        var json = await LoadFixtureAsync("simple-hash-check.json");
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        var registry = CreateRegistryWithTestBlocks();
        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        foreach (var (blockId, blockResult) in result.BlockResults)
        {
            var blockType = pipeline.Blocks.First(b => b.Id == blockId).Type;
            Log($"Block {blockId} ({blockType}): Success={blockResult.Success}, Status={blockResult.Status}, Error={blockResult.Error}");

            if (blockResult.Status != BlockExecutionStatus.Skipped)
            {
                blockResult.Success.ShouldBeTrue(
                    $"Block '{blockId}' ({blockType}) failed: {blockResult.Error}");
            }
        }
    }

    [Test]
    public async Task SimpleHashCheck_SecondRun_DetectsNotBaseline()
    {
        var json = await LoadFixtureAsync("simple-hash-check.json");
        var pipeline = PipelineSerializer.Deserialize(json);
        pipeline.ShouldNotBeNull();

        var registry = CreateRegistryWithTestBlocks();
        var executor = CreateExecutor(registry);

        // State store that returns previous data (simulating a second run)
        var stateStore = Substitute.For<IBlockStateStore>();
        var previousOutput = JsonSerializer.SerializeToElement(new { hash = "abc123" });
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(previousOutput);

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeTrue(result.Error ?? "Pipeline failed");
        result.WasBaseline.ShouldBeFalse("Second run should not be baseline");
    }
}
