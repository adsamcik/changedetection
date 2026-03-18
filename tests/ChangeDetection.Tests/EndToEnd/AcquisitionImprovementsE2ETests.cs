using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Content;
using ChangeDetection.Services.SetupPipeline;
using ChangeDetection.Services.Scraping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("EndToEnd")]
public class AcquisitionImprovementsE2ETests : TestBase
{
    [Test]
    public async Task FullPipeline_LightweightHttpFetch_AndStructuredDataExtraction_PrefersJsonLd()
    {
        const string url = "https://example.com/products/structured-widget";
        const string html = """
            <html>
              <head>
                <script type="application/ld+json">
                {
                  "@context": "https://schema.org",
                  "@type": "Product",
                  "name": "Structured Widget",
                  "offers": {
                    "@type": "Offer",
                    "price": "42.00"
                  }
                }
                </script>
              </head>
              <body>
                <h1>CSS Widget</h1>
                <div class="price">$29.99</div>
              </body>
            </html>
            """;

        var fetcher = Substitute.For<IContentFetcher>();
        var capturedOptions = new List<FetchOptions>();
        fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedOptions.Add(call.ArgAt<FetchOptions>(1));
                return Task.FromResult(new FetchResult
                {
                    IsSuccess = true,
                    Html = html,
                    HttpStatusCode = 200,
                    DurationMs = 5
                });
            });

        using var services = CreatePipelineServiceProvider(fetcher);
        var executor = CreateExecutor(services);
        var pipeline = CreateStructuredDataPipeline(url, new { useLightweight = true, timeout = 10000 });
        var stateStore = new InMemoryBlockStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeTrue(result.Error);
        result.OutputData.ShouldNotBeNull();
        result.OutputData!.Value.GetProperty("title").GetString().ShouldBe("Structured Widget");
        result.OutputData!.Value.GetProperty("price").GetString().ShouldBe("42.00");
        result.OutputData!.Value.GetProperty("title_source").GetString().ShouldBe("json-ld");
        result.OutputData!.Value.GetProperty("price_source").GetString().ShouldBe("json-ld");

        capturedOptions.Count.ShouldBe(1);
        capturedOptions[0].Mode.ShouldBe(FetchMode.LightweightHttp);
        capturedOptions[0].EffectiveMode.ShouldBe(FetchMode.LightweightHttp);

        await fetcher.Received(1).FetchAsync(
            url,
            Arg.Is<FetchOptions>(options => options.Mode == FetchMode.LightweightHttp),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VolatilityFilter_PreventsFalseHashCompareChanges_WhenOnlyTimestampAndTokensMove()
    {
        const string url = "https://example.com/pricing/widget";
        const string baselineHtml = """
            <html><body>
              <h1>Widget</h1>
              <div class="price">$29.99</div>
              <div class="timestamp">2026-03-18T09:30:00Z</div>
              <div class="csrf-token">csrf_token=ABC123</div>
              <div class="session-id">session=SESS-001</div>
            </body></html>
            """;
        const string updatedHtml = """
            <html><body>
              <h1>Widget</h1>
              <div class="price">$29.99</div>
              <div class="timestamp">2026-03-18T10:45:00Z</div>
              <div class="csrf-token">csrf_token=XYZ999</div>
              <div class="session-id">session=SESS-777</div>
            </body></html>
            """;

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new FetchResult { IsSuccess = true, Html = baselineHtml, HttpStatusCode = 200, DurationMs = 1 }),
                Task.FromResult(new FetchResult { IsSuccess = true, Html = updatedHtml, HttpStatusCode = 200, DurationMs = 1 }));

        using var services = CreatePipelineServiceProvider(fetcher);
        var executor = CreateExecutor(services);
        var pipeline = CreateVolatilityAwareComparisonPipeline(url);
        var stateStore = new InMemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        var firstRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);
        var secondRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        firstRun.Success.ShouldBeTrue(firstRun.Error);
        secondRun.Success.ShouldBeTrue(secondRun.Error);

        var firstCompare = GetBlockOutput(firstRun, "hash-1");
        var secondCompare = GetBlockOutput(secondRun, "hash-1");
        var secondFiltered = GetBlockOutput(secondRun, "volatility-1");

        firstRun.BlockResults["hash-1"].Status.ShouldBe(BlockExecutionStatus.Baseline);
        firstCompare.GetProperty("changed").GetBoolean().ShouldBeFalse();
        secondCompare.GetProperty("changed").GetBoolean().ShouldBeFalse();
        secondFiltered.GetProperty("price").GetString().ShouldBe("$29.99");
        secondFiltered.GetProperty("lastUpdated").GetString().ShouldBe(string.Empty);
        secondFiltered.GetProperty("csrfToken").GetString().ShouldBe(string.Empty);
        secondFiltered.GetProperty("sessionId").GetString().ShouldBe(string.Empty);
    }

    [Test]
    public async Task VolatilityFilter_AllowsRealChangesThrough_WhenStableFieldChanges()
    {
        const string url = "https://example.com/pricing/widget";
        const string baselineHtml = """
            <html><body>
              <h1>Widget</h1>
              <div class="price">$29.99</div>
              <div class="timestamp">2026-03-18T09:30:00Z</div>
              <div class="csrf-token">csrf_token=ABC123</div>
              <div class="session-id">session=SESS-001</div>
            </body></html>
            """;
        const string changedHtml = """
            <html><body>
              <h1>Widget</h1>
              <div class="price">$31.99</div>
              <div class="timestamp">2026-03-18T10:45:00Z</div>
              <div class="csrf-token">csrf_token=XYZ999</div>
              <div class="session-id">session=SESS-777</div>
            </body></html>
            """;

        var fetcher = Substitute.For<IContentFetcher>();
        fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(new FetchResult { IsSuccess = true, Html = baselineHtml, HttpStatusCode = 200, DurationMs = 1 }),
                Task.FromResult(new FetchResult { IsSuccess = true, Html = changedHtml, HttpStatusCode = 200, DurationMs = 1 }));

        using var services = CreatePipelineServiceProvider(fetcher);
        var executor = CreateExecutor(services);
        var pipeline = CreateVolatilityAwareComparisonPipeline(url);
        var stateStore = new InMemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        var firstRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);
        var secondRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        firstRun.Success.ShouldBeTrue(firstRun.Error);
        secondRun.Success.ShouldBeTrue(secondRun.Error);

        var secondCompare = GetBlockOutput(secondRun, "hash-1");
        var secondFiltered = GetBlockOutput(secondRun, "volatility-1");

        secondCompare.GetProperty("changed").GetBoolean().ShouldBeTrue();
        secondCompare.GetProperty("previousHash").GetString().ShouldNotBeNullOrWhiteSpace();
        secondFiltered.GetProperty("price").GetString().ShouldBe("$31.99");
        secondFiltered.GetProperty("lastUpdated").GetString().ShouldBe(string.Empty);
    }

    [Test]
    public async Task PlatformDetection_ForKnownWorkdayUrl_UsesTemplateAndSkipsPipelineAssemblyLlm()
    {
        const string url = "https://acme.myworkdayjobs.com/en-US/Careers";

        var detected = new PlatformDetector().DetectFromUrl(url);
        detected.ShouldNotBeNull();
        detected.PlatformId.ShouldBe("workday");

        var template = new PipelineTemplateRegistry().GetTemplate(detected.PlatformId, "Track new job openings");
        template.ShouldNotBeNull();
        template.Pipeline.Blocks.ShouldContain(block => block.Type == "LlmExtract");

        var harness = CreateSetupPipelineHarness();
        var intentJson = """
            {
                "url": "https://acme.myworkdayjobs.com/en-US/Careers",
                "intent": "Track new job openings",
                "changeType": "jobs",
                "summary": "I'll watch the Workday careers page for new jobs"
            }
            """;
        var qcJson = """
            {
                "valid": true,
                "issues": [],
                "suggestions": []
            }
            """;

        harness.LlmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResponse(intentJson), SuccessResponse(qcJson));

        harness.ContentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><head><script>window.__NEXT_DATA__={};</script></head><body>Careers</body></html>",
                HttpStatusCode = 200,
                DurationMs = 50
            });

        harness.PipelineExecutor.ExecuteAsync(
                Arg.Any<PipelineDefinition>(),
                Arg.Any<Guid>(),
                Arg.Any<IBlockStateStore>(),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(new PipelineExecutionResult
            {
                Success = true,
                BlockResults = new Dictionary<string, BlockResult>(),
                OutputData = JsonDocument.Parse("""[{"title":"Engineer"}]""").RootElement.Clone(),
                ExecutionDurationMs = 25,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        string? sessionId = null;
        await foreach (var progress in harness.Pipeline.StartSetupAsync(new SetupRequest
        {
            UserInput = $"Track new jobs at {url}"
        }))
        {
            if (progress.Phase == SetupPhase.Checkpoint1 && progress.Detail is not null)
                sessionId = progress.Detail.Replace("Session: ", "", StringComparison.Ordinal);
        }

        sessionId.ShouldNotBeNull();

        var confirmProgress = new List<SetupProgress>();
        await foreach (var progress in harness.Pipeline.ConfirmIntentAsync(sessionId!, confirmed: true))
            confirmProgress.Add(progress);

        var checkpoint2 = confirmProgress.Last();
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.Pipeline.Metadata!.DisplayTitle.ShouldContain("Workday");
        checkpoint2.Proposal.Pipeline.Blocks.ShouldContain(block => block.Type == "LlmExtract");

        await harness.LlmChain.Received(2).ExecuteAsync(
            Arg.Any<string>(),
            Arg.Any<LlmRequestOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Navigate_AutoMode_FallsBackToPlaywright_WhenHttpOnlyReturnsJsShell()
    {
        const string renderedHtml = """
            <article class="product">
              <h1>Rendered Product</h1>
              <div class="price">$77.00</div>
            </article>
            """;
        var shellHtml = $$"""
            <html>
              <body>
                <div id="root"></div>
                <script>
                  document.getElementById('root').innerHTML = {{JsonSerializer.Serialize(renderedHtml)}};
                </script>
              </body>
            </html>
            """;

        await using var server = new LoopbackHtmlServer(shellHtml);
        await using var services = CreatePipelineServiceProviderWithPlaywright();
        var executor = CreateExecutor(services);
        var pipeline = CreateStructuredDataPipeline(server.Url, new { timeout = 10000 });
        var stateStore = new InMemoryBlockStateStore();

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, page: null);

        result.Success.ShouldBeTrue(result.Error);
        result.OutputData.ShouldNotBeNull();
        result.OutputData!.Value.GetProperty("title").GetString().ShouldBe("Rendered Product");
        result.OutputData!.Value.GetProperty("price").GetString().ShouldBe("$77.00");
        result.OutputData!.Value.GetProperty("title_source").GetString().ShouldBe("css");
        result.OutputData!.Value.GetProperty("price_source").GetString().ShouldBe("css");
    }

    private PipelineExecutor CreateExecutor(IServiceProvider services)
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        return new PipelineExecutor(
            registry,
            new PipelineValidator(CreateLogger<PipelineValidator>()),
            services,
            CreateLogger<PipelineExecutor>());
    }

    private ServiceProvider CreatePipelineServiceProvider(IContentFetcher fetcher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IContentFetcher>(fetcher);
        services.AddSingleton<IContentExtractor, ContentExtractor>();
        services.AddSingleton<IStructuredDataExtractor, StructuredDataExtractor>();
        services.AddSingleton<IUrlValidator, AllowAllUrlValidator>();
        return services.BuildServiceProvider();
    }

    private ServiceProvider CreatePipelineServiceProviderWithPlaywright()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IContentExtractor, ContentExtractor>();
        services.AddSingleton<IStructuredDataExtractor, StructuredDataExtractor>();
        services.AddSingleton<IUrlValidator, AllowAllUrlValidator>();
        services.AddSingleton<IContentFetcher>(sp =>
            new PlaywrightFetcher(
                CreateLogger<PlaywrightFetcher>(),
                sp.GetRequiredService<IHttpClientFactory>()));
        return services.BuildServiceProvider();
    }

    private static PipelineDefinition CreateStructuredDataPipeline(string url, object navigateConfig) => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            Block("input-1", "Input", 0, new { url }),
            Block("navigate-1", "Navigate", 1, navigateConfig),
            Block("extract-1", "ExtractSchema", 2, new
            {
                preferStructuredData = true,
                enableLlmFallback = false,
                schema = new object[]
                {
                    new { field = "title", selector = "h1" },
                    new { field = "price", selector = ".price" }
                }
            }),
            Block("output-1", "Output", 3)
        ],
        Connections =
        [
            Connect("input-1", "url", "navigate-1", "url"),
            Connect("navigate-1", "html", "extract-1", "html"),
            Connect("extract-1", "data", "output-1", "data")
        ]
    };

    private static PipelineDefinition CreateVolatilityAwareComparisonPipeline(string url) => new()
    {
        SchemaVersion = 1,
        Blocks =
        [
            Block("input-1", "Input", 0, new { url }),
            Block("navigate-1", "Navigate", 1, new { useLightweight = true, timeout = 10000 }),
            Block("extract-1", "ExtractSchema", 2, new
            {
                preferStructuredData = false,
                enableLlmFallback = false,
                schema = new object[]
                {
                    new { field = "title", selector = "h1" },
                    new { field = "price", selector = ".price" },
                    new { field = "lastUpdated", selector = ".timestamp" },
                    new { field = "csrfToken", selector = ".csrf-token" },
                    new { field = "sessionId", selector = ".session-id" }
                }
            }),
            Block("volatility-1", "VolatilityFilter", 3, new
            {
                stripTimestamps = true,
                replacement = string.Empty,
                stripPatterns = new object[]
                {
                    new { name = "csrf", pattern = "csrf_token=[A-Za-z0-9\\-]+" },
                    new { name = "session", pattern = "session=[A-Za-z0-9\\-]+" }
                }
            }),
            Block("hash-1", "HashCompare", 4),
            Block("output-1", "Output", 5)
        ],
        Connections =
        [
            Connect("input-1", "url", "navigate-1", "url"),
            Connect("navigate-1", "html", "extract-1", "html"),
            Connect("extract-1", "data", "volatility-1", "data"),
            Connect("volatility-1", "data", "hash-1", "data"),
            Connect("volatility-1", "data", "output-1", "data")
        ]
    };

    private static BlockDefinition Block(string id, string type, int position, object? config = null) => new()
    {
        Id = id,
        Type = type,
        Position = position,
        Config = config is null ? null : JsonSerializer.SerializeToElement(config)
    };

    private static ConnectionDefinition Connect(string fromBlockId, string fromPort, string toBlockId, string toPort) => new()
    {
        FromBlockId = fromBlockId,
        FromPort = fromPort,
        ToBlockId = toBlockId,
        ToPort = toPort
    };

    private static JsonElement GetBlockOutput(PipelineExecutionResult result, string blockId)
    {
        result.BlockResults.ShouldContainKey(blockId);
        result.BlockResults[blockId].Output.ShouldNotBeNull();
        return result.BlockResults[blockId].Output!.Value;
    }

    private SetupPipelineHarness CreateSetupPipelineHarness()
    {
        var llmChain = Substitute.For<ILlmProviderChain>();
        var contentFetcher = Substitute.For<IContentFetcher>();
        var pipelineExecutor = Substitute.For<IPipelineExecutor>();
        var pipelineValidator = Substitute.For<IPipelineValidator>();
        var blockRegistry = Substitute.For<IBlockRegistry>();
        var watchRepo = Substitute.For<IRepository<WatchedSite>>();
        var platformDetector = new PlatformDetector();
        var templateRegistry = new PipelineTemplateRegistry();

        blockRegistry.IsRegistered(Arg.Any<string>()).Returns(true);
        blockRegistry.RegisteredBlockTypes.Returns(new List<string>
        {
            "Input", "Output", "Navigate", "Filter", "ExtractSchema",
            "HashCompare", "ListDiff", "Condition", "Notify", "LlmExtract"
        });

        blockRegistry.GetOutputPorts("Input").Returns(new List<PortDescriptor>
        {
            new() { Name = "url", Type = PortType.Url },
            new() { Name = "config", Type = PortType.Configuration }
        });
        blockRegistry.GetInputPorts("Navigate").Returns(new List<PortDescriptor>
        {
            new() { Name = "url", Type = PortType.Url }
        });
        blockRegistry.GetOutputPorts("Navigate").Returns(new List<PortDescriptor>
        {
            new() { Name = "page", Type = PortType.PageReference },
            new() { Name = "html", Type = PortType.HtmlContent }
        });
        blockRegistry.GetInputPorts("LlmExtract").Returns(new List<PortDescriptor>
        {
            new() { Name = "html", Type = PortType.HtmlContent }
        });
        blockRegistry.GetOutputPorts("LlmExtract").Returns(new List<PortDescriptor>
        {
            new() { Name = "data", Type = PortType.ExtractedObjects }
        });
        blockRegistry.GetInputPorts("Output").Returns(new List<PortDescriptor>
        {
            new() { Name = "data", Type = PortType.ExtractedObjects }
        });
        blockRegistry.GetOutputPorts("Output").Returns(new List<PortDescriptor>());

        pipelineValidator.Validate(Arg.Any<PipelineDefinition>(), Arg.Any<IBlockRegistry>())
            .Returns(ChangeDetection.Core.Pipeline.Validation.ValidationResult.Valid());

        var pipeline = new ComposableSetupPipeline(
            llmChain,
            contentFetcher,
            pipelineExecutor,
            pipelineValidator,
            blockRegistry,
            platformDetector,
            templateRegistry,
            watchRepo,
            CreateLogger<ComposableSetupPipeline>());

        return new SetupPipelineHarness(
            pipeline,
            llmChain,
            contentFetcher,
            pipelineExecutor);
    }

    private static LlmResponse SuccessResponse(string content) => new()
    {
        IsSuccess = true,
        Content = content,
        ProviderUsed = "test",
        Model = "test-model",
        DurationMs = 25
    };

    private sealed record SetupPipelineHarness(
        ComposableSetupPipeline Pipeline,
        ILlmProviderChain LlmChain,
        IContentFetcher ContentFetcher,
        IPipelineExecutor PipelineExecutor);

    private sealed class AllowAllUrlValidator : IUrlValidator
    {
        public string? Validate(string url) => null;
    }

    private sealed class InMemoryBlockStateStore : IBlockStateStore
    {
        private readonly ConcurrentDictionary<string, JsonElement> _outputs = new();
        private readonly ConcurrentDictionary<string, JsonElement> _cached = new();

        public Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default)
        {
            var key = $"{watchId}:{blockInstanceId}";
            return Task.FromResult(_outputs.TryGetValue(key, out var value) ? value : (JsonElement?)null);
        }

        public Task<JsonElement?> GetCachedOutputAsync(
            string watchId,
            string blockInstanceId,
            string inputHash,
            string pipelineHash,
            CancellationToken ct = default)
        {
            var key = $"{watchId}:{blockInstanceId}:{inputHash}:{pipelineHash}";
            return Task.FromResult(_cached.TryGetValue(key, out var value) ? value : (JsonElement?)null);
        }

        public Task SaveOutputAsync(
            string watchId,
            string blockInstanceId,
            JsonElement output,
            string? inputHash = null,
            string? pipelineHash = null,
            CancellationToken ct = default)
        {
            var blockKey = $"{watchId}:{blockInstanceId}";
            _outputs[blockKey] = output.Clone();

            if (!string.IsNullOrWhiteSpace(inputHash) && !string.IsNullOrWhiteSpace(pipelineHash))
            {
                var cacheKey = $"{watchId}:{blockInstanceId}:{inputHash}:{pipelineHash}";
                _cached[cacheKey] = output.Clone();
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(
            string watchId,
            string blockInstanceId,
            int maxResults = 10,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<BlockExecutionSnapshot>>([]);
    }

    private sealed class LoopbackHtmlServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _html;
        private readonly Task _acceptLoop;

        public LoopbackHtmlServer(string html)
        {
            _html = html;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/";
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }

        public string Url { get; }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();

            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using var _ = client;
            using var stream = client.GetStream();

            var buffer = new byte[4096];
            while (!ct.IsCancellationRequested && client.Available > 0)
            {
                await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, client.Available)), ct);
            }

            var bodyBytes = Encoding.UTF8.GetBytes(_html);
            var response = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(response);

            await stream.WriteAsync(headerBytes, ct);
            await stream.WriteAsync(bodyBytes, ct);
            await stream.FlushAsync(ct);
        }
    }
}

