using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Decision;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Decision;

[Category("Unit")]
public class ConditionBlockTests : TestBase
{
    private readonly ConditionBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_LessThan_TrueWhenBelow()
    {
        var data = JsonSerializer.SerializeToElement(new { price = 400 });
        var pipeline = CreatePipeline("cond-1", new { field = "price", @operator = "lessThan", value = 500 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("cond-1")
            .WithInput("data", data)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("signal").GetBoolean().ShouldBeTrue();
        result.Output!.Value.GetProperty("field").GetString().ShouldBe("price");
    }

    [Test]
    public async Task ExecuteAsync_LessThan_FalseWhenAbove()
    {
        var data = JsonSerializer.SerializeToElement(new { price = 600 });
        var pipeline = CreatePipeline("cond-1", new { field = "price", @operator = "lessThan", value = 500 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("cond-1")
            .WithInput("data", data)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("signal").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_Equals_TrueWhenMatch()
    {
        var data = JsonSerializer.SerializeToElement(new { changed = true });
        var pipeline = CreatePipeline("cond-1", new { field = "changed", @operator = "equals", value = true });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("cond-1")
            .WithInput("result", data)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("signal").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_Contains_TrueWhenFound()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Big Summer Sale Event" });
        var pipeline = CreatePipeline("cond-1", new { field = "title", @operator = "contains", value = "sale" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("cond-1")
            .WithInput("data", data)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("signal").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_GreaterThan_ForArrayLength()
    {
        var data = JsonSerializer.SerializeToElement(new { added = new[] { "item1", "item2" } });
        var pipeline = CreatePipeline("cond-1", new { field = "added.length", @operator = "greaterThan", value = 0 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("cond-1")
            .WithInput("data", data)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("signal").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_NoInputs_SkipsBlock()
    {
        var pipeline = CreatePipeline("cond-1", new { field = "price", @operator = "lessThan", value = 500 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("cond-1")
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Skipped);
        result.SkipReason.ShouldBe("No data to evaluate");
    }

    [Test]
    public async Task BlockType_ReturnsCondition()
    {
        _sut.BlockType.ShouldBe("Condition");
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
        _sut.InputPorts.Count.ShouldBe(2);
        _sut.InputPorts[0].Name.ShouldBe("data");
        _sut.InputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.InputPorts[0].Required.ShouldBeFalse();
        _sut.InputPorts[1].Name.ShouldBe("result");
        _sut.InputPorts[1].Type.ShouldBe(PortType.DiffResult);
        _sut.InputPorts[1].Required.ShouldBeFalse();
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("signal");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.BooleanSignal);
        await Task.CompletedTask;
    }

    private static PipelineDefinition CreatePipeline(string blockId, object config) => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            new BlockDefinition
            {
                Id = blockId,
                Type = "Condition",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
