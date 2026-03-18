using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.AutoHealing;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.AutoHealing;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.AutoHealing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks;

[Category("Unit")]
public class PipelineExecutorTests : TestBase
{
    #region Test Block Implementations

    private class PassthroughBlock(string blockType, BlockCriticalityTier tier) : IPipelineBlock
    {
        public string BlockType => blockType;
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public BlockCriticalityTier CriticalityTier => tier;
        public int ExecutionCount { get; private set; }

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            ExecutionCount++;
            var output = context.Inputs.TryGetValue("data", out var input)
                ? input
                : JsonSerializer.SerializeToElement(new { passthrough = true });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class InputTestBlock : IPipelineBlock
    {
        public string BlockType => "Input";
        public IReadOnlyList<PortDescriptor> InputPorts => [];
        public IReadOnlyList<PortDescriptor> OutputPorts =>
        [
            new PortDescriptor { Name = "url", Type = PortType.Url },
            new PortDescriptor { Name = "config", Type = PortType.Configuration }
        ];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Infrastructure;
        public int ExecutionCount { get; private set; }

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            ExecutionCount++;
            var output = JsonSerializer.SerializeToElement(new { url = "https://example.com" });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class OutputTestBlock : IPipelineBlock
    {
        public string BlockType => "Output";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Delivery;
        public int ExecutionCount { get; private set; }

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            ExecutionCount++;
            var output = context.Inputs.TryGetValue("data", out var input)
                ? input
                : JsonSerializer.SerializeToElement(new { result = "output" });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class FailingBlock(string blockType, BlockCriticalityTier tier) : IPipelineBlock
    {
        public string BlockType => blockType;
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public BlockCriticalityTier CriticalityTier => tier;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            return Task.FromResult(BlockResult.Failed("test error"));
        }
    }

    private class CounterBlock(string blockType, BlockCriticalityTier tier) : IPipelineBlock
    {
        public string BlockType => blockType;
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public BlockCriticalityTier CriticalityTier => tier;
        public int ExecutionCount { get; private set; }

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            ExecutionCount++;
            return Task.FromResult(BlockResult.Failed($"attempt {ExecutionCount}"));
        }
    }

    private class ConditionFalseBlock : IPipelineBlock
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
            var output = JsonSerializer.SerializeToElement(false);
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

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
        public int ExecutionCount { get; private set; }

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            ExecutionCount++;
            var output = JsonSerializer.SerializeToElement(new { html = "<html>test</html>" });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class FilterTestBlock : IPipelineBlock
    {
        public string BlockType => "Filter";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
        public int ExecutionCount { get; private set; }

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            ExecutionCount++;
            var output = JsonSerializer.SerializeToElement(new { html = "<div>filtered</div>" });
            return Task.FromResult(BlockResult.Succeeded(output));
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
            var output = JsonSerializer.SerializeToElement(new { price = 29.99 });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private class HashCompareTestBlock : IPipelineBlock
    {
        public string BlockType => "HashCompare";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "result", Type = PortType.DiffResult }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Analysis;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var output = JsonSerializer.SerializeToElement(new { changed = true });
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
        public int ExecutionCount { get; private set; }

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            ExecutionCount++;
            var output = JsonSerializer.SerializeToElement(new { sent = true });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    #endregion

    #region Helpers

    private PipelineExecutor CreateExecutor(BlockRegistry registry, IServiceProvider? serviceProvider = null)
    {
        var validatorLogger = CreateLogger<PipelineValidator>();
        var validator = new PipelineValidator(validatorLogger);
        serviceProvider ??= BuildServiceProvider();
        var executorLogger = CreateLogger<PipelineExecutor>();
        return new PipelineExecutor(registry, validator, serviceProvider, executorLogger);
    }

    private PipelineExecutor CreateExecutor(
        BlockRegistry registry,
        params (Type ServiceType, object Instance)[] registrations)
    {
        var validatorLogger = CreateLogger<PipelineValidator>();
        var validator = new PipelineValidator(validatorLogger);
        var serviceProvider = BuildServiceProvider(registrations);
        var executorLogger = CreateLogger<PipelineExecutor>();
        return new PipelineExecutor(registry, validator, serviceProvider, executorLogger);
    }

    private static IServiceProvider BuildServiceProvider(
        params (Type ServiceType, object Instance)[] registrations)
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ILoggerFactory)).Returns(NullLoggerFactory.Instance);
        foreach (var (serviceType, instance) in registrations)
            sp.GetService(serviceType).Returns(instance);
        return sp;
    }

    private static IBlockStateStore CreateEmptyStateStore()
    {
        var store = Substitute.For<IBlockStateStore>();
        store.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);
        return store;
    }

    private static IBlockStateStore CreateStateStoreWithData()
    {
        var store = Substitute.For<IBlockStateStore>();
        var previousOutput = JsonSerializer.SerializeToElement(new { price = 19.99 });
        store.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(previousOutput);
        return store;
    }

    /// <summary>
    /// Builds a simple valid pipeline: Input→Navigate→Filter→ExtractSchema→HashCompare→Output
    /// and registers the corresponding test blocks.
    /// </summary>
    private static (BlockRegistry Registry, PipelineDefinition Pipeline) BuildSimpleValidPipeline()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        // Override with test block factories
        registry.Register("Input",
            inputPorts: [],
            outputPorts:
            [
                new PortDescriptor { Name = "url", Type = PortType.Url },
                new PortDescriptor { Name = "config", Type = PortType.Configuration }
            ],
            factory: _ => new InputTestBlock());

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

        registry.Register("HashCompare",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new HashCompareTestBlock());

        registry.Register("Output",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [],
            factory: _ => new OutputTestBlock());

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "filter-1", Type = "Filter" },
                new BlockDefinition { Id = "extract-1", Type = "ExtractSchema" },
                new BlockDefinition { Id = "hash-1", Type = "HashCompare" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "filter-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "filter-1", FromPort = "html", ToBlockId = "extract-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "hash-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        return (registry, pipeline);
    }

    #endregion

    [Test]
    public async Task ExecuteAsync_ValidPipeline_ExecutesAllBlocks()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var executor = CreateExecutor(registry);
        var stateStore = CreateStateStoreWithData();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeTrue();
        result.Error.ShouldBeNull();
        result.BlockResults.Count.ShouldBe(6);
        result.BlockResults.Values.ShouldAllBe(r => r.Success);
        result.OutputData.ShouldNotBeNull();
    }

    [Test]
    public async Task ExecuteAsync_InfrastructureFailure_AbortsRun()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();

        // Replace Navigate with a failing block
        registry.Register("Navigate",
            inputPorts: [new PortDescriptor { Name = "url", Type = PortType.Url }],
            outputPorts:
            [
                new PortDescriptor { Name = "page", Type = PortType.PageReference },
                new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
            ],
            factory: _ => new FailingBlock("Navigate", BlockCriticalityTier.Infrastructure));

        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("navigate-1");
        // Downstream blocks should not have been executed
        result.BlockResults.ShouldNotContainKey("filter-1");
        result.BlockResults.ShouldNotContainKey("extract-1");
    }

    [Test]
    public async Task ExecuteAsync_ExtractionFailure_Retries()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();

        var counterBlock = new CounterBlock("Filter", BlockCriticalityTier.Extraction);
        registry.Register("Filter",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            factory: _ => counterBlock);

        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeFalse();
        // Should have been called 3 times (1 initial + 2 retries)
        counterBlock.ExecutionCount.ShouldBe(3);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("filter-1");
    }

    [Test]
    public async Task ExecuteAsync_AnalysisFailure_SkipsWithDegraded()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();

        // Replace HashCompare (Analysis tier) with a failing block
        registry.Register("HashCompare",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new FailingBlock("HashCompare", BlockCriticalityTier.Analysis));

        var executor = CreateExecutor(registry);
        var stateStore = CreateStateStoreWithData();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeTrue();
        result.IsDegraded.ShouldBeTrue();
        result.SkippedBlockIds.ShouldContain("hash-1");
        // Output block should still execute (it gets data from extract, not hash)
        result.BlockResults.ShouldContainKey("output-1");
        result.BlockResults["output-1"].Success.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_FirstRun_MarksAsBaseline()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.WasBaseline.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_SubsequentRun_NotBaseline()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var executor = CreateExecutor(registry);
        var stateStore = CreateStateStoreWithData();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.WasBaseline.ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_InvalidPipeline_ReturnsError()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);
        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        // Pipeline with no Input or Output blocks
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = [new BlockDefinition { Id = "nav-1", Type = "Navigate" }],
            Connections = []
        };

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.BlockResults.ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_CancellationRequested_StopsExecution()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null, ct: cts.Token);

        result.Success.ShouldBeFalse();
        // Not all blocks should have been executed
        result.BlockResults.Count.ShouldBeLessThan(6);
    }

    [Test]
    public async Task ExecuteAsync_ConditionFalse_SkipsDownstream()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        // Register test block factories
        registry.Register("Input",
            inputPorts: [],
            outputPorts:
            [
                new PortDescriptor { Name = "url", Type = PortType.Url },
                new PortDescriptor { Name = "config", Type = PortType.Configuration }
            ],
            factory: _ => new InputTestBlock());

        registry.Register("Navigate",
            inputPorts: [new PortDescriptor { Name = "url", Type = PortType.Url }],
            outputPorts:
            [
                new PortDescriptor { Name = "page", Type = PortType.PageReference },
                new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
            ],
            factory: _ => new NavigateTestBlock());

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new ExtractTestBlock());

        registry.Register("HashCompare",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new HashCompareTestBlock());

        registry.Register("Condition",
            inputPorts:
            [
                new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false },
                new PortDescriptor { Name = "result", Type = PortType.DiffResult, Required = false }
            ],
            outputPorts: [new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal }],
            factory: _ => new ConditionFalseBlock());

        var notifyBlock = new NotifyTestBlock();
        registry.Register("Notify",
            inputPorts:
            [
                new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal },
                new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false }
            ],
            outputPorts: [new PortDescriptor { Name = "notification", Type = PortType.Notification }],
            factory: _ => notifyBlock);

        registry.Register("Output",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [],
            factory: _ => new OutputTestBlock());

        // Pipeline: Input→Navigate→ExtractSchema→HashCompare→Condition→Notify, ExtractSchema→Output
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "extract-1", Type = "ExtractSchema" },
                new BlockDefinition { Id = "hash-1", Type = "HashCompare" },
                new BlockDefinition { Id = "condition-1", Type = "Condition" },
                new BlockDefinition { Id = "notify-1", Type = "Notify" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "extract-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "hash-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "hash-1", FromPort = "result", ToBlockId = "condition-1", ToPort = "result" },
                new ConnectionDefinition { FromBlockId = "condition-1", FromPort = "signal", ToBlockId = "notify-1", ToPort = "signal" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var executor = CreateExecutor(registry);
        var stateStore = CreateStateStoreWithData();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeTrue();
        // Notify should have been skipped because Condition returned false
        result.SkippedBlockIds.ShouldContain("notify-1");
        notifyBlock.ExecutionCount.ShouldBe(0);
        // Output block is NOT downstream of Condition, so it should still execute
        result.BlockResults.ShouldContainKey("output-1");
        result.BlockResults["output-1"].Success.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_SavesOutputToStateStore()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();
        var watchId = Guid.NewGuid();

        await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        // Verify SaveOutputAsync was called for blocks that produced output
        await stateStore.Received().SaveOutputAsync(
            watchId.ToString(),
            "input-1",
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ExtractionDegradesAfterPreviouslyNonEmptyOutput_SkipsPersistenceAndDownstream()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var degradedOutput = JsonSerializer.SerializeToElement(new
        {
            price = (string?)null,
            title = (string?)null,
            price_source = (string?)null,
            title_source = (string?)null,
            _meta_extractedCount = "0",
            _meta_totalFields = "2"
        });

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ =>
            {
                var block = Substitute.For<IPipelineBlock>();
                block.BlockType.Returns("ExtractSchema");
                block.CriticalityTier.Returns(BlockCriticalityTier.Extraction);
                block.ExecuteAsync(Arg.Any<BlockContext>())
                    .Returns(BlockResult.Succeeded(degradedOutput));
                return block;
            });

        var stateStore = Substitute.For<IBlockStateStore>();
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            price = "$19.99",
            title = "Widget",
            category = "Tools"
        });
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), "extract-1", Arg.Any<CancellationToken>())
            .Returns(previousOutput);
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Is<string>(id => id != "extract-1"), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);

        var watchId = Guid.NewGuid();
        var result = await CreateExecutor(registry).ExecuteAsync(pipeline, watchId, stateStore, page: null);

        result.Success.ShouldBeTrue();
        result.IsDegraded.ShouldBeTrue();
        result.BlockResults["extract-1"].Status.ShouldBe(BlockExecutionStatus.ExtractionDegraded);
        result.BlockResults["extract-1"].SkipReason.ShouldContain("previously 3");
        result.SkippedBlockIds.ShouldContain("hash-1");
        result.SkippedBlockIds.ShouldContain("output-1");
        result.BlockResults["hash-1"].Status.ShouldBe(BlockExecutionStatus.Skipped);
        result.BlockResults["output-1"].Status.ShouldBe(BlockExecutionStatus.Skipped);
        await stateStore.DidNotReceive().SaveOutputAsync(
            watchId.ToString(),
            "extract-1",
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ExtractionWithSomeFields_DoesNotDegrade()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var partialOutput = JsonSerializer.SerializeToElement(new
        {
            price = "$29.99",
            title = (string?)null,
            category = "Tools",
            price_source = "css",
            title_source = (string?)null,
            category_source = "css",
            _meta_extractedCount = "2",
            _meta_totalFields = "3"
        });

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ =>
            {
                var block = Substitute.For<IPipelineBlock>();
                block.BlockType.Returns("ExtractSchema");
                block.CriticalityTier.Returns(BlockCriticalityTier.Extraction);
                block.ExecuteAsync(Arg.Any<BlockContext>())
                    .Returns(BlockResult.Succeeded(partialOutput));
                return block;
            });

        var stateStore = Substitute.For<IBlockStateStore>();
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            price = "$19.99",
            title = "Widget",
            category = "Old"
        });
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), "extract-1", Arg.Any<CancellationToken>())
            .Returns(previousOutput);
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Is<string>(id => id != "extract-1"), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);

        var result = await CreateExecutor(registry).ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.IsDegraded.ShouldBeFalse();
        result.BlockResults["extract-1"].Status.ShouldBe(BlockExecutionStatus.Completed);
        result.BlockResults["output-1"].Success.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_FirstRunEmptyExtraction_DoesNotDegrade()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var emptyOutput = JsonSerializer.SerializeToElement(new
        {
            price = (string?)null,
            title = (string?)null,
            price_source = (string?)null,
            title_source = (string?)null,
            _meta_extractedCount = "0",
            _meta_totalFields = "2"
        });

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ =>
            {
                var block = Substitute.For<IPipelineBlock>();
                block.BlockType.Returns("ExtractSchema");
                block.CriticalityTier.Returns(BlockCriticalityTier.Extraction);
                block.ExecuteAsync(Arg.Any<BlockContext>())
                    .Returns(BlockResult.Succeeded(emptyOutput));
                return block;
            });

        var stateStore = CreateEmptyStateStore();
        var result = await CreateExecutor(registry).ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.WasBaseline.ShouldBeTrue();
        result.IsDegraded.ShouldBeFalse();
        result.BlockResults["extract-1"].Status.ShouldBe(BlockExecutionStatus.Completed);
        result.BlockResults["output-1"].Success.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_AllowEmptyExtraction_DoesNotDegrade()
    {
        var (registry, originalPipeline) = BuildSimpleValidPipeline();
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = originalPipeline.SchemaVersion,
            Connections = [.. originalPipeline.Connections],
            Blocks =
            [
                .. originalPipeline.Blocks.Select(block => block.Id == "extract-1"
                    ? new BlockDefinition
                    {
                        Id = block.Id,
                        Type = block.Type,
                        Config = JsonSerializer.SerializeToElement(new
                        {
                            allowEmpty = true
                        })
                    }
                    : new BlockDefinition
                    {
                        Id = block.Id,
                        Type = block.Type,
                        Config = block.Config
                    })
            ]
        };

        var emptyOutput = JsonSerializer.SerializeToElement(new
        {
            price = (string?)null,
            title = (string?)null,
            price_source = (string?)null,
            title_source = (string?)null,
            _meta_extractedCount = "0",
            _meta_totalFields = "2"
        });

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ =>
            {
                var block = Substitute.For<IPipelineBlock>();
                block.BlockType.Returns("ExtractSchema");
                block.CriticalityTier.Returns(BlockCriticalityTier.Extraction);
                block.ExecuteAsync(Arg.Any<BlockContext>())
                    .Returns(BlockResult.Succeeded(emptyOutput));
                return block;
            });

        var stateStore = Substitute.For<IBlockStateStore>();
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            price = "$19.99",
            title = "Widget",
            category = "Tools"
        });
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), "extract-1", Arg.Any<CancellationToken>())
            .Returns(previousOutput);
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Is<string>(id => id != "extract-1"), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);

        var result = await CreateExecutor(registry).ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.IsDegraded.ShouldBeFalse();
        result.BlockResults["extract-1"].Status.ShouldBe(BlockExecutionStatus.Completed);
        result.BlockResults["output-1"].Success.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_ExtractionDegradation_TriggersAutoHealing()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        var degradedOutput = JsonSerializer.SerializeToElement(new
        {
            price = (string?)null,
            title = (string?)null,
            price_source = (string?)null,
            title_source = (string?)null,
            _meta_extractedCount = "0",
            _meta_totalFields = "2"
        });

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ =>
            {
                var block = Substitute.For<IPipelineBlock>();
                block.BlockType.Returns("ExtractSchema");
                block.CriticalityTier.Returns(BlockCriticalityTier.Extraction);
                block.ExecuteAsync(Arg.Any<BlockContext>())
                    .Returns(BlockResult.Succeeded(degradedOutput));
                return block;
            });

        var stateStore = Substitute.For<IBlockStateStore>();
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            price = "$19.99",
            title = "Widget",
            category = "Tools"
        });
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), "extract-1", Arg.Any<CancellationToken>())
            .Returns(previousOutput);
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Is<string>(id => id != "extract-1"), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);

        var failureTracker = Substitute.For<IFailureTracker>();
        failureTracker.RecordFailureAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(3);

        var autoHealing = Substitute.For<IAutoHealingService>();
        autoHealing.AttemptHealAsync(Arg.Any<HealingContext>(), Arg.Any<CancellationToken>())
            .Returns(new HealingResult { Outcome = HealingOutcome.NoActionNeeded, Message = "queued" });

        var executor = CreateExecutor(registry,
            (typeof(IFailureTracker), failureTracker),
            (typeof(IAutoHealingService), autoHealing));

        var watchId = Guid.NewGuid();
        var result = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        result.IsDegraded.ShouldBeTrue();
        await failureTracker.Received(1).RecordFailureAsync(
            watchId,
            "extract-1",
            Arg.Is<string>(msg => msg.Contains("previously 3")),
            Arg.Any<CancellationToken>());
        await autoHealing.Received(1).AttemptHealAsync(
            Arg.Is<HealingContext>(ctx =>
                ctx.WatchId == watchId &&
                ctx.BlockInstanceId == "extract-1" &&
                ctx.BlockType == "ExtractSchema" &&
                ctx.ErrorMessage.Contains("previously 3")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_PortResolution_PassesUpstreamOutputToDownstreamInput()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        JsonElement? capturedInput = null;

        // Custom extract block that captures its input
        var extractBlock = Substitute.For<IPipelineBlock>();
        extractBlock.BlockType.Returns("ExtractSchema");
        extractBlock.CriticalityTier.Returns(BlockCriticalityTier.Extraction);
        extractBlock.ExecuteAsync(Arg.Any<BlockContext>())
            .Returns(callInfo =>
            {
                var ctx = callInfo.Arg<BlockContext>();
                if (ctx.Inputs.TryGetValue("html", out var html))
                    capturedInput = html;
                var output = JsonSerializer.SerializeToElement(new { extracted = true });
                return BlockResult.Succeeded(output);
            });

        registry.Register("Input",
            inputPorts: [],
            outputPorts:
            [
                new PortDescriptor { Name = "url", Type = PortType.Url },
                new PortDescriptor { Name = "config", Type = PortType.Configuration }
            ],
            factory: _ => new InputTestBlock());

        registry.Register("Navigate",
            inputPorts: [new PortDescriptor { Name = "url", Type = PortType.Url }],
            outputPorts:
            [
                new PortDescriptor { Name = "page", Type = PortType.PageReference },
                new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
            ],
            factory: _ => new NavigateTestBlock());

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => extractBlock);

        registry.Register("Output",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [],
            factory: _ => new OutputTestBlock());

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "extract-1", Type = "ExtractSchema" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "extract-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var executor = CreateExecutor(registry);
        var stateStore = CreateEmptyStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeTrue();
        // The extract block should have received the navigate block's output as its "html" input
        capturedInput.ShouldNotBeNull();
    }

    [Test]
    public async Task ExecuteAsync_DeliveryFailure_ContinuesPipeline()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        registry.Register("Input",
            inputPorts: [],
            outputPorts:
            [
                new PortDescriptor { Name = "url", Type = PortType.Url },
                new PortDescriptor { Name = "config", Type = PortType.Configuration }
            ],
            factory: _ => new InputTestBlock());

        registry.Register("Navigate",
            inputPorts: [new PortDescriptor { Name = "url", Type = PortType.Url }],
            outputPorts:
            [
                new PortDescriptor { Name = "page", Type = PortType.PageReference },
                new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
            ],
            factory: _ => new NavigateTestBlock());

        registry.Register("ExtractSchema",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new ExtractTestBlock());

        registry.Register("HashCompare",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            factory: _ => new HashCompareTestBlock());

        registry.Register("Condition",
            inputPorts:
            [
                new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false },
                new PortDescriptor { Name = "result", Type = PortType.DiffResult, Required = false }
            ],
            outputPorts: [new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal }],
            factory: _ =>
            {
                var block = Substitute.For<IPipelineBlock>();
                block.BlockType.Returns("Condition");
                block.CriticalityTier.Returns(BlockCriticalityTier.Analysis);
                block.ExecuteAsync(Arg.Any<BlockContext>())
                    .Returns(BlockResult.Succeeded(JsonSerializer.SerializeToElement(true)));
                return block;
            });

        // Failing Notify block (Delivery tier)
        registry.Register("Notify",
            inputPorts:
            [
                new PortDescriptor { Name = "signal", Type = PortType.BooleanSignal },
                new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects, Required = false }
            ],
            outputPorts: [new PortDescriptor { Name = "notification", Type = PortType.Notification }],
            factory: _ => new FailingBlock("Notify", BlockCriticalityTier.Delivery));

        registry.Register("Output",
            inputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            outputPorts: [],
            factory: _ => new OutputTestBlock());

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "input-1", Type = "Input" },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "extract-1", Type = "ExtractSchema" },
                new BlockDefinition { Id = "hash-1", Type = "HashCompare" },
                new BlockDefinition { Id = "condition-1", Type = "Condition" },
                new BlockDefinition { Id = "notify-1", Type = "Notify" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "extract-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "hash-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "hash-1", FromPort = "result", ToBlockId = "condition-1", ToPort = "result" },
                new ConnectionDefinition { FromBlockId = "condition-1", FromPort = "signal", ToBlockId = "notify-1", ToPort = "signal" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "output-1", ToPort = "data" }
            ]
        };

        var executor = CreateExecutor(registry);
        var stateStore = CreateStateStoreWithData();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        // Pipeline succeeds despite delivery failure (outbox handles retry)
        result.Success.ShouldBeTrue();
        result.SkippedBlockIds.ShouldContain("notify-1");
        // Output block should still execute
        result.BlockResults.ShouldContainKey("output-1");
        result.BlockResults["output-1"].Success.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_AutoHealingContext_UsesCurrentAndLatestNavigateHtml()
    {
        var (registry, pipeline) = BuildSimpleValidPipeline();
        registry.Register("Filter",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            factory: _ => new FailingBlock("Filter", BlockCriticalityTier.Extraction));

        var failureTracker = Substitute.For<IFailureTracker>();
        failureTracker.RecordFailureAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(3);

        HealingContext? capturedContext = null;
        var healingService = Substitute.For<IAutoHealingService>();
        healingService.AttemptHealAsync(Arg.Any<HealingContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedContext = callInfo.Arg<HealingContext>();
                return new HealingResult
                {
                    Outcome = HealingOutcome.NoActionNeeded,
                    Message = "captured"
                };
            });

        var watchId = Guid.NewGuid();
        var watchRepo = Substitute.For<IRepository<WatchedSite>>();
        watchRepo.GetByIdAsync(watchId, Arg.Any<CancellationToken>())
            .Returns(new WatchedSite
            {
                Id = watchId,
                Url = "https://example.com",
                SetupTimeHtml = "<html>setup</html>",
                LatestSuccessfulHtml = "<html>legacy-should-not-be-used</html>"
            });

        var serviceProvider = BuildServiceProvider(failureTracker, healingService, watchRepo);
        var executor = CreateExecutor(registry, serviceProvider);

        var previousNavigateOutput = JsonSerializer.SerializeToElement(new { html = "<html>previous</html>" });
        var stateStore = Substitute.For<IBlockStateStore>();
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var blockId = callInfo.ArgAt<string>(1);
                return blockId == "navigate-1" ? previousNavigateOutput : (JsonElement?)null;
            });

        var result = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        result.Success.ShouldBeFalse();
        capturedContext.ShouldNotBeNull();
        capturedContext!.CurrentHtml.ShouldBe("<html>test</html>");
        capturedContext.LatestSuccessfulHtml.ShouldBe("<html>previous</html>");
        capturedContext.SetupTimeHtml.ShouldBe("<html>setup</html>");
        await stateStore.Received().GetPreviousOutputAsync(watchId.ToString(), "navigate-1", Arg.Any<CancellationToken>());
    }
}
