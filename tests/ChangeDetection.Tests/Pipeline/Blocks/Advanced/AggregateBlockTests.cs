using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Advanced;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Advanced;

[Category("Unit")]
public class AggregateBlockTests : TestBase
{
    private readonly AggregateBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_GroupsAndSummarizes()
    {
        var pipeline = CreatePipeline("agg-1", new
        {
            groupBy = "category",
            summarize = new Dictionary<string, string>
            {
                ["count"] = "count",
                ["total"] = "sum:price"
            }
        });

        var data = new[]
        {
            new { category = "electronics", price = 99.99 },
            new { category = "electronics", price = 149.99 },
            new { category = "books", price = 12.99 }
        };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("agg-1")
            .WithInput("data", JsonSerializer.SerializeToElement(data))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var groups = result.Output!.Value.GetProperty("groups");
        groups.GetArrayLength().ShouldBe(2);
    }

    [Test]
    public async Task ExecuteAsync_SingleObject_WrapsInArray()
    {
        var pipeline = CreatePipeline("agg-1", new
        {
            groupBy = "type",
            summarize = new Dictionary<string, string> { ["count"] = "count" }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("agg-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { type = "item", value = 10 }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var groups = result.Output!.Value.GetProperty("groups");
        groups.GetArrayLength().ShouldBe(1);
        groups[0].GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_MissingGroupByConfig_ReturnsFailed()
    {
        var pipeline = CreatePipeline("agg-1", new
        {
            summarize = new Dictionary<string, string> { ["count"] = "count" }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("agg-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new[] { new { a = 1 } }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("groupBy");
    }

    [Test]
    public async Task ExecuteAsync_MinMaxAvg_ComputesCorrectly()
    {
        var pipeline = CreatePipeline("agg-1", new
        {
            groupBy = "group",
            summarize = new Dictionary<string, string>
            {
                ["minimum"] = "min:val",
                ["maximum"] = "max:val",
                ["average"] = "avg:val"
            }
        });

        var data = new[]
        {
            new { group = "A", val = 10 },
            new { group = "A", val = 20 },
            new { group = "A", val = 30 }
        };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("agg-1")
            .WithInput("data", JsonSerializer.SerializeToElement(data))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var group = result.Output!.Value.GetProperty("groups")[0];
        group.GetProperty("minimum").GetDecimal().ShouldBe(10);
        group.GetProperty("maximum").GetDecimal().ShouldBe(30);
        group.GetProperty("average").GetDecimal().ShouldBe(20);
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("agg-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task BlockType_ReturnsAggregate()
    {
        _sut.BlockType.ShouldBe("Aggregate");
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
                Type = "Aggregate",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
