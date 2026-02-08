using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Comparison;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Comparison;

[Category("Unit")]
public class HashCompareBlockTests : TestBase
{
    private readonly HashCompareBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_FirstRun_ReturnsBaseline()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Hello", price = "9.99" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("hash-1")
            .WithInput("data", data)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Baseline);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
        result.Output!.Value.GetProperty("hash").GetString().ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task ExecuteAsync_SameHash_ReportsNoChange()
    {
        var data = JsonSerializer.SerializeToElement(new { title = "Hello", price = "9.99" });
        var hash = ComputeHashForElement(data);

        var previousOutput = JsonSerializer.SerializeToElement(new { hash, changed = false });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("hash-1")
            .WithInput("data", data)
            .WithPreviousOutput(previousOutput)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
        result.Output!.Value.GetProperty("hash").GetString().ShouldBe(hash);
        result.Output!.Value.GetProperty("previousHash").GetString().ShouldBe(hash);
    }

    [Test]
    public async Task ExecuteAsync_DifferentHash_ReportsChange()
    {
        var currentData = JsonSerializer.SerializeToElement(new { title = "Updated", price = "7.99" });
        var previousData = JsonSerializer.SerializeToElement(new { title = "Hello", price = "9.99" });
        var previousHash = ComputeHashForElement(previousData);

        var previousOutput = JsonSerializer.SerializeToElement(new { hash = previousHash, changed = false });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("hash-1")
            .WithInput("data", currentData)
            .WithPreviousOutput(previousOutput)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();
        result.Output!.Value.GetProperty("hash").GetString().ShouldNotBe(previousHash);
        result.Output!.Value.GetProperty("previousHash").GetString().ShouldBe(previousHash);
    }

    [Test]
    public async Task BlockType_ReturnsHashCompare()
    {
        _sut.BlockType.ShouldBe("HashCompare");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CriticalityTier_ReturnsAnalysis()
    {
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

    private static string ComputeHashForElement(JsonElement element)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(element.GetRawText()));
        return Convert.ToHexStringLower(bytes);
    }
}
