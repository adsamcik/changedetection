using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks;

[Category("Unit")]
public class PortTypeTests : TestBase
{
    [Test]
    public async Task AllPortTypes_HaveDistinctValues()
    {
        var values = Enum.GetValues<PortType>();

        values.Length.ShouldBe(values.Distinct().Count());
        values.Length.ShouldBe(11);
        await Task.CompletedTask;
    }

    [Test]
    public async Task PortDescriptor_Required_DefaultsToTrue()
    {
        var descriptor = new PortDescriptor
        {
            Name = "content",
            Type = PortType.HtmlContent
        };

        descriptor.Required.ShouldBeTrue();
        descriptor.Description.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BlockResult_Succeeded_HasCorrectStatus()
    {
        var output = JsonDocument.Parse("""{"value": 42}""").RootElement.Clone();

        var result = BlockResult.Succeeded(output);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Error.ShouldBeNull();
        result.SkipReason.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BlockResult_Failed_HasCorrectStatus()
    {
        var result = BlockResult.Failed("Something went wrong");

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldBe("Something went wrong");
        result.Output.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BlockResult_Skip_HasCorrectStatus()
    {
        var result = BlockResult.Skip("Condition not met");

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Skipped);
        result.SkipReason.ShouldBe("Condition not met");
        result.Output.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BlockResult_BaselineCapture_HasCorrectStatus()
    {
        var output = JsonDocument.Parse("""{"baseline": true}""").RootElement.Clone();

        var result = BlockResult.BaselineCapture(output);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Baseline);
        result.Output.ShouldNotBeNull();
        result.Error.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BlockResult_CachedResult_HasCorrectStatus()
    {
        var output = JsonDocument.Parse("""{"cached": true}""").RootElement.Clone();

        var result = BlockResult.CachedResult(output);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.CacheHit.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Error.ShouldBeNull();
        await Task.CompletedTask;
    }
}
