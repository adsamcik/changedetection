using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Comparison;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Comparison;

[Category("Unit")]
public class ListDiffBlockTests : TestBase
{
    private readonly ListDiffBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_FirstRun_ReturnsBaseline()
    {
        var items = JsonSerializer.SerializeToElement(new[]
        {
            new { url = "https://example.com/1", title = "Item 1" },
            new { url = "https://example.com/2", title = "Item 2" }
        });

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("list-1", "ListDiff", new { identityKey = "url", mode = "all_changes" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("list-1")
            .WithInput("data", items)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Baseline);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_NewItems_DetectsAdditions()
    {
        var previousItems = JsonSerializer.SerializeToElement(new[]
        {
            new { url = "https://example.com/1", title = "Item 1" }
        });
        var previousOutput = JsonSerializer.SerializeToElement(new { items = previousItems, changed = false });

        var currentItems = JsonSerializer.SerializeToElement(new[]
        {
            new { url = "https://example.com/1", title = "Item 1" },
            new { url = "https://example.com/2", title = "Item 2" }
        });

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("list-1", "ListDiff", new { identityKey = "url", mode = "all_changes" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("list-1")
            .WithInput("data", currentItems)
            .WithPreviousOutput(previousOutput)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();

        var added = result.Output!.Value.GetProperty("added");
        added.GetArrayLength().ShouldBe(1);
        added[0].GetProperty("url").GetString().ShouldBe("https://example.com/2");
    }

    [Test]
    public async Task ExecuteAsync_RemovedItems_DetectsRemovals()
    {
        var previousItems = JsonSerializer.SerializeToElement(new[]
        {
            new { url = "https://example.com/1", title = "Item 1" },
            new { url = "https://example.com/2", title = "Item 2" }
        });
        var previousOutput = JsonSerializer.SerializeToElement(new { items = previousItems, changed = false });

        var currentItems = JsonSerializer.SerializeToElement(new[]
        {
            new { url = "https://example.com/1", title = "Item 1" }
        });

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("list-1", "ListDiff", new { identityKey = "url", mode = "all_changes" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("list-1")
            .WithInput("data", currentItems)
            .WithPreviousOutput(previousOutput)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();

        var removed = result.Output!.Value.GetProperty("removed");
        removed.GetArrayLength().ShouldBe(1);
        removed[0].GetProperty("url").GetString().ShouldBe("https://example.com/2");
    }

    [Test]
    public async Task ExecuteAsync_AdditionsOnlyMode_OnlyReportsAdded()
    {
        var previousItems = JsonSerializer.SerializeToElement(new[]
        {
            new { url = "https://example.com/1", title = "Item 1" },
            new { url = "https://example.com/2", title = "Item 2" }
        });
        var previousOutput = JsonSerializer.SerializeToElement(new { items = previousItems, changed = false });

        var currentItems = JsonSerializer.SerializeToElement(new[]
        {
            new { url = "https://example.com/1", title = "Item 1" },
            new { url = "https://example.com/3", title = "Item 3" }
        });

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("list-1", "ListDiff", new { identityKey = "url", mode = "additions_only" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("list-1")
            .WithInput("data", currentItems)
            .WithPreviousOutput(previousOutput)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();

        var added = result.Output!.Value.GetProperty("added");
        added.GetArrayLength().ShouldBe(1);
        added[0].GetProperty("url").GetString().ShouldBe("https://example.com/3");

        // additions_only mode should not include removed/modified/unchanged
        result.Output!.Value.TryGetProperty("removed", out _).ShouldBeFalse();
        result.Output!.Value.TryGetProperty("modified", out _).ShouldBeFalse();
        result.Output!.Value.TryGetProperty("unchanged", out _).ShouldBeFalse();
    }

    [Test]
    public async Task BlockType_ReturnsListDiff()
    {
        _sut.BlockType.ShouldBe("ListDiff");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CriticalityTier_ReturnsAnalysis()
    {
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Analysis);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.Count.ShouldBe(1);
        _sut.InputPorts[0].Name.ShouldBe("data");
        _sut.InputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("result");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.DiffResult);
        await Task.CompletedTask;
    }

}
