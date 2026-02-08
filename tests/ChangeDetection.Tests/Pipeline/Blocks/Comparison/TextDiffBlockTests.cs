using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Comparison;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Comparison;

[Category("Unit")]
public class TextDiffBlockTests : TestBase
{
    private readonly TextDiffBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_WithChangedContent_ReturnsHasChanges()
    {
        var diffService = Substitute.For<IDiffService>();
        diffService.Compare(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new DiffResult { HasChanges = true, LinesAdded = 2, LinesRemoved = 1, LinesUnchanged = 5 });
        diffService.GenerateSummary(Arg.Any<DiffResult>()).Returns("2 added, 1 removed");

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IDiffService)).Returns(diffService);

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("textdiff-1")
            .WithInput("current", JsonSerializer.SerializeToElement("new content"))
            .WithInput("previous", JsonSerializer.SerializeToElement("old content"))
            .WithServices(sp)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("hasChanges").GetBoolean().ShouldBeTrue();
        result.Output!.Value.GetProperty("linesAdded").GetInt32().ShouldBe(2);
        result.Output!.Value.GetProperty("linesRemoved").GetInt32().ShouldBe(1);
        result.Output!.Value.GetProperty("summary").GetString().ShouldBe("2 added, 1 removed");
    }

    [Test]
    public async Task ExecuteAsync_WithIdenticalContent_ReturnsNoChanges()
    {
        var diffService = Substitute.For<IDiffService>();
        diffService.Compare(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new DiffResult { HasChanges = false, LinesAdded = 0, LinesRemoved = 0, LinesUnchanged = 3 });
        diffService.GenerateSummary(Arg.Any<DiffResult>()).Returns("No changes");

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IDiffService)).Returns(diffService);

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("textdiff-1")
            .WithInput("current", JsonSerializer.SerializeToElement("same content"))
            .WithInput("previous", JsonSerializer.SerializeToElement("same content"))
            .WithServices(sp)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("hasChanges").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_MissingCurrentInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("textdiff-1")
            .WithInput("previous", JsonSerializer.SerializeToElement("old"))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("current");
    }

    [Test]
    public async Task ExecuteAsync_MissingPreviousInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("textdiff-1")
            .WithInput("current", JsonSerializer.SerializeToElement("new"))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("previous");
    }

    [Test]
    public async Task BlockType_ReturnsTextDiff()
    {
        _sut.BlockType.ShouldBe("TextDiff");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.Count.ShouldBe(2);
        _sut.InputPorts[0].Name.ShouldBe("current");
        _sut.InputPorts[0].Type.ShouldBe(PortType.PlainText);
        _sut.InputPorts[1].Name.ShouldBe("previous");
        _sut.InputPorts[1].Type.ShouldBe(PortType.PlainText);
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("result");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.DiffResult);
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Analysis);
        await Task.CompletedTask;
    }
}
