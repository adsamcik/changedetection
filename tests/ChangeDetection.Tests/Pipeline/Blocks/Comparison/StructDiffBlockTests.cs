using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Comparison;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Comparison;

[Category("Unit")]
public class StructDiffBlockTests : TestBase
{
    private readonly StructDiffBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_FirstRun_ReturnsBaseline()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Hello", price = "9.99" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("struct-1")
            .WithInput("data", data)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Baseline);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
        result.Output!.Value.GetProperty("snapshot").GetProperty("title").GetString().ShouldBe("Hello");
    }

    [Test]
    public async Task ExecuteAsync_IdenticalData_ReportsNoChanges()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Hello", price = "9.99" });
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            snapshot = new { title = "Hello", price = "9.99" },
            changed = false
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("struct-1")
            .WithInput("data", data)
            .WithPreviousOutput(previousOutput)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
        result.Output!.Value.GetProperty("changes").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task ExecuteAsync_ModifiedField_DetectsChange()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Hello", price = "7.99" });
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            snapshot = new { title = "Hello", price = "9.99" },
            changed = false
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("struct-1")
            .WithInput("data", data)
            .WithPreviousOutput(previousOutput)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();

        var changes = result.Output!.Value.GetProperty("changes");
        changes.GetArrayLength().ShouldBe(1);
        changes[0].GetProperty("field").GetString().ShouldBe("price");
        changes[0].GetProperty("old").GetString().ShouldBe("9.99");
        changes[0].GetProperty("new").GetString().ShouldBe("7.99");
    }

    [Test]
    public async Task ExecuteAsync_AddedField_DetectsAddition()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Hello", price = "9.99", stock = "in stock" });
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            snapshot = new { title = "Hello", price = "9.99" },
            changed = false
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("struct-1")
            .WithInput("data", data)
            .WithPreviousOutput(previousOutput)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();

        var changes = result.Output!.Value.GetProperty("changes");
        changes.GetArrayLength().ShouldBe(1);
        changes[0].GetProperty("field").GetString().ShouldBe("stock");
        changes[0].GetProperty("new").GetString().ShouldBe("in stock");
    }

    [Test]
    public async Task ExecuteAsync_RemovedField_DetectsRemoval()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Hello" });
        var previousOutput = JsonSerializer.SerializeToElement(new
        {
            snapshot = new { title = "Hello", price = "9.99" },
            changed = false
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("struct-1")
            .WithInput("data", data)
            .WithPreviousOutput(previousOutput)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();

        var changes = result.Output!.Value.GetProperty("changes");
        changes.GetArrayLength().ShouldBe(1);
        changes[0].GetProperty("field").GetString().ShouldBe("price");
        changes[0].GetProperty("old").GetString().ShouldBe("9.99");
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("struct-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task BlockType_ReturnsStructDiff()
    {
        _sut.BlockType.ShouldBe("StructDiff");
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Analysis);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.Count.ShouldBe(1);
        _sut.InputPorts[0].Name.ShouldBe("data");
        _sut.InputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("result");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.DiffResult);
        await Task.CompletedTask;
    }
}
