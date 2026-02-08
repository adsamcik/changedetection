using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Extraction;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Extraction;

[Category("Unit")]
public class ExtractSchemaBlockTests : TestBase
{
    private readonly ExtractSchemaBlock _sut = new();
    private readonly IContentExtractor _extractor = Substitute.For<IContentExtractor>();

    private IServiceProvider CreateServices()
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContentExtractor)).Returns(_extractor);
        return services;
    }

    [Test]
    public async Task ExecuteAsync_WithSchema_ExtractsFields()
    {
        const string inputHtml = "<html><body><span class='price'>$29.99</span><h1>Widget</h1></body></html>";

        _extractor.ExtractText(inputHtml, cssSelector: ".price").Returns("$29.99");
        _extractor.ExtractText(inputHtml, cssSelector: "h1").Returns("Widget");

        var config = new
        {
            schema = new[]
            {
                new { field = "price", selector = ".price", type = "text" },
                new { field = "title", selector = "h1", type = "text" }
            }
        };

        var pipeline = CreatePipeline("extract-1", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("extract-1")
            .WithInput("html", (object)inputHtml)
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();

        var data = result.Output!.Value;
        data.GetProperty("price").GetString().ShouldBe("$29.99");
        data.GetProperty("title").GetString().ShouldBe("Widget");

        _extractor.Received(1).ExtractText(inputHtml, cssSelector: ".price");
        _extractor.Received(1).ExtractText(inputHtml, cssSelector: "h1");
    }

    [Test]
    public async Task ExecuteAsync_WithScope_NarrowsFirst()
    {
        const string inputHtml = "<html><body><div id='product'><span class='price'>$9.99</span></div></body></html>";
        const string scopedHtml = "<div id='product'><span class='price'>$9.99</span></div>";

        _extractor.ExtractHtml(inputHtml, cssSelector: "#product").Returns(scopedHtml);
        _extractor.ExtractText(scopedHtml, cssSelector: ".price").Returns("$9.99");

        var config = new
        {
            scope = "#product",
            schema = new[]
            {
                new { field = "price", selector = ".price", type = "text" }
            }
        };

        var pipeline = CreatePipeline("extract-1", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("extract-1")
            .WithInput("html", (object)inputHtml)
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetProperty("price").GetString().ShouldBe("$9.99");

        _extractor.Received(1).ExtractHtml(inputHtml, cssSelector: "#product");
        _extractor.Received(1).ExtractText(scopedHtml, cssSelector: ".price");
    }

    [Test]
    public async Task ExecuteAsync_NoSchema_ReturnsFailed()
    {
        const string inputHtml = "<html><body>Hello</body></html>";

        var pipeline = CreatePipeline("extract-1", new { });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("extract-1")
            .WithInput("html", (object)inputHtml)
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error!.ShouldContain("No extraction schema configured");
    }

    [Test]
    public async Task ExecuteAsync_EmptyHtml_ReturnsFailed()
    {
        var config = new
        {
            schema = new[]
            {
                new { field = "price", selector = ".price", type = "text" }
            }
        };

        var pipeline = CreatePipeline("extract-1", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("extract-1")
            .WithInput("html", (object)"")
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("empty or invalid HTML");
    }

    [Test]
    public async Task BlockType_ReturnsExtractSchema()
    {
        _sut.BlockType.ShouldBe("ExtractSchema");
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

    private static PipelineDefinition CreatePipeline(string blockId, object config) => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            new BlockDefinition
            {
                Id = blockId,
                Type = "ExtractSchema",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
