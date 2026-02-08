using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Output;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Output;

[Category("Unit")]
public class OutputBlockTests : TestBase
{
    private readonly OutputBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_WithData_PassesThrough()
    {
        var data = JsonSerializer.SerializeToElement(new { price = 29.99, title = "Widget" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("output-1")
            .WithInput("data", data)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("price").GetDouble().ShouldBe(29.99);
        result.Output!.Value.GetProperty("title").GetString().ShouldBe("Widget");
    }

    [Test]
    public async Task ExecuteAsync_NoData_ReturnsEmptyObject()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("output-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Test]
    public async Task BlockType_ReturnsOutput()
    {
        _sut.BlockType.ShouldBe("Output");
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
        _sut.InputPorts.Count.ShouldBe(1);
        _sut.InputPorts[0].Name.ShouldBe("data");
        _sut.InputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.OutputPorts.Count.ShouldBe(0);
        await Task.CompletedTask;
    }
}
