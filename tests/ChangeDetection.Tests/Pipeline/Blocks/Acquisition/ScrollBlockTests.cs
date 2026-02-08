using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using Microsoft.Playwright;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Acquisition;

[Category("Unit")]
public class ScrollBlockTests : TestBase
{
    private readonly ScrollBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_DefaultConfig_ScrollsDownOnce()
    {
        var mockPage = Substitute.For<IPage>();

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("scroll-1")
            .WithPage(mockPage)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        await mockPage.Received(1).EvaluateAsync(
            Arg.Is<string>(s => s.Contains("scrollBy")), Arg.Any<object>());
    }

    [Test]
    public async Task ExecuteAsync_NoPage_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("scroll-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("page");
    }

    [Test]
    public async Task ExecuteAsync_MultipleTimes_ScrollsRepeatedly()
    {
        var mockPage = Substitute.For<IPage>();

        var pipeline = CreatePipeline("scroll-1", new { times = 3, delay = 0 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("scroll-1")
            .WithPage(mockPage)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        await mockPage.Received(3).EvaluateAsync(
            Arg.Is<string>(s => s.Contains("scrollBy")), Arg.Any<object>());
    }

    [Test]
    public async Task ExecuteAsync_DirectionUp_ScrollsUp()
    {
        var mockPage = Substitute.For<IPage>();

        var pipeline = CreatePipeline("scroll-1", new { direction = "up", delay = 0 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("scroll-1")
            .WithPage(mockPage)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        await mockPage.Received(1).EvaluateAsync(
            Arg.Is<string>(s => s.Contains("-window.innerHeight")), Arg.Any<object>());
    }

    [Test]
    public async Task BlockType_ReturnsScroll()
    {
        _sut.BlockType.ShouldBe("Scroll");
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
                Type = "Scroll",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
