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
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("enrich-1", "Enrich", new
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
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("enrich-1", "Enrich", new
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
    public async Task ExecuteAsync_ArrayInput_FetchesInParallelRespectingMaxConcurrency()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("enrich-1", "Enrich", new
        {
            urlField = "detailUrl",
            extractFields = new[] { "description" },
            maxItems = 3,
            maxConcurrency = 3,
            delayBetweenRequests = 0
        });

        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var url = callInfo.Arg<string>();
                var concurrency = Interlocked.Increment(ref currentConcurrency);
                UpdateMaxConcurrency(ref maxObservedConcurrency, concurrency);

                await Task.Delay(150, callInfo.Arg<CancellationToken>());

                Interlocked.Decrement(ref currentConcurrency);
                return new FetchResult
                {
                    IsSuccess = true,
                    Html = $"<div id=\"description\">{url}</div>",
                    HttpStatusCode = 200
                };
            });

        var validator = Substitute.For<IUrlValidator>();
        validator.Validate(Arg.Any<string>()).Returns((string?)null);

        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IContentFetcher)).Returns(fetcher);
        sp.GetService(typeof(IUrlValidator)).Returns(validator);

        var logger = CreateLogger<EnrichBlock>();
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("enrich-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new[]
            {
                new { detailUrl = "https://example.com/1", title = "One" },
                new { detailUrl = "https://example.com/2", title = "Two" },
                new { detailUrl = "https://example.com/3", title = "Three" }
            }))
            .WithServices(sp)
            .WithLogger(logger)
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        maxObservedConcurrency.ShouldBeGreaterThan(1);

        var output = result.Output!.Value;
        output.GetArrayLength().ShouldBe(3);
        output[0].GetProperty("description").GetString().ShouldBe("https://example.com/1");
        output[1].GetProperty("description").GetString().ShouldBe("https://example.com/2");
        output[2].GetProperty("description").GetString().ShouldBe("https://example.com/3");

        LogCollector.GetSnapshot().Any(x => x.Message.Contains("items/sec", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_MissingUrlFieldConfig_ReturnsFailed()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("enrich-1", "Enrich", new { extractFields = new[] { "desc" } });

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
    public async Task ExecuteAsync_MissingUrlInData_AddsEnrichError()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("enrich-1", "Enrich", new { urlField = "detailUrl" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("enrich-1")
            .WithInput("data", JsonSerializer.SerializeToElement(new { title = "no url here" }))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.TryGetProperty("_enrichError", out _).ShouldBeTrue();
        result.Output!.Value.GetProperty("_enrichError").GetString().ShouldContain("detailUrl");
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

    private static void UpdateMaxConcurrency(ref int maxObservedConcurrency, int currentConcurrency)
    {
        while (true)
        {
            var snapshot = maxObservedConcurrency;
            if (currentConcurrency <= snapshot)
                return;

            if (Interlocked.CompareExchange(ref maxObservedConcurrency, currentConcurrency, snapshot) == snapshot)
                return;
        }
    }

}
