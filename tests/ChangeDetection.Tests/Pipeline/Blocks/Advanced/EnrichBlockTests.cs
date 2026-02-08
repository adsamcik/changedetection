using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Advanced;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Blocks.Advanced;

[Category("Unit")]
public class EnrichBlockTests : TestBase
{
    private readonly EnrichBlock _sut = new();

    [Test]
    public async Task ExecuteAsync_FetchesAndEnriches()
    {
        var pipeline = CreatePipeline("enrich-1", new
        {
            urlField = "detailUrl",
            extractFields = new[] { "description" }
        });

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<div id=\"description\">A great product</div>",
                HttpStatusCode = 200
            });

        var validator = Substitute.For<IUrlValidator>();
        validator.Validate(Arg.Any<string>()).Returns((string?)null);

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IContentFetcher)).Returns(fetcher);
        sp.GetService(typeof(IUrlValidator)).Returns(validator);

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("enrich-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { detailUrl = "https://example.com/item", title = "Widget" }))
            .WithServices(sp)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("title").GetString().ShouldBe("Widget");
        result.Output!.Value.GetProperty("description").GetString().ShouldBe("A great product");
    }

    [Test]
    public async Task ExecuteAsync_FetchFailure_AddsEnrichError()
    {
        var pipeline = CreatePipeline("enrich-1", new
        {
            urlField = "detailUrl",
            extractFields = new[] { "description" }
        });

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = false, HttpStatusCode = 404 });

        var validator = Substitute.For<IUrlValidator>();
        validator.Validate(Arg.Any<string>()).Returns((string?)null);

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IContentFetcher)).Returns(fetcher);
        sp.GetService(typeof(IUrlValidator)).Returns(validator);

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("enrich-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { detailUrl = "https://example.com/missing" }))
            .WithServices(sp)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.TryGetProperty("_enrichError", out _).ShouldBeTrue();
        result.Output!.Value.GetProperty("detailUrl").GetString().ShouldBe("https://example.com/missing");
    }

    [Test]
    public async Task ExecuteAsync_MissingUrlFieldConfig_ReturnsFailed()
    {
        var pipeline = CreatePipeline("enrich-1", new { extractFields = new[] { "desc" } });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("enrich-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { url = "https://example.com" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("urlField");
    }

    [Test]
    public async Task ExecuteAsync_MissingUrlInData_ReturnsFailed()
    {
        var pipeline = CreatePipeline("enrich-1", new { urlField = "detailUrl" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("enrich-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { title = "no url here" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("detailUrl");
    }

    [Test]
    public async Task ExecuteAsync_MissingDataInput_ReturnsFailed()
    {
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("enrich-1")
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("data");
    }

    [Test]
    public async Task BlockType_ReturnsEnrich()
    {
        _sut.BlockType.ShouldBe("Enrich");
        _sut.CriticalityTier.ShouldBe(BlockCriticalityTier.Extraction);
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
                Type = "Enrich",
                Config = JsonSerializer.SerializeToElement(config)
            }
        ],
        Connections = []
    };
}
