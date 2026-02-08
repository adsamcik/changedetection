using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Llm;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Llm;

[Category("Unit")]
public class LlmEvaluateBlockTests : TestBase
{
    private readonly LlmEvaluateBlock _sut = new();

    private static (ILlmProviderChain llm, IServiceProvider sp) CreateMockedServices()
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ILlmProviderChain)).Returns(llmChain);
        return (llmChain, sp);
    }

    private static PipelineDefinition CreatePipeline(string blockId, object config) => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            new BlockDefinition
            {
                Id = blockId,
                Type = "LlmEvaluate",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };

    [Test]
    public async Task ExecuteAsync_WithValidData_ReturnsEvaluation()
    {
        var (llm, sp) = CreateMockedServices();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """{"relevant": true, "reason": "Contains AI regulation content"}"""
            });

        var config = new
        {
            prompt = "Which headlines relate to AI regulation?",
            outputSchema = new { relevant = "boolean", reason = "string" }
        };

        var inputData = new { headlines = new[] { "New AI Bill Proposed", "Weather Update" } };
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-eval-1")
            .WithInput("data", inputData)
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-eval-1", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var data = result.Output!.Value;
        data.GetProperty("relevant").GetBoolean().ShouldBeTrue();
        data.GetProperty("reason").GetString().ShouldContain("AI regulation");
    }

    [Test]
    public async Task ExecuteAsync_LlmFails_ReturnsFailed()
    {
        var (llm, sp) = CreateMockedServices();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false, ErrorMessage = "Rate limit exceeded" });

        var config = new { prompt = "Evaluate the data" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-eval-1")
            .WithInput("data", new { items = new[] { "a", "b" } })
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-eval-1", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("Rate limit exceeded");
    }

    [Test]
    public async Task ExecuteAsync_NoData_ReturnsFailed()
    {
        var (_, sp) = CreateMockedServices();
        var config = new { prompt = "Evaluate the data" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-eval-1")
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-eval-1", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task ExecuteAsync_UsesExpectJsonOption()
    {
        var (llm, sp) = CreateMockedServices();
        LlmRequestOptions? capturedOptions = null;
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedOptions = callInfo.Arg<LlmRequestOptions?>();
                return new LlmResponse { IsSuccess = true, Content = """{"result": true}""" };
            });

        var config = new { prompt = "Evaluate" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-eval-1")
            .WithInput("data", new { value = 1 })
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-eval-1", config))
            .Build();

        await _sut.ExecuteAsync(context);

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.ExpectJson.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_NoPromptInConfig_ReturnsFailed()
    {
        var (_, sp) = CreateMockedServices();
        var config = new { outputSchema = new { relevant = "boolean" } };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-eval-1")
            .WithInput("data", new { items = new[] { "test" } })
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-eval-1", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("prompt");
    }

    [Test]
    public async Task BlockType_ReturnsLlmEvaluate()
    {
        _sut.BlockType.ShouldBe("LlmEvaluate");
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
}
