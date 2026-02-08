using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Decision;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Decision;

[Category("Unit")]
public class NotifyBlockTests : TestBase
{
    private readonly NotifyBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_SignalTrue_SendsNotification()
    {
        var signal = JsonSerializer.SerializeToElement(new { signal = true });
        var pipeline = CreatePipeline("notify-1", new { channel = "email", template = "Price dropped!" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("notify-1")
            .WithInput("signal", signal)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("sent").GetBoolean().ShouldBeTrue();
        result.Output!.Value.GetProperty("channel").GetString().ShouldBe("email");
        result.Output!.Value.GetProperty("summary").GetString().ShouldBe("Price dropped!");
    }

    [Test]
    public async Task ExecuteAsync_SignalFalse_Skips()
    {
        var signal = JsonSerializer.SerializeToElement(new { signal = false });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("notify-1")
            .WithInput("signal", signal)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Skipped);
        result.SkipReason.ShouldBe("No notification needed");
    }

    [Test]
    public async Task ExecuteAsync_FirstRun_Skips()
    {
        var signal = JsonSerializer.SerializeToElement(new { signal = true });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("notify-1")
            .WithInput("signal", signal)
            .WithFirstRun()
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Skipped);
        result.SkipReason!.ShouldContain("First run");
    }

    [Test]
    public async Task ExecuteAsync_NoSignal_Skips()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("notify-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Skipped);
        result.SkipReason.ShouldBe("No signal input");
    }

    [Test]
    public async Task BlockType_ReturnsNotify()
    {
        _sut.BlockType.ShouldBe("Notify");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CriticalityTier_ReturnsDelivery()
    {
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Delivery);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.Count.ShouldBe(2);
        _sut.InputPorts[0].Name.ShouldBe("signal");
        _sut.InputPorts[0].Type.ShouldBe(PortType.BooleanSignal);
        _sut.InputPorts[0].Required.ShouldBeTrue();
        _sut.InputPorts[1].Name.ShouldBe("data");
        _sut.InputPorts[1].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.InputPorts[1].Required.ShouldBeFalse();
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("notification");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.Notification);
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
                Type = "Notify",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
