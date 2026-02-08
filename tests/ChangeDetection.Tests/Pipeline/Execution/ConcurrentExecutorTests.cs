using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Execution;

[Category("Integration")]
public class ConcurrentExecutorTests : TestBase
{
    private class SlowBlock : IPipelineBlock
    {
        public string BlockType => "Slow";
        public IReadOnlyList<PortDescriptor> InputPorts => [];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "output", Type = PortType.PlainText }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

        public async Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            await Task.Delay(5000, context.CancellationToken);
            return BlockResult.Succeeded(JsonSerializer.SerializeToElement("done"));
        }
    }

    private PipelineExecutor CreateExecutor(BlockRegistry registry)
    {
        var validator = new PipelineValidator(CreateLogger<PipelineValidator>());
        var sp = BuildServiceProvider();
        var executorLogger = CreateLogger<PipelineExecutor>();
        return new PipelineExecutor(registry, validator, sp, executorLogger);
    }

    private static IServiceProvider BuildServiceProvider()
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ILoggerFactory)).Returns(NullLoggerFactory.Instance);
        return sp;
    }

    private static IBlockStateStore CreateEmptyStateStore()
    {
        var store = Substitute.For<IBlockStateStore>();
        store.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);
        return store;
    }

    private static (BlockRegistry Registry, PipelineDefinition Pipeline) BuildMinimalPipeline()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        // Override Input to output ExtractedObjects-compatible data on a "data" port
        registry.Register("Input",
            inputPorts: [],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new InputBlock());

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition
                {
                    FromBlockId = "input-1", FromPort = "data",
                    ToBlockId = "output-1", ToPort = "data"
                }
            ]
        };

        return (registry, pipeline);
    }

    private static (BlockRegistry Registry, PipelineDefinition Pipeline) BuildSlowPipeline()
    {
        var registry = new BlockRegistry();

        // Register Input with a simple factory
        registry.Register("Input",
            inputPorts: [],
            outputPorts:
            [
                new PortDescriptor { Name = "url", Type = PortType.Url },
                new PortDescriptor { Name = "config", Type = PortType.Configuration }
            ],
            factory: _ => new InputBlock());

        // Register Slow block
        registry.Register("Slow",
            inputPorts: [],
            outputPorts: [new PortDescriptor { Name = "output", Type = PortType.PlainText }],
            factory: _ => new SlowBlock());

        // Register Output
        registry.Register("Output",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [],
            factory: _ => new OutputBlock());

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "slow-1", Type = "Slow" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition
                {
                    FromBlockId = "input-1", FromPort = "url",
                    ToBlockId = "slow-1", ToPort = "url"
                },
                new ConnectionDefinition
                {
                    FromBlockId = "slow-1", FromPort = "output",
                    ToBlockId = "output-1", ToPort = "data"
                }
            ]
        };

        return (registry, pipeline);
    }

    [Test]
    public async Task TwoExecutionsForSameWatch_DontCorruptState()
    {
        var (registry, pipeline) = BuildMinimalPipeline();
        var executor = CreateExecutor(registry);
        var watchId = Guid.NewGuid();
        var stateStore = CreateEmptyStateStore();

        var task1 = executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);
        var task2 = executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        var results = await Task.WhenAll(task1, task2);

        Log($"Result 1: Success={results[0].Success}, Error={results[0].Error}");
        Log($"Result 2: Success={results[1].Success}, Error={results[1].Error}");

        // Both should complete — either both succeed or one is guarded
        results.Length.ShouldBe(2);

        var successCount = results.Count(r => r.Success);
        successCount.ShouldBeGreaterThan(0, "At least one execution should succeed");

        // Verify no corruption: each result should have valid block results
        foreach (var result in results)
        {
            result.BlockResults.ShouldNotBeNull();
            if (result.Success)
            {
                result.BlockResults.Count.ShouldBeGreaterThan(0);
            }
        }
    }

    [Test]
    public async Task CancellationMidPipeline_StopsGracefully()
    {
        var (registry, pipeline) = BuildSlowPipeline();
        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        using var cts = new CancellationTokenSource();

        var executeTask = executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null, ct: cts.Token);

        // Give the pipeline time to start, then cancel
        await Task.Delay(200);
        await cts.CancelAsync();

        // Should complete without throwing (executor catches OperationCanceledException internally)
        PipelineExecutionResult? result = null;
        var threw = false;
        try
        {
            result = await executeTask;
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        Log($"Threw OperationCanceledException: {threw}");
        if (result is not null)
        {
            Log($"Result: Success={result.Success}, Error={result.Error}");
        }

        // Either the executor returns a failed result or throws cancellation — both are valid
        if (!threw)
        {
            result.ShouldNotBeNull();
            // If the cancellation was caught, the pipeline should report failure or partial completion
            (result.Success == false || result.Error is not null || result.BlockResults.Count < pipeline.Blocks.Count)
                .ShouldBeTrue("Cancelled pipeline should show signs of cancellation");
        }
    }

    // Reuse InputBlock and OutputBlock from the registered core blocks
    private class InputBlock : IPipelineBlock
    {
        public string BlockType => "Input";
        public IReadOnlyList<PortDescriptor> InputPorts => [];
        public IReadOnlyList<PortDescriptor> OutputPorts =>
            [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var output = JsonSerializer.SerializeToElement(new { url = "https://example.com" });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class OutputBlock : IPipelineBlock
    {
        public string BlockType => "Output";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Delivery;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var output = context.Inputs.TryGetValue("data", out var input)
                ? input
                : JsonSerializer.SerializeToElement(new { result = "output" });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }
}
