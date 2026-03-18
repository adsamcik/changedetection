using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Services.Blocks.Acquisition;
using ChangeDetection.Services.Blocks.Advanced;
using ChangeDetection.Services.Blocks.Comparison;
using ChangeDetection.Services.Blocks.Decision;
using ChangeDetection.Services.Blocks.Output;
using ChangeDetection.Tests.Pipeline.Blocks;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("Integration")]
public class AdvancedBlocksE2ETests : TestBase
{
    [Test]
    public async Task LinkValidateChain_Detects404AndAggregatesDeadLinks()
    {
        var finalOutput = await ExecuteLinkValidationChainAsync(
            new[]
            {
                new { title = "Live role", url = "https://example.com/jobs/a" },
                new { title = "Dead role", url = "https://example.com/jobs/b" }
            },
            CreateClientFactory(request => request.RequestUri!.AbsoluteUri switch
            {
                "https://example.com/jobs/a" => CreateHttpResponse("https://example.com/jobs/a", HttpStatusCode.OK, "<html><body>Live posting</body></html>"),
                "https://example.com/jobs/b" => CreateHttpResponse("https://example.com/jobs/b", HttpStatusCode.NotFound),
                _ => throw new InvalidOperationException($"Unexpected URL {request.RequestUri}")
            }));

        finalOutput.GetProperty("hasDeadLinks").GetBoolean().ShouldBeTrue();
        var items = finalOutput.GetProperty("items");
        items.GetArrayLength().ShouldBe(2);
        items[0].GetProperty("url_valid").GetBoolean().ShouldBeTrue();
        items[0].GetProperty("url_status").GetString().ShouldBe("live");
        items[1].GetProperty("url_valid").GetBoolean().ShouldBeFalse();
        items[1].GetProperty("url_status").GetString().ShouldBe("dead_link");
        items[1].GetProperty("hasDeadLinks").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task LinkValidateChain_DetectsDeathSignalInPageContent()
    {
        var finalOutput = await ExecuteLinkValidationChainAsync(
            new { title = "Scientist", url = "https://example.com/jobs/filled" },
            CreateClientFactory(_ => CreateHttpResponse(
                "https://example.com/jobs/filled",
                HttpStatusCode.OK,
                "<html><body>This position has been filled</body></html>")));

        finalOutput.GetProperty("url_valid").GetBoolean().ShouldBeFalse();
        finalOutput.GetProperty("url_status").GetString().ShouldBe("dead_listing");
        finalOutput.GetProperty("hasDeadLinks").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task LinkValidateChain_DetectsDanishDeathSignal()
    {
        var finalOutput = await ExecuteLinkValidationChainAsync(
            new { title = "Forsker", url = "https://example.com/jobs/danish" },
            CreateClientFactory(_ => CreateHttpResponse(
                "https://example.com/jobs/danish",
                HttpStatusCode.OK,
                "<html><body>Stillingen er besat</body></html>")),
            language: "da");

        finalOutput.GetProperty("url_valid").GetBoolean().ShouldBeFalse();
        finalOutput.GetProperty("url_status").GetString().ShouldBe("dead_listing");
        finalOutput.GetProperty("hasDeadLinks").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task RelevanceScore_FiltersNoisyListAndReportsMetadata()
    {
        var result = await ExecuteRelevanceScoreAsync(
            new[]
            {
                new { title = "Scientist" },
                new { title = "Laboratory Analyst" },
                new { title = "Senior Scientist" },
                new { title = "Scientist Laboratory Associate" },
                new { title = "Senior Laboratory Manager" },
                new { title = "Administrative Assistant" },
                new { title = "Laboratory Scientist Senior" },
                new { title = "Intern" },
                new { title = "Senior Principal" },
                new { title = "Clinical Scientist" }
            },
            new
            {
                targetFields = new[] { "title" },
                positiveKeywords = new Dictionary<string, int>
                {
                    ["scientist"] = 10,
                    ["laboratory"] = 8
                },
                negativeKeywords = new Dictionary<string, int>
                {
                    ["senior"] = -5
                },
                minScore = 5
            });

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var output = result.Output!.Value;
        output.GetProperty("totalScored").GetInt32().ShouldBe(10);
        output.GetProperty("passedFilter").GetInt32().ShouldBe(6);
        output.GetProperty("topScore").GetInt32().ShouldBe(18);

        var items = output.GetProperty("items");
        items.GetArrayLength().ShouldBe(6);
        items[0].GetProperty("relevanceScore").GetInt32().ShouldBe(10);
        items[1].GetProperty("relevanceScore").GetInt32().ShouldBe(8);
        items[2].GetProperty("relevanceScore").GetInt32().ShouldBe(5);
        items[3].GetProperty("relevanceScore").GetInt32().ShouldBe(18);
        items[4].GetProperty("relevanceScore").GetInt32().ShouldBe(13);
        items[5].GetProperty("relevanceScore").GetInt32().ShouldBe(10);
    }

    [Test]
    public async Task RelevanceScore_AllNegativeScoresPreservesActualTopScore()
    {
        var result = await ExecuteRelevanceScoreAsync(
            new[]
            {
                new { title = "Danish required" },
                new { title = "PhD required" },
                new { title = "Senior scientist" }
            },
            new
            {
                targetFields = new[] { "title" },
                positiveKeywords = new Dictionary<string, int>(),
                negativeKeywords = new Dictionary<string, int>
                {
                    ["danish required"] = -20,
                    ["phd required"] = -15,
                    ["senior"] = -5
                },
                minScore = 100
            });

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var output = result.Output!.Value;
        output.GetProperty("topScore").GetInt32().ShouldBe(-5);
        output.GetProperty("passedFilter").GetInt32().ShouldBe(0);
        output.GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task ExtractRelevanceListDiffConditionChain_FindsOnlyNewMatchingItems()
    {
        var stateStore = new MemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        var scoringConfig = new
        {
            targetFields = new[] { "title" },
            positiveKeywords = new Dictionary<string, int>
            {
                ["scientist"] = 10,
                ["laboratory"] = 8
            },
            negativeKeywords = new Dictionary<string, int>
            {
                ["senior"] = -5
            },
            minScore = 5
        };

        var run1Items = CreateRankedJobs(matchingCount: 5, noiseCount: 15);
        var run1Scored = await ExecuteRelevanceScoreAsync(run1Items, scoringConfig);
        run1Scored.Output!.Value.GetProperty("passedFilter").GetInt32().ShouldBe(5);

        var run1Diff = await ExecuteListDiffAsync(
            watchId,
            run1Scored.Output!.Value.GetProperty("items"),
            stateStore,
            identityKey: "url");

        run1Diff.Status.ShouldBe(BlockExecutionStatus.Baseline);
        run1Diff.Output!.Value.GetProperty("items").GetArrayLength().ShouldBe(5);
        run1Diff.Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
        await stateStore.SaveOutputAsync(watchId.ToString(), "diff-1", run1Diff.Output!.Value);

        var run2Items = CreateRankedJobs(matchingCount: 7, noiseCount: 15);
        var run2Scored = await ExecuteRelevanceScoreAsync(run2Items, scoringConfig);
        run2Scored.Output!.Value.GetProperty("totalScored").GetInt32().ShouldBe(22);
        run2Scored.Output!.Value.GetProperty("passedFilter").GetInt32().ShouldBe(7);

        // RelevanceScore wraps filtered matches in a result envelope; ListDiff compares the filtered item list itself.
        var run2Diff = await ExecuteListDiffAsync(
            watchId,
            run2Scored.Output!.Value.GetProperty("items"),
            stateStore,
            identityKey: "url");

        run2Diff.Status.ShouldBe(BlockExecutionStatus.Completed);
        var diffOutput = run2Diff.Output!.Value;
        diffOutput.GetProperty("changed").GetBoolean().ShouldBeTrue();
        diffOutput.GetProperty("added").GetArrayLength().ShouldBe(2);
        diffOutput.GetProperty("modified").GetArrayLength().ShouldBe(0);

        var conditionResult = await ExecuteConditionAsync(
            diffOutput,
            new
            {
                field = "added.length",
                @operator = "greaterThan",
                value = 0
            });

        conditionResult.Status.ShouldBe(BlockExecutionStatus.Completed);
        conditionResult.Output!.Value.GetProperty("signal").GetBoolean().ShouldBeTrue();
        conditionResult.Output!.Value.GetProperty("actualValue").GetString().ShouldBe("2");
    }

    [Test]
    public async Task PaginateSequential_FollowsNextLinksWithoutRepeatingPages()
    {
        var fetcher = CreateFetcher((url, cancellationToken) =>
        {
            var result = url switch
            {
                "https://example.com/page2" => new FetchResult
                {
                    IsSuccess = true,
                    Html = """
                           <html><body>
                               <div>Page 2</div>
                               <a class="next" href="/page3">Next</a>
                           </body></html>
                           """,
                    HttpStatusCode = 200
                },
                "https://example.com/page3" => new FetchResult
                {
                    IsSuccess = true,
                    Html = "<html><body><div>Page 3</div></body></html>",
                    HttpStatusCode = 200
                },
                _ => throw new InvalidOperationException($"Unexpected URL {url}")
            };

            return Task.FromResult(result);
        });

        var result = await ExecutePaginateAsync(
            new
            {
                html = """
                       <html><body>
                           <div>Page 1</div>
                           <a class="next" href="/page2">Next</a>
                       </body></html>
                       """,
                url = "https://example.com/page1"
            },
            new
            {
                nextSelector = "a.next",
                maxPages = 5,
                delay = 0
            },
            fetcher);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        var pages = result.Output!.Value.GetProperty("pages");
        pages.GetArrayLength().ShouldBe(3);
        pages[0].GetString().ShouldContain("Page 1");
        pages[1].GetString().ShouldContain("Page 2");
        pages[2].GetString().ShouldContain("Page 3");
        pages.EnumerateArray().Select(page => page.GetString()).Distinct().Count().ShouldBe(3);
    }

    [Test]
    public async Task PaginateParallel_UrlPatternModePreservesOrderAndRespectsConcurrency()
    {
        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;
        var delays = new Dictionary<string, int>
        {
            ["https://example.com/page/1"] = 125,
            ["https://example.com/page/2"] = 30,
            ["https://example.com/page/3"] = 90,
            ["https://example.com/page/4"] = 10,
            ["https://example.com/page/5"] = 60
        };

        var fetcher = CreateFetcher(async (url, cancellationToken) =>
        {
            var concurrency = Interlocked.Increment(ref currentConcurrency);
            UpdateMaxConcurrency(ref maxObservedConcurrency, concurrency);

            await Task.Delay(delays[url], cancellationToken);
            Interlocked.Decrement(ref currentConcurrency);

            return new FetchResult
            {
                IsSuccess = true,
                Html = $"<html><body>{url}</body></html>",
                HttpStatusCode = 200
            };
        });

        var result = await ExecutePaginateAsync(
            "<html><body>seed</body></html>",
            new
            {
                mode = "parallel",
                urlPattern = "https://example.com/page/{page}",
                startPage = 1,
                maxPages = 5,
                maxConcurrency = 3,
                delay = 0
            },
            fetcher);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        maxObservedConcurrency.ShouldBeLessThanOrEqualTo(3);
        maxObservedConcurrency.ShouldBeGreaterThan(1);

        var pages = result.Output!.Value.GetProperty("pages");
        pages.GetArrayLength().ShouldBe(5);
        for (var i = 0; i < 5; i++)
        {
            pages[i].GetString().ShouldContain($"https://example.com/page/{i + 1}");
        }
    }

    [Test]
    public async Task EnrichParallel_EnrichesAllItemsAndRespectsMaxConcurrency()
    {
        var currentConcurrency = 0;
        var maxObservedConcurrency = 0;

        var fetcher = CreateFetcher(async (url, cancellationToken) =>
        {
            var concurrency = Interlocked.Increment(ref currentConcurrency);
            UpdateMaxConcurrency(ref maxObservedConcurrency, concurrency);

            await Task.Delay(100, cancellationToken);
            Interlocked.Decrement(ref currentConcurrency);

            return new FetchResult
            {
                IsSuccess = true,
                Html = $"<div id=\"description\">Enriched {url}</div>",
                HttpStatusCode = 200
            };
        });

        var items = Enumerable.Range(1, 10)
            .Select(index => new
            {
                title = $"Role {index}",
                detailUrl = $"https://example.com/details/{index}"
            })
            .ToArray();

        var result = await ExecuteEnrichAsync(
            items,
            new
            {
                urlField = "detailUrl",
                extractFields = new[] { "description" },
                maxItems = 10,
                maxConcurrency = 3,
                delayBetweenRequests = 0
            },
            fetcher);

        result.Status.ShouldBe(BlockExecutionStatus.Completed);
        maxObservedConcurrency.ShouldBeLessThanOrEqualTo(3);
        maxObservedConcurrency.ShouldBeGreaterThan(1);

        var output = result.Output!.Value;
        output.GetArrayLength().ShouldBe(10);
        for (var i = 0; i < 10; i++)
        {
            output[i].GetProperty("description").GetString().ShouldBe($"Enriched https://example.com/details/{i + 1}");
        }
    }

    private async Task<JsonElement> ExecuteLinkValidationChainAsync(
        object extractedData,
        IHttpClientFactory httpClientFactory,
        string[]? urlFields = null,
        string language = "en")
    {
        var validated = await ExecuteLinkValidateAsync(extractedData, httpClientFactory, urlFields, language);
        var outputResult = await ExecuteOutputAsync(validated.Output!.Value);
        outputResult.Status.ShouldBe(BlockExecutionStatus.Completed);
        return outputResult.Output!.Value;
    }

    private async Task<BlockResult> ExecuteLinkValidateAsync(
        object data,
        IHttpClientFactory httpClientFactory,
        string[]? urlFields = null,
        string language = "en",
        bool followRedirects = true)
    {
        var block = new LinkValidateBlock();
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("validate-1", "LinkValidate", new
        {
            urlFields = urlFields ?? new[] { "url" },
            language,
            followRedirects
        });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("validate-1")
            .WithInput("data", data)
            .WithServices(CreateServices(
                httpClientFactory: httpClientFactory,
                urlValidator: CreatePermissiveUrlValidator()))
            .WithLogger(CreateLogger<LinkValidateBlock>())
            .WithPipelineDefinition(pipeline)
            .Build();

        return await block.ExecuteAsync(context);
    }

    private async Task<BlockResult> ExecuteOutputAsync(JsonElement data)
    {
        var block = new OutputBlock();
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("output-1", "Output");
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("output-1")
            .WithInput("data", data)
            .WithLogger(CreateLogger<OutputBlock>())
            .WithPipelineDefinition(pipeline)
            .Build();

        return await block.ExecuteAsync(context);
    }

    private async Task<BlockResult> ExecuteRelevanceScoreAsync(object data, object config)
    {
        var block = new RelevanceScoreBlock();
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("score-1", "RelevanceScore", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("score-1")
            .WithInput("data", data)
            .WithLogger(CreateLogger<RelevanceScoreBlock>())
            .WithPipelineDefinition(pipeline)
            .Build();

        return await block.ExecuteAsync(context);
    }

    private async Task<BlockResult> ExecuteListDiffAsync(
        Guid watchId,
        JsonElement data,
        IBlockStateStore stateStore,
        string identityKey = "url")
    {
        var block = new ListDiffBlock();
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("diff-1", "ListDiff", new
        {
            identityKey,
            mode = "all_changes"
        });

        var context = new BlockContextBuilder()
            .WithWatchId(watchId)
            .WithBlockInstanceId("diff-1")
            .WithInput("data", data)
            .WithStateStore(stateStore)
            .WithLogger(CreateLogger<ListDiffBlock>())
            .WithPipelineDefinition(pipeline)
            .Build();

        return await block.ExecuteAsync(context);
    }

    private async Task<BlockResult> ExecuteConditionAsync(JsonElement result, object config)
    {
        var block = new ConditionBlock();
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("condition-1", "Condition", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("condition-1")
            .WithInput("result", result)
            .WithLogger(CreateLogger<ConditionBlock>())
            .WithPipelineDefinition(pipeline)
            .Build();

        return await block.ExecuteAsync(context);
    }

    private async Task<BlockResult> ExecutePaginateAsync(object htmlInput, object config, IContentFetcher fetcher)
    {
        var block = new PaginateBlock();
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("paginate-1", "Paginate", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("paginate-1")
            .WithInput("html", htmlInput)
            .WithServices(CreateServices(
                fetcher: fetcher,
                urlValidator: CreatePermissiveUrlValidator()))
            .WithLogger(CreateLogger<PaginateBlock>())
            .WithPipelineDefinition(pipeline)
            .Build();

        return await block.ExecuteAsync(context);
    }

    private async Task<BlockResult> ExecuteEnrichAsync(object data, object config, IContentFetcher fetcher)
    {
        var block = new EnrichBlock();
        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline("enrich-1", "Enrich", config);
        var context = new BlockContextBuilder()
            .WithBlockInstanceId("enrich-1")
            .WithInput("data", data)
            .WithServices(CreateServices(
                fetcher: fetcher,
                urlValidator: CreatePermissiveUrlValidator()))
            .WithLogger(CreateLogger<EnrichBlock>())
            .WithPipelineDefinition(pipeline)
            .Build();

        return await block.ExecuteAsync(context);
    }

    private static IServiceProvider CreateServices(
        IContentFetcher? fetcher = null,
        IUrlValidator? urlValidator = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        var services = new ServiceCollection();

        if (fetcher is not null)
            services.AddSingleton(fetcher);

        if (urlValidator is not null)
            services.AddSingleton(urlValidator);

        if (httpClientFactory is not null)
            services.AddSingleton(httpClientFactory);

        return services.BuildServiceProvider();
    }

    private static IUrlValidator CreatePermissiveUrlValidator()
    {
        var validator = Substitute.For<IUrlValidator>();
        validator.Validate(Arg.Any<string>()).Returns((string?)null);
        return validator;
    }

    private static IHttpClientFactory CreateClientFactory(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        var client = new HttpClient(new StubHttpMessageHandler(responder));
        factory.CreateClient(Arg.Any<string>()).Returns(client);
        return factory;
    }

    private static HttpResponseMessage CreateHttpResponse(string url, HttpStatusCode statusCode, string body = "")
    {
        var response = new HttpResponseMessage(statusCode)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, url)
        };

        if (!string.IsNullOrEmpty(body))
            response.Content = new StringContent(body);

        return response;
    }

    private static IContentFetcher CreateFetcher(Func<string, CancellationToken, Task<FetchResult>> responder)
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => responder(callInfo.Arg<string>(), callInfo.Arg<CancellationToken>()));
        return fetcher;
    }

    private static object[] CreateRankedJobs(int matchingCount, int noiseCount)
    {
        var jobs = new List<object>();

        for (var index = 1; index <= matchingCount; index++)
        {
            jobs.Add(new
            {
                title = index % 2 == 0 ? $"Laboratory Scientist {index}" : $"Scientist {index}",
                url = $"https://example.com/jobs/match-{index}"
            });
        }

        for (var index = 1; index <= noiseCount; index++)
        {
            jobs.Add(new
            {
                title = $"Senior Manager {index}",
                url = $"https://example.com/jobs/noise-{index}"
            });
        }

        return jobs.ToArray();
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    private sealed class MemoryBlockStateStore : IBlockStateStore
    {
        private readonly ConcurrentDictionary<(string WatchId, string BlockId), BlockExecutionSnapshot> _latest = new();
        private readonly ConcurrentDictionary<(string WatchId, string BlockId, string InputHash, string PipelineHash), JsonElement> _cache = new();

        public Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default)
        {
            return Task.FromResult(
                _latest.TryGetValue((watchId, blockInstanceId), out var snapshot)
                    ? (JsonElement?)snapshot.Output.Clone()
                    : null);
        }

        public Task<JsonElement?> GetCachedOutputAsync(
            string watchId,
            string blockInstanceId,
            string inputHash,
            string pipelineHash,
            CancellationToken ct = default)
        {
            return Task.FromResult(
                _cache.TryGetValue((watchId, blockInstanceId, inputHash, pipelineHash), out var output)
                    ? (JsonElement?)output.Clone()
                    : null);
        }

        public Task SaveOutputAsync(
            string watchId,
            string blockInstanceId,
            JsonElement output,
            string? inputHash = null,
            string? pipelineHash = null,
            CancellationToken ct = default)
        {
            var snapshot = new BlockExecutionSnapshot
            {
                WatchId = watchId,
                BlockInstanceId = blockInstanceId,
                Timestamp = DateTime.UtcNow,
                Output = output.Clone()
            };

            _latest[(watchId, blockInstanceId)] = snapshot;

            if (!string.IsNullOrWhiteSpace(inputHash) && !string.IsNullOrWhiteSpace(pipelineHash))
                _cache[(watchId, blockInstanceId, inputHash, pipelineHash)] = output.Clone();

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(
            string watchId,
            string blockInstanceId,
            int maxResults = 10,
            CancellationToken ct = default)
        {
            if (_latest.TryGetValue((watchId, blockInstanceId), out var snapshot))
                return Task.FromResult<IReadOnlyList<BlockExecutionSnapshot>>(new[] { snapshot });

            return Task.FromResult<IReadOnlyList<BlockExecutionSnapshot>>(Array.Empty<BlockExecutionSnapshot>());
        }
    }
}
