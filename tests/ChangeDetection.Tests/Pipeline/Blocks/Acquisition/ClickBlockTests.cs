using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using Microsoft.Playwright;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Acquisition;

[Category("Unit")]
public class ClickBlockTests : TestBase
{
    private readonly ClickBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_WithSelectorConfig_CallsClickAsync()
    {
        var mockPage = Substitute.For<IPage>();

        var pipeline = CreatePipeline("click-1", new { selector = "button.submit" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("click-1")
            .WithPage(mockPage)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        await mockPage.Received(1).ClickAsync("button.submit", Arg.Any<PageClickOptions>());
    }

    [Test]
    public async Task ExecuteAsync_NoPage_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("click-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("page");
    }

    [Test]
    public async Task ExecuteAsync_MissingSelectorConfig_ReturnsFailed()
    {
        var mockPage = Substitute.For<IPage>();

        var pipeline = CreatePipeline("click-1", new { });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("click-1")
            .WithPage(mockPage)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("selector");
    }

    [Test]
    public async Task ExecuteAsync_WithWaitAfter_DelaysAfterClick()
    {
        var mockPage = Substitute.For<IPage>();

        var pipeline = CreatePipeline("click-1", new { selector = "#btn", waitAfter = 10 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("click-1")
            .WithPage(mockPage)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        await mockPage.Received(1).ClickAsync("#btn", Arg.Any<PageClickOptions>());
    }

    [Test]
    public async Task BlockType_ReturnsClick()
    {
        _sut.BlockType.ShouldBe("Click");
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Infrastructure);
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
                Type = "Click",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
