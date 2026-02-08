using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Advanced;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Advanced;

[Category("Unit")]
public class RouteBlockTests : TestBase
{
    private readonly RouteBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_MatchingEqualsCondition_RoutesCorrectly()
    {
        var pipeline = CreatePipeline("route-1", new
        {
            conditions = new[]
            {
                new { field = "type", equals = "price_drop", output = "alert" },
                new { field = "type", equals = "info", output = "log" }
            }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("route-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { type = "price_drop", amount = 5 }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("_route").GetString().ShouldBe("alert");
        result.Output!.Value.GetProperty("type").GetString().ShouldBe("price_drop");
    }

    [Test]
    public async Task ExecuteAsync_NoMatchingCondition_RoutesToDefault()
    {
        var pipeline = CreatePipeline("route-1", new
        {
            conditions = new[]
            {
                new { field = "type", equals = "price_drop", output = "alert" }
            }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("route-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { type = "unknown" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("_route").GetString().ShouldBe("default");
    }

    [Test]
    public async Task ExecuteAsync_ContainsCondition_MatchesSubstring()
    {
        var config = JsonSerializer.SerializeToElement(new
        {
            conditions = new object[]
            {
                new { field = "message", contains = "error", output = "errors" }
            }
        });

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = [new BlockDefinition { Id = "route-1", Type = "Route", Config = config }],
            Connections = []
        };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("route-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { message = "An error occurred" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("_route").GetString().ShouldBe("errors");
    }

    [Test]
    public async Task ExecuteAsync_NoConditionsConfig_RoutesToDefault()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("route-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { a = 1 }))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("_route").GetString().ShouldBe("default");
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("route-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task BlockType_ReturnsRoute()
    {
        _sut.BlockType.ShouldBe("Route");
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
                Type = "Route",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
