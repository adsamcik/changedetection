using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Acquisition;

[Category("Unit")]
public class InputBlockTests : TestBase
{
    private readonly InputBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_WithValidConfig_ReturnsUrl()
    {
        var pipeline = CreatePipeline("input-1", new { url = "https://example.com" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("input-1")
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("url").GetString().ShouldBe("https://example.com");
    }

    [Test]
    public async Task ExecuteAsync_WithMissingUrl_ReturnsFailed()
    {
        var pipeline = CreatePipeline("input-1", new { checkInterval = "6h" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("input-1")
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error!.ShouldContain("url");
    }

    [Test]
    public async Task ExecuteAsync_WithMetadata_IncludesInOutput()
    {
        var pipeline = CreatePipeline("input-1", new
        {
            url = "https://example.com",
            checkInterval = "6h",
            metadata = new { source = "manual" }
        });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("input-1")
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("url").GetString().ShouldBe("https://example.com");
        var config = result.Output!.Value.GetProperty("config");
        config.GetProperty("checkInterval").GetString().ShouldBe("6h");
    }

    [Test]
    public async Task ExecuteAsync_WithNoPipelineDefinition_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("input-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("pipeline definition");
    }

    [Test]
    public async Task ExecuteAsync_WithNoConfig_ReturnsFailed()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks = [new BlockDefinition { Id = "input-1", Type = "Input" }],
            Connections = []
        };
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("input-1")
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("configuration");
    }

    [Test]
    public async Task BlockType_ReturnsInput()
    {
        _sut.BlockType.ShouldBe("Input");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CriticalityTier_ReturnsInfrastructure()
    {
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Infrastructure);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.ShouldBeEmpty();
        _sut.OutputPorts.Count.ShouldBe(2);
        _sut.OutputPorts[0].Name.ShouldBe("url");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.Url);
        _sut.OutputPorts[1].Name.ShouldBe("config");
        _sut.OutputPorts[1].Type.ShouldBe(PortType.Configuration);
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
                Type = "Input",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
