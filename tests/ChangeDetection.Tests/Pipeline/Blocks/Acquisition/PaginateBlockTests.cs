using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using NSubstitute;
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
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new { nextSelector = "a.next", maxPages = 3 });

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
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new { maxPages = 3 });

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
    public async Task ExecuteAsync_SequentialMode_FollowsNextLinksFromFetchedHtml()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new
        {
            nextSelector = "a.next",
            maxPages = 5,
            delay = 0
        });

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync("https://example.com/page2", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = """
                       <html><body>
                           <div>Page 2</div>
                           <a class="next" href="/page3">Next</a>
                       </body></html>
                       """,
                HttpStatusCode = 200
            });
        fetcher.FetchAsync("https://example.com/page3", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><div>Page 3</div></body></html>",
                HttpStatusCode = 200
            });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement(new
            {
                html = """
                       <html><body>
                           <div>Page 1</div>
                           <a class="next" href="/page2">Next</a>
                       </body></html>
                       """,
                url = "https://example.com/page1"
            }))
            .WithPipelineDefinition(pipeline)
            .WithServices(CreateServices(fetcher))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var pages = result.Output!.Value.GetProperty("pages");
        pages.GetArrayLength().ShouldBe(3);
        pages[0].GetString().ShouldContain("Page 1");
        pages[1].GetString().ShouldContain("Page 2");
        pages[2].GetString().ShouldContain("Page 3");

        await fetcher.Received(1).FetchAsync("https://example.com/page2", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
        await fetcher.Received(1).FetchAsync("https://example.com/page3", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_SequentialMode_ResolvesRelativeUrlsAgainstCurrentPage()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new
        {
            nextSelector = "a.next",
            maxPages = 2,
            delay = 0
        });

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body>Page 2</body></html>", HttpStatusCode = 200 });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement(new
            {
                html = "<html><body><a class='next' href='../page2?sort=asc'>Next</a></body></html>",
                url = "https://example.com/catalog/page1"
            }))
            .WithPipelineDefinition(pipeline)
            .WithServices(CreateServices(fetcher))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        await fetcher.Received(1).FetchAsync("https://example.com/page2?sort=asc", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_SequentialMode_BreaksWhenNextUrlRepeats()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new
        {
            nextSelector = "a.next",
            maxPages = 5,
            delay = 0
        });

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync("https://example.com/page2", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><div>Page 2</div><a class='next' href='https://example.com/page2'>Loop</a></body></html>",
                HttpStatusCode = 200
            });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement(new
            {
                html = "<html><body><div>Page 1</div><a class='next' href='https://example.com/page2'>Next</a></body></html>",
                url = "https://example.com/page1"
            }))
            .WithPipelineDefinition(pipeline)
            .WithServices(CreateServices(fetcher))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        result.Output!.Value.GetProperty("pageCount").GetInt32().ShouldBe(2);
        await fetcher.Received(1).FetchAsync("https://example.com/page2", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ParallelMode_RespectsMaxConcurrency()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new
        {
            mode = "parallel",
            urlPattern = "https://example.com/items?page={page}",
            startPage = 1,
            maxPages = 4,
            maxConcurrency = 2,
            delay = 0
        });

        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var concurrency = Interlocked.Increment(ref currentConcurrency);
                UpdateMaxConcurrency(ref maxObservedConcurrency, concurrency);

                await Task.Delay(150, callInfo.Arg<CancellationToken>());
                Interlocked.Decrement(ref currentConcurrency);

                return new FetchResult
                {
                    IsSuccess = true,
                    Html = $"<html><body>{callInfo.Arg<string>()}</body></html>",
                    HttpStatusCode = 200
                };
            });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement("<html>seed</html>"))
            .WithPipelineDefinition(pipeline)
            .WithServices(CreateServices(fetcher))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        maxObservedConcurrency.ShouldBeLessThanOrEqualTo(2);
        maxObservedConcurrency.ShouldBeGreaterThan(1);
        result.Output!.Value.GetProperty("pageCount").GetInt32().ShouldBe(4);
    }

    [Test]
    public async Task ExecuteAsync_ParallelMode_PreservesPageOrder()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new
        {
            mode = "parallel",
            urlPattern = "https://example.com/items?page={page}",
            startPage = 1,
            maxPages = 3,
            maxConcurrency = 3,
            delay = 0
        });

        var delays = new Dictionary<string, int>
        {
            ["https://example.com/items?page=1"] = 200,
            ["https://example.com/items?page=2"] = 25,
            ["https://example.com/items?page=3"] = 100
        };

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var url = callInfo.Arg<string>();
                await Task.Delay(delays[url], callInfo.Arg<CancellationToken>());
                return new FetchResult
                {
                    IsSuccess = true,
                    Html = $"<html><body>{url}</body></html>",
                    HttpStatusCode = 200
                };
            });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement("<html>seed</html>"))
            .WithPipelineDefinition(pipeline)
            .WithServices(CreateServices(fetcher))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var pages = result.Output!.Value.GetProperty("pages");
        pages.GetArrayLength().ShouldBe(3);
        pages[0].GetString().ShouldContain("page=1");
        pages[1].GetString().ShouldContain("page=2");
        pages[2].GetString().ShouldContain("page=3");
    }

    [Test]
    public async Task ExecuteAsync_ParallelMode_StopsOnFetchFailure()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new
        {
            mode = "parallel",
            urlPattern = "https://example.com/items?page={page}",
            startPage = 1,
            maxPages = 3,
            maxConcurrency = 1,
            delay = 0
        });

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync("https://example.com/items?page=1", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body>Page 1</body></html>", HttpStatusCode = 200 });
        fetcher.FetchAsync("https://example.com/items?page=2", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = false, HttpStatusCode = 500, ErrorMessage = "Boom" });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement("<html>seed</html>"))
            .WithPipelineDefinition(pipeline)
            .WithServices(CreateServices(fetcher))
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var pages = result.Output!.Value.GetProperty("pages");
        pages.GetArrayLength().ShouldBe(1);
        pages[0].GetString().ShouldContain("Page 1");

        await fetcher.Received(1).FetchAsync("https://example.com/items?page=1", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
        await fetcher.Received(1).FetchAsync("https://example.com/items?page=2", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
        await fetcher.DidNotReceive().FetchAsync("https://example.com/items?page=3", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ParallelMode_WithoutUrlPattern_ReturnsFailed()
    {
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", new
        {
            mode = "parallel",
            maxPages = 3
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", JsonSerializer.SerializeToElement("<html>seed</html>"))
            .WithPipelineDefinition(pipeline)
            .Build();

        var result = await _sut.ExecuteAsync(context);

        result.Status.ShouldBe(BlockExecutionStatus.Failed);
        result.Error.ShouldContain("urlPattern");
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

    private static IServiceProvider CreateServices(IContentFetcher fetcher, IUrlValidator? validator = null)
    {
        validator ??= Substitute.For<IUrlValidator>();
        validator.Validate(Arg.Any<string>()).Returns((string?)null);

        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IContentFetcher)).Returns(fetcher);
        services.GetService(typeof(IUrlValidator)).Returns(validator);
        return services;
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
