using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Comparison;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Comparison;

[Category("Unit")]
public class NumericDeltaBlockTests : TestBase
{
    private readonly NumericDeltaBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_FirstRun_ReturnsBaseline()
    {
        var data = JsonSerializer.SerializeToElement(new { price = 100m });

        var pipeline = CreatePipeline("numeric-1", new { field = "price" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("numeric-1")
            .WithInput("data", data)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Baseline);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("value").GetDecimal().ShouldBe(100m);
        result.Output!.Value.GetProperty("field").GetString().ShouldBe("price");
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_PriceDropped_CalculatesDelta()
    {
        var currentData = JsonSerializer.SerializeToElement(new { price = 90m });
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            value = 100m,
            field = "price",
            changed = false
        });

        var pipeline = CreatePipeline("numeric-1", new { field = "price" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("numeric-1")
            .WithInput("data", currentData)
            .WithPreviousOutput(previousOutput)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var output = result.Output!.Value;
        output.GetProperty("value").GetDecimal().ShouldBe(90m);
        output.GetProperty("previousValue").GetDecimal().ShouldBe(100m);
        output.GetProperty("delta").GetDecimal().ShouldBe(-10m);
        output.GetProperty("deltaPercent").GetDecimal().ShouldBe(-10m);
        output.GetProperty("trend").GetString().ShouldBe("down");
        output.GetProperty("changed").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_NoPriceChange_ReportsNoChange()
    {
        var currentData = JsonSerializer.SerializeToElement(new { price = 100m });
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            value = 100m,
            field = "price",
            changed = false
        });

        var pipeline = CreatePipeline("numeric-1", new { field = "price" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("numeric-1")
            .WithInput("data", currentData)
            .WithPreviousOutput(previousOutput)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var output = result.Output!.Value;
        output.GetProperty("value").GetDecimal().ShouldBe(100m);
        output.GetProperty("delta").GetDecimal().ShouldBe(0m);
        output.GetProperty("trend").GetString().ShouldBe("flat");
        output.GetProperty("changed").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_PriceFromString_ParsesCorrectly()
    {
        var currentData = JsonSerializer.SerializeToElement(new { price = "$85.50" });
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            value = 100m,
            field = "price",
            changed = false
        });

        var pipeline = CreatePipeline("numeric-1", new { field = "price" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("numeric-1")
            .WithInput("data", currentData)
            .WithPreviousOutput(previousOutput)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("value").GetDecimal().ShouldBe(85.50m);
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task BlockType_ReturnsNumericDelta()
    {
        _sut.BlockType.ShouldBe("NumericDelta");
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
        _sut.OutputPorts.Count.ShouldBe(2);
        _sut.OutputPorts[0].Name.ShouldBe("result");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.DiffResult);
        _sut.OutputPorts[1].Name.ShouldBe("value");
        _sut.OutputPorts[1].Type.ShouldBe(PortType.NumericValue);
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
                Type = "NumericDelta",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
