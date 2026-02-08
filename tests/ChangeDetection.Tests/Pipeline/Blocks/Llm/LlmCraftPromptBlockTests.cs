using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Llm;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Llm;

[Category("Unit")]
public class LlmCraftPromptBlockTests : TestBase
{
    private readonly LlmCraftPromptBlock _sut = new();

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
                Type = "LlmCraftPrompt",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };

    [Test]
    public async Task ExecuteAsync_WithInstructions_GeneratesPrompt()
    {
        var (llm, sp) = CreateMockedServices();
        const string generatedPrompt = "Evaluate whether the following content discusses cybersecurity threats or vulnerabilities.";
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = generatedPrompt });

        var config = new { instructions = "Generate a prompt to evaluate cybersecurity relevance" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-craft-1")
            .WithInput("data", new { topic = "cybersecurity", articles = new[] { "Breach at Company X" } })
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-craft-1", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var outputText = result.Output!.Value.GetString();
        outputText.ShouldBe(generatedPrompt);
    }

    [Test]
    public async Task ExecuteAsync_LlmFails_ReturnsFailed()
    {
        var (llm, sp) = CreateMockedServices();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false, ErrorMessage = "Service unavailable" });

        var config = new { instructions = "Generate a prompt" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-craft-1")
            .WithInput("data", new { value = "test" })
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-craft-1", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("Service unavailable");
    }

    [Test]
    public async Task ExecuteAsync_DoesNotUseExpectJson()
    {
        var (llm, sp) = CreateMockedServices();
        LlmRequestOptions? capturedOptions = null;
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedOptions = callInfo.Arg<LlmRequestOptions?>();
                return new LlmResponse { IsSuccess = true, Content = "A generated prompt" };
            });

        var config = new { instructions = "Generate a prompt" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-craft-1")
            .WithInput("data", new { value = 1 })
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-craft-1", config))
            .Build();

        await _sut.ExecuteAsync(context);

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.ExpectJson.ShouldBeFalse();
    }

    [Test]
    public async Task ExecuteAsync_NoDataInput_ReturnsFailed()
    {
        var (_, sp) = CreateMockedServices();
        var config = new { instructions = "Generate a prompt" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-craft-1")
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-craft-1", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task ExecuteAsync_NoInstructionsInConfig_ReturnsFailed()
    {
        var (_, sp) = CreateMockedServices();
        var config = new { other = "irrelevant" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-craft-1")
            .WithInput("data", new { value = "test" })
            .WithServices(sp)
            .WithPipelineDefinition(CreatePipeline("llm-craft-1", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("instructions");
    }

    [Test]
    public async Task BlockType_ReturnsLlmCraftPrompt()
    {
        _sut.BlockType.ShouldBe("LlmCraftPrompt");
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
        _sut.OutputPorts[0].Name.ShouldBe("data");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.PlainText);
        await Task.CompletedTask;
    }
}
