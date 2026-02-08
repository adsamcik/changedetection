using System.Text.Json;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Acquisition;

[Category("Unit")]
public class PaginateBlockTests : TestBase
{
    private readonly PaginateBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_NoPage_ReturnsSinglePage()
    {
        var pipeline = CreatePipeline("paginate-1", new { nextSelector = "a.next", maxPages = 3 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement("<html><body>Page 1</body></html>"))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("pageCount").GetInt32().ShouldBe(1);
        result.Output!.Value.GetProperty("pages").GetArrayLength().ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_NoSelector_ReturnsSinglePage()
    {
        var pipeline = CreatePipeline("paginate-1", new { maxPages = 3 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement("<html>content</html>"))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("pageCount").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_MissingHtmlInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("html");
    }

    [Test]
    public async Task ExecuteAsync_EmptyHtml_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement(""))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("empty");
    }

    [Test]
    public async Task BlockType_ReturnsPaginate()
    {
        _sut.BlockType.ShouldBe("Paginate");
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Infrastructure);
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
                Type = "Paginate",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
