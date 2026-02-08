using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Advanced;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Advanced;

[Category("Unit")]
public class TransformBlockTests : TestBase
{
    private readonly TransformBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_RenameFields_RenamesCorrectly()
    {
        var pipeline = CreatePipeline("transform-1", new
        {
            rename = new Dictionary<string, string> { ["old_name"] = "new_name" }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("transform-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { old_name = "value", other = 42 }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.TryGetProperty("old_name", out _).ShouldBeFalse();
        result.Output!.Value.GetProperty("new_name").GetString().ShouldBe("value");
        result.Output!.Value.GetProperty("other").GetInt32().ShouldBe(42);
    }

    [Test]
    public async Task ExecuteAsync_DropFields_RemovesFields()
    {
        var pipeline = CreatePipeline("transform-1", new
        {
            drop = new[] { "unwanted" }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("transform-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { keep = "yes", unwanted = "no" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("keep").GetString().ShouldBe("yes");
        result.Output!.Value.TryGetProperty("unwanted", out _).ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_ComputeFields_PerformsTemplateSubstitution()
    {
        var pipeline = CreatePipeline("transform-1", new
        {
            compute = new Dictionary<string, string> { ["fullName"] = "${firstName} ${lastName}" }
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("transform-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { firstName = "John", lastName = "Doe" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("fullName").GetString().ShouldBe("John Doe");
    }

    [Test]
    public async Task ExecuteAsync_NoConfig_PassesThrough()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("transform-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { a = 1 }))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("a").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("transform-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task BlockType_ReturnsTransform()
    {
        _sut.BlockType.ShouldBe("Transform");
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
                Type = "Transform",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
