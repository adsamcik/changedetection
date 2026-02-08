using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Advanced;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Advanced;

[Category("Unit")]
public class LookupHistoryBlockTests : TestBase
{
    private readonly LookupHistoryBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_FirstRun_ReturnsCurrentWithEmptyHistory()
    {
        var pipeline = CreatePipeline("history-1", new { field = "price", period = "7d" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("history-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { price = 29.99, title = "Widget" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("current").GetDouble().ShouldBe(29.99);
        result.Output!.Value.GetProperty("field").GetString().ShouldBe("price");
        result.Output!.Value.GetProperty("history").GetArrayLength().ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_WithHistory_AccumulatesValues()
    {
        var pipeline = CreatePipeline("history-1", new { field = "price", period = "7d" });

        var previousHistory = JsonSerializer.SerializeToElement(new
        {
            current = 25.00,
            field = "price",
            history = new[]
            {
                new { timestamp = DateTime.UtcNow.AddDays(-1).ToString("O"), value = 25.00 }
            }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("history-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { price = 29.99 }))
            .WithPreviousOutput(previousHistory)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("current").GetDouble().ShouldBe(29.99);
        result.Output!.Value.GetProperty("field").GetString().ShouldBe("price");
        result.Output!.Value.GetProperty("history").GetArrayLength().ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_PeriodFilter_DropsOldEntries()
    {
        var pipeline = CreatePipeline("history-1", new { field = "price", period = "7d" });

        var previousHistory = JsonSerializer.SerializeToElement(new
        {
            current = 25.00,
            field = "price",
            history = new[]
            {
                new { timestamp = DateTime.UtcNow.AddDays(-10).ToString("O"), value = 20.00 },
                new { timestamp = DateTime.UtcNow.AddDays(-1).ToString("O"), value = 25.00 }
            }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("history-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { price = 30.00 }))
            .WithPreviousOutput(previousHistory)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        // Old entry (10 days ago) should be filtered out, only 1-day-ago + current remain
        result.Output!.Value.GetProperty("history").GetArrayLength().ShouldBe(2);
        result.Output!.Value.GetProperty("current").GetDouble().ShouldBe(30.00);
    }

    [Test]
    public async Task ExecuteAsync_MissingFieldConfig_ReturnsFailed()
    {
        var pipeline = CreatePipeline("history-1", new { period = "7d" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("history-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { price = 10 }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("field");
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("history-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task BlockType_ReturnsLookupHistory()
    {
        _sut.BlockType.ShouldBe("LookupHistory");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.Count.ShouldBe(1);
        _sut.InputPorts[0].Name.ShouldBe("data");
        _sut.InputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("data");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Analysis);
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
                Type = "LookupHistory",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
