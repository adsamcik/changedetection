using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using Microsoft.Playwright;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Acquisition;

[Category("Unit")]
public class WaitBlockTests : TestBase
{
    private readonly WaitBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_WithDelayConfig_Succeeds()
    {
        var mockPage = Substitute.For<IPage>();

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("wait-1", "Wait", new { forTime = 10 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("wait-1")
            .WithPage(mockPage)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
    }

    [Test]
    public async Task ExecuteAsync_NoPage_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("wait-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("page");
    }

    [Test]
    public async Task ExecuteAsync_WithSelectorConfig_CallsWaitForSelector()
    {
        var mockPage = Substitute.For<IPage>();
        mockPage.WaitForSelectorAsync(Arg.Any<string>(), Arg.Any<PageWaitForSelectorOptions>())
            .Returns((IElementHandle?)null);

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("wait-1", "Wait", new { forSelector = "#content" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("wait-1")
            .WithPage(mockPage)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        await mockPage.Received(1).WaitForSelectorAsync("#content", Arg.Any<PageWaitForSelectorOptions>());
    }

    [Test]
    public async Task ExecuteAsync_NoConfig_SucceedsWithoutWaiting()
    {
        var mockPage = Substitute.For<IPage>();

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("wait-1")
            .WithPage(mockPage)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
    }

    [Test]
    public async Task BlockType_ReturnsWait()
    {
        _sut.BlockType.ShouldBe("Wait");
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Infrastructure);
        await Task.CompletedTask;
    }
}
