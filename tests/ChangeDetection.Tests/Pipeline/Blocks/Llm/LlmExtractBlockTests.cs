using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Llm;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Llm;

[Category("Unit")]
public class LlmExtractBlockTests : TestBase
{
    private readonly LlmExtractBlock _sut = new();

    private static (ILlmProviderChain llm, IServiceProvider sp) CreateMockedServices()
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ILlmProviderChain)).Returns(llmChain);
        return (llmChain, sp);
    }

    [Test]
    public async Task ExecuteAsync_WithValidHtml_ExtractsData()
    {
        var (llm, sp) = CreateMockedServices();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = """{"price": 29.99, "title": "Widget"}""" });

        var config = new
        {
            prompt = "Extract the product details",
            outputSchema = new { price = "decimal", title = "string" }
        };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-extract-1")
            .WithInput("html", (object)"<html><body><span>$29.99</span></body></html>")
            .WithServices(sp)
            .WithPipelineDefinition(BlockContextBuilder.CreateSingleBlockPipeline("llm-extract-1", "LlmExtract", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var data = result.Output!.Value;
        data.GetProperty("price").GetDecimal().ShouldBe(29.99m);
        data.GetProperty("title").GetString().ShouldBe("Widget");
    }

    [Test]
    public async Task ExecuteAsync_LlmFails_ReturnsFailed()
    {
        var (llm, sp) = CreateMockedServices();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false, ErrorMessage = "Provider unavailable" });

        var config = new { prompt = "Extract data" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-extract-1")
            .WithInput("html", (object)"<html><body>Content</body></html>")
            .WithServices(sp)
            .WithPipelineDefinition(BlockContextBuilder.CreateSingleBlockPipeline("llm-extract-1", "LlmExtract", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("Provider unavailable");
    }

    [Test]
    public async Task ExecuteAsync_InvalidJsonResponse_ReturnsFailed()
    {
        var (llm, sp) = CreateMockedServices();
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = "This is not JSON at all" });

        var config = new { prompt = "Extract data" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-extract-1")
            .WithInput("html", (object)"<html><body>Content</body></html>")
            .WithServices(sp)
            .WithPipelineDefinition(BlockContextBuilder.CreateSingleBlockPipeline("llm-extract-1", "LlmExtract", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("not valid JSON");
    }

    [Test]
    public async Task ExecuteAsync_BuildsPromptWithHtmlAndSchema()
    {
        var (llm, sp) = CreateMockedServices();
        string? capturedPrompt = null;
        llm.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.Arg<string>();
                return new LlmResponse { IsSuccess = true, Content = """{"price": 9.99}""" };
            });

        var config = new
        {
            prompt = "Extract the product price",
            outputSchema = new { price = "decimal" }
        };

        var html = "<html><body><span class='price'>$9.99</span></body></html>";
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-extract-1")
            .WithInput("html", (object)html)
            .WithServices(sp)
            .WithPipelineDefinition(BlockContextBuilder.CreateSingleBlockPipeline("llm-extract-1", "LlmExtract", config))
            .Build();

        await _sut.ExecuteAsync(context);

        capturedPrompt.ShouldNotBeNull();
        capturedPrompt.ShouldContain("Extract the product price");
        capturedPrompt.ShouldContain(html);
        capturedPrompt.ShouldContain("price");
        capturedPrompt.ShouldContain("schema");
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
                return new LlmResponse { IsSuccess = true, Content = """{"data": true}""" };
            });

        var config = new { prompt = "Extract data" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-extract-1")
            .WithInput("html", (object)"<html><body>Content</body></html>")
            .WithServices(sp)
            .WithPipelineDefinition(BlockContextBuilder.CreateSingleBlockPipeline("llm-extract-1", "LlmExtract", config))
            .Build();

        await _sut.ExecuteAsync(context);

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.ExpectJson.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_NoHtmlInput_ReturnsFailed()
    {
        var (_, sp) = CreateMockedServices();
        var config = new { prompt = "Extract data" };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-extract-1")
            .WithServices(sp)
            .WithPipelineDefinition(BlockContextBuilder.CreateSingleBlockPipeline("llm-extract-1", "LlmExtract", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("html");
    }

    [Test]
    public async Task ExecuteAsync_NoPromptInConfig_ReturnsFailed()
    {
        var (_, sp) = CreateMockedServices();
        var config = new { outputSchema = new { price = "decimal" } };

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("llm-extract-1")
            .WithInput("html", (object)"<html><body>Content</body></html>")
            .WithServices(sp)
            .WithPipelineDefinition(BlockContextBuilder.CreateSingleBlockPipeline("llm-extract-1", "LlmExtract", config))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error.ShouldContain("prompt");
    }

    [Test]
    public async Task BlockType_ReturnsLlmExtract()
    {
        _sut.BlockType.ShouldBe("LlmExtract");
        await Task.CompletedTask;
    }

    [Test]
    public async Task CriticalityTier_ReturnsExtraction()
    {
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Extraction);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Ports_MatchExpectedDefinition()
    {
        _sut.InputPorts.Count.ShouldBe(1);
        _sut.InputPorts[0].Name.ShouldBe("html");
        _sut.InputPorts[0].Type.ShouldBe(PortType.HtmlContent);
        _sut.OutputPorts.Count.ShouldBe(1);
        _sut.OutputPorts[0].Name.ShouldBe("data");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.ExtractedObjects);
        await Task.CompletedTask;
    }
}
