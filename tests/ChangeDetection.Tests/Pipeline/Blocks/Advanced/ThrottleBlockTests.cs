using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Advanced;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Advanced;

[Category("Unit")]
public class ThrottleBlockTests : TestBase
{
    private readonly ThrottleBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_NoPreviousState_PassesThrough()
    {
        var pipeline = CreatePipeline("throttle-1", new { cooldown = "1h" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("throttle-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { value = 42 }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("value").GetInt32().ShouldBe(42);
    }

    [Test]
    public async Task ExecuteAsync_CooldownNotElapsed_ReturnsSkip()
    {
        var pipeline = CreatePipeline("throttle-1", new { cooldown = "1h" });

        // Last pass-through was 30 minutes ago
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            _throttleTimestamp = DateTime.UtcNow.AddMinutes(-30).ToString("O")
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("throttle-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { value = 43 }))
            .WithPreviousOutput(previousOutput)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Skipped);
        result.SkipReason.ShouldContain("cooldown not elapsed");
    }

    [Test]
    public async Task ExecuteAsync_CooldownElapsed_PassesThrough()
    {
        var pipeline = CreatePipeline("throttle-1", new { cooldown = "1h" });

        // Last pass-through was 2 hours ago
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            _throttleTimestamp = DateTime.UtcNow.AddHours(-2).ToString("O")
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("throttle-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { value = 43 }))
            .WithPreviousOutput(previousOutput)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("value").GetInt32().ShouldBe(43);
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var pipeline = CreatePipeline("throttle-1", new { cooldown = "1h" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("throttle-1")
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task ExecuteAsync_NoCooldownConfig_DefaultsToOneHour()
    {
        var pipeline = CreatePipeline("throttle-1", new { });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("throttle-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { value = 1 }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        // Should pass through with default 1-hour cooldown (no previous state)
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
    }

    [Test]
    public async Task ParseDuration_VariousFormats_ParsesCorrectly()
    {
        ThrottleBlock.ParseDuration("15s").ShouldBe(TimeSpan.FromSeconds(15));
        ThrottleBlock.ParseDuration("30m").ShouldBe(TimeSpan.FromMinutes(30));
        ThrottleBlock.ParseDuration("1h").ShouldBe(TimeSpan.FromHours(1));
        ThrottleBlock.ParseDuration("1d").ShouldBe(TimeSpan.FromDays(1));
        ThrottleBlock.ParseDuration("invalid").ShouldBeNull();
        ThrottleBlock.ParseDuration("").ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BlockType_ReturnsThrottle()
    {
        _sut.BlockType.ShouldBe("Throttle");
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
                Type = "Throttle",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
