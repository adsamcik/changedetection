using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Extraction;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Extraction;

[Category("Unit")]
public class FilterBlockTests : TestBase
{
    private readonly FilterBlock _sut = new();
    private readonly IContentExtractor _extractor = Substitute.For<IContentExtractor>();

    private IServiceProvider CreateServices()
    {
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContentExtractor)).Returns(_extractor);
        return services;
    }

    [Test]
    public async Task ExecuteAsync_WithCssSelector_FiltersHtml()
    {
        const string inputHtml = "<html><body><div class='content'>Hello</div></body></html>";
        const string filteredHtml = "<div class='content'>Hello</div>";

        _extractor.ExtractHtml(inputHtml, cssSelector: "div.content").Returns(filteredHtml);

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("filter-1", "Filter", new { css = "div.content" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("filter-1")
            .WithInput("html", (object)inputHtml)
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetString().ShouldBe(filteredHtml);

        _extractor.Received(1).ExtractHtml(inputHtml, cssSelector: "div.content");
    }

    [Test]
    public async Task ExecuteAsync_WithXPathSelector_FiltersHtml()
    {
        const string inputHtml = "<html><body><div id='main'>Content</div></body></html>";
        const string filteredHtml = "<div id='main'>Content</div>";

        _extractor.ExtractHtml(inputHtml, xpathSelector: "//div[@id='main']").Returns(filteredHtml);

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("filter-1", "Filter", new { xpath = "//div[@id='main']" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("filter-1")
            .WithInput("html", (object)inputHtml)
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetString().ShouldBe(filteredHtml);

        _extractor.Received(1).ExtractHtml(inputHtml, xpathSelector: "//div[@id='main']");
    }

    [Test]
    public async Task ExecuteAsync_WithRegex_FiltersHtml()
    {
        const string inputHtml = "<html><body>Price: $29.99 today</body></html>";

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("filter-1", "Filter", new { regex = @"\$\d+\.\d{2}" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("filter-1")
            .WithInput("html", (object)inputHtml)
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetString().ShouldBe("$29.99");
    }

    [Test]
    public async Task ExecuteAsync_NoSelector_PassesThrough()
    {
        const string inputHtml = "<html><body>Unchanged</body></html>";

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("filter-1", "Filter", new { });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("filter-1")
            .WithInput("html", (object)inputHtml)
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output!.Value.GetString().ShouldBe(inputHtml);

        _extractor.DidNotReceive().ExtractHtml(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>());
    }

    [Test]
    public async Task ExecuteAsync_SelectorMatchesNothing_ReturnsFailed()
    {
        const string inputHtml = "<html><body>No match here</body></html>";

        _extractor.ExtractHtml(inputHtml, cssSelector: ".nonexistent").Returns((string?)null);

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("filter-1", "Filter", new { css = ".nonexistent" });
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("filter-1")
            .WithInput("html", (object)inputHtml)
            .WithServices(CreateServices())
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Success.ShouldBeFalse();
        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error!.ShouldContain("Selector matched no content");
    }

    [Test]
    public async Task BlockType_ReturnsFilter()
    {
        _sut.BlockType.ShouldBe("Filter");
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
        _sut.OutputPorts[0].Name.ShouldBe("html");
        _sut.OutputPorts[0].Type.ShouldBe(PortType.HtmlContent);
        await Task.CompletedTask;
    }
}
