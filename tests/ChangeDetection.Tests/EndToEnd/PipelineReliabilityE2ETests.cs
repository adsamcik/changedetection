using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Blocks.Acquisition;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.SetupPipeline;
using ChangeDetection.Tests.Pipeline.Blocks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.EndToEnd;

[Category("EndToEnd")]
public class PipelineReliabilityE2ETests : TestBase
{
    private const string WatchUrl = "https://example.com/watch";

    [Test]
    public async Task ExtractionDegradation_WhenExtractionDropsToZero_MarksRunDegradedAndPreservesPreviousGoodState()
    {
        const string healthyHtml = "<html>healthy</html>";
        const string brokenHtml = "<html>broken</html>";

        var currentHtml = healthyHtml;
        var fetcher = CreateFetcher(() => currentHtml);
        var extractor = CreateExtractor(new Dictionary<string, Dictionary<string, string?>>
        {
            [healthyHtml] = new(StringComparer.Ordinal)
            {
                [".title"] = "Widget Alpha",
                [".price"] = "$29.99",
                [".status"] = "In stock"
            },
            [brokenHtml] = new(StringComparer.Ordinal)
            {
                [".title"] = null,
                [".price"] = null,
                [".status"] = null
            }
        });

        var services = CreatePipelineServices(fetcher, extractor);
        var registry = CreateReliabilityRegistry();
        var pipeline = BuildReliabilityPipeline(CreateExtractSchemaConfig());
        var executor = CreateExecutor(registry, services);
        var stateStore = new InMemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        var baselineRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);
        baselineRun.Success.ShouldBeTrue();
        baselineRun.IsDegraded.ShouldBeFalse();

        baselineRun.BlockResults["extract-1"].Output.ShouldNotBeNull();
        var baselineExtract = baselineRun.BlockResults["extract-1"].Output!.Value.Clone();

        currentHtml = brokenHtml;
        var degradedRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        degradedRun.Success.ShouldBeTrue();
        degradedRun.IsDegraded.ShouldBeTrue();
        degradedRun.BlockResults["extract-1"].SkipReason.ShouldContain("previously 3");
        degradedRun.SkippedBlockIds.ShouldContain("hash-1");
        degradedRun.SkippedBlockIds.ShouldContain("condition-1");
        degradedRun.SkippedBlockIds.ShouldContain("notify-1");
        degradedRun.SkippedBlockIds.ShouldContain("output-1");
        degradedRun.BlockResults["hash-1"].Status.ShouldBe(BlockExecutionStatus.Skipped);
        degradedRun.BlockResults["condition-1"].Status.ShouldBe(BlockExecutionStatus.Skipped);
        degradedRun.BlockResults["notify-1"].Status.ShouldBe(BlockExecutionStatus.Skipped);
        degradedRun.BlockResults["output-1"].Status.ShouldBe(BlockExecutionStatus.Skipped);

        var storedExtract = await stateStore.GetPreviousOutputAsync(watchId.ToString(), "extract-1");
        storedExtract.ShouldNotBeNull();
        storedExtract.Value.GetRawText().ShouldBe(baselineExtract.GetRawText());
    }

    [Test]
    public async Task ExtractionDegradation_WithAllowEmptyBypass_DoesNotTriggerDegradation()
    {
        const string healthyHtml = "<html>healthy</html>";
        const string emptyHtml = "<html>empty</html>";

        var currentHtml = healthyHtml;
        var fetcher = CreateFetcher(() => currentHtml);
        var extractor = CreateExtractor(new Dictionary<string, Dictionary<string, string?>>
        {
            [healthyHtml] = new(StringComparer.Ordinal)
            {
                [".title"] = "Widget Alpha",
                [".price"] = "$29.99",
                [".status"] = "In stock"
            },
            [emptyHtml] = new(StringComparer.Ordinal)
            {
                [".title"] = null,
                [".price"] = null,
                [".status"] = null
            }
        });

        var services = CreatePipelineServices(fetcher, extractor);
        var registry = CreateReliabilityRegistry();
        var pipeline = BuildReliabilityPipeline(CreateExtractSchemaConfig(allowEmpty: true));
        var executor = CreateExecutor(registry, services);
        var stateStore = new InMemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        var baselineRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);
        baselineRun.Success.ShouldBeTrue();

        currentHtml = emptyHtml;
        var secondRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        secondRun.Success.ShouldBeTrue();
        secondRun.IsDegraded.ShouldBeFalse();
        secondRun.BlockResults["hash-1"].Status.ShouldBe(BlockExecutionStatus.Completed);
        secondRun.BlockResults["hash-1"].Output.ShouldNotBeNull();
        secondRun.BlockResults["hash-1"].Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();
        secondRun.BlockResults["notify-1"].Status.ShouldBe(BlockExecutionStatus.Completed);
        secondRun.BlockResults["output-1"].Status.ShouldBe(BlockExecutionStatus.Completed);

        var storedExtract = await stateStore.GetPreviousOutputAsync(watchId.ToString(), "extract-1");
        storedExtract.ShouldNotBeNull();
        storedExtract.Value.GetProperty("_meta_extractedCount").GetString().ShouldBe("0");
    }

    [Test]
    public async Task DryRunPreview_SuppressesNotificationsAndCostTracking()
    {
        const string currentHtml = "<html>current</html>";

        var fetcher = CreateFetcher(() => currentHtml);
        var extractor = CreateExtractor(new Dictionary<string, Dictionary<string, string?>>
        {
            [currentHtml] = new(StringComparer.Ordinal)
            {
                [".title"] = "Preview title",
                [".price"] = "$41.00",
                [".status"] = "New"
            }
        });

        var costTracker = Substitute.For<ILlmCostTracker>();
        var watchRepo = Substitute.For<IRepository<WatchedSite>>();
        watchRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new WatchedSite { Id = Guid.NewGuid(), Url = WatchUrl, MonthlyLlmBudget = 5m });

        var services = CreatePipelineServices(
            fetcher,
            extractor,
            (typeof(ILlmCostTracker), costTracker),
            (typeof(IRepository<WatchedSite>), watchRepo));

        var registry = CreateDryRunRegistry();
        var pipeline = BuildDryRunPipeline();
        var executor = CreateExecutor(registry, services);
        var stateStore = new InMemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        stateStore.SeedPreviousOutput(
            watchId.ToString(),
            "hash-1",
            JsonSerializer.SerializeToElement(new { hash = "old-hash", changed = false }));

        var result = await executor.ExecuteAsync(
            pipeline,
            watchId,
            stateStore,
            page: null,
            ct: default,
            isDryRun: true);

        result.Success.ShouldBeTrue();
        result.BlockResults["navigate-1"].Output.ShouldNotBeNull();
        result.BlockResults["llm-1"].Output.ShouldNotBeNull();
        result.BlockResults["hash-1"].Output.ShouldNotBeNull();
        result.BlockResults["condition-1"].Output.ShouldNotBeNull();
        result.BlockResults["notify-1"].Status.ShouldBe(BlockExecutionStatus.Skipped);
        result.BlockResults["notify-1"].SkipReason.ShouldBe("Preview mode — notifications suppressed");

        await watchRepo.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        await costTracker.DidNotReceiveWithAnyArgs().IsBudgetExceededAsync(default, default, default);
        await costTracker.DidNotReceiveWithAnyArgs()
            .RecordUsageAsync(default, default!, default!, default, default, default, default);
    }

    [Test]
    public async Task DryRunPreview_WithCachedHtml_UsesCachedHtmlWithoutRefetching()
    {
        const string cachedHtml = "<html><body>cached snapshot</body></html>";

        var fetcher = Substitute.For<IContentFetcher>();
        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.Validate(Arg.Any<string>()).Returns((string?)null);

        var services = BuildServiceProvider(
            (typeof(IContentFetcher), fetcher),
            (typeof(IUrlValidator), urlValidator),
            (typeof(ILoggerFactory), NullLoggerFactory.Instance));

        var pipeline = BlockContextBuilder.CreateSingleBlockPipeline(
            "navigate-1",
            "Navigate",
            new { _cachedHtml = cachedHtml });

        var context = new BlockContextBuilder()
            .WithBlockInstanceId("navigate-1")
            .WithInput("url", (object)WatchUrl)
            .WithServices(services)
            .WithPipelineDefinition(pipeline)
            .WithDryRun()
            .WithLogger(CreateLogger<NavigateBlock>())
            .Build();

        var result = await new NavigateBlock().ExecuteAsync(context);

        result.Success.ShouldBeTrue();
        result.Output.ShouldNotBeNull();
        result.Output!.Value.GetProperty("html").GetString().ShouldBe(cachedHtml);
        result.Output.Value.GetProperty("url").GetString().ShouldBe(WatchUrl);

        await fetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default!, default);
    }

    [Test]
    public async Task BlockOutputCaching_WhenContentUnchanged_UsesCachedExtractionOutput()
    {
        const string htmlA = "<html>A</html>";

        var currentHtml = htmlA;
        var fetcher = CreateFetcher(() => currentHtml);
        var extractor = CreateExtractor(new Dictionary<string, Dictionary<string, string?>>
        {
            [htmlA] = new(StringComparer.Ordinal)
            {
                [".title"] = "Stable title",
                [".price"] = "$55.00",
                [".status"] = "Available"
            }
        });

        var services = CreatePipelineServices(fetcher, extractor);
        var registry = CreateComparisonOutputRegistry();
        var pipeline = BuildCachingPipeline(CreateExtractSchemaConfig());
        var executor = CreateExecutor(registry, services);
        var stateStore = new InMemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        var firstRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);
        var secondRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        firstRun.Success.ShouldBeTrue();
        secondRun.Success.ShouldBeTrue();
        secondRun.BlockResults["extract-1"].CacheHit.ShouldBeTrue();
        secondRun.BlockResults["hash-1"].Status.ShouldBe(BlockExecutionStatus.Completed);
        secondRun.BlockResults["hash-1"].Output.ShouldNotBeNull();
        secondRun.BlockResults["hash-1"].Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task BlockOutputCaching_WhenContentChanges_InvalidatesExtractionCache()
    {
        const string htmlA = "<html>A</html>";
        const string htmlB = "<html>B</html>";

        var currentHtml = htmlA;
        var fetcher = CreateFetcher(() => currentHtml);
        var extractor = CreateExtractor(new Dictionary<string, Dictionary<string, string?>>
        {
            [htmlA] = new(StringComparer.Ordinal)
            {
                [".title"] = "Stable title",
                [".price"] = "$55.00",
                [".status"] = "Available"
            },
            [htmlB] = new(StringComparer.Ordinal)
            {
                [".title"] = "Updated title",
                [".price"] = "$49.00",
                [".status"] = "Backorder"
            }
        });

        var services = CreatePipelineServices(fetcher, extractor);
        var registry = CreateComparisonOutputRegistry();
        var pipeline = BuildCachingPipeline(CreateExtractSchemaConfig());
        var executor = CreateExecutor(registry, services);
        var stateStore = new InMemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        currentHtml = htmlB;
        var secondRun = await executor.ExecuteAsync(pipeline, watchId, stateStore, page: null);

        secondRun.Success.ShouldBeTrue();
        secondRun.BlockResults["extract-1"].CacheHit.ShouldBeFalse();
        secondRun.BlockResults["hash-1"].Output.ShouldNotBeNull();
        secondRun.BlockResults["hash-1"].Output!.Value.GetProperty("changed").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task BlockOutputCaching_WhenPipelineConfigChanges_InvalidatesExtractionCache()
    {
        const string htmlA = "<html>A</html>";

        var fetcher = CreateFetcher(() => htmlA);
        var extractor = CreateExtractor(new Dictionary<string, Dictionary<string, string?>>
        {
            [htmlA] = new(StringComparer.Ordinal)
            {
                [".title"] = "Stable title",
                [".price"] = "$55.00",
                [".status"] = "Available"
            }
        });

        var services = CreatePipelineServices(fetcher, extractor);
        var registry = CreateComparisonOutputRegistry();
        var pipelineV1 = BuildCachingPipeline(CreateExtractSchemaConfig(preferStructuredData: false));
        var pipelineV2 = BuildCachingPipeline(CreateExtractSchemaConfig(preferStructuredData: true));
        var executor = CreateExecutor(registry, services);
        var stateStore = new InMemoryBlockStateStore();
        var watchId = Guid.NewGuid();

        await executor.ExecuteAsync(pipelineV1, watchId, stateStore, page: null);
        var secondRun = await executor.ExecuteAsync(pipelineV2, watchId, stateStore, page: null);

        secondRun.Success.ShouldBeTrue();
        secondRun.BlockResults["extract-1"].CacheHit.ShouldBeFalse();
        secondRun.BlockResults["hash-1"].Output.ShouldNotBeNull();
        secondRun.BlockResults["hash-1"].Output!.Value.GetProperty("changed").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task SetupFlow_PersistsSetupTimeHtmlSnapshotForHealing()
    {
        const string setupHtml = "<html><body><p>Content</p></body></html>";

        var llmChain = Substitute.For<ILlmProviderChain>();
        var contentFetcher = Substitute.For<IContentFetcher>();
        var pipelineExecutor = Substitute.For<IPipelineExecutor>();
        var pipelineValidator = Substitute.For<IPipelineValidator>();
        var blockRegistry = CreateSetupBlockRegistry();
        var watchRepo = Substitute.For<IRepository<WatchedSite>>();

        pipelineValidator.Validate(Arg.Any<PipelineDefinition>(), Arg.Any<IBlockRegistry>())
            .Returns(ChangeDetection.Core.Pipeline.Validation.ValidationResult.Valid());

        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse("""
                    { "url": "https://example.com/page", "intent": "Monitor content changes", "changeType": "content" }
                    """),
                SuccessResponse("""
                    {
                        "contentType": "article",
                        "regions": ["main content"],
                        "hasPagination": false,
                        "needsJavaScript": false,
                        "pageSummary": "Article page"
                    }
                    """),
                SuccessResponse("""
                    {
                        "blocks": [
                            { "id": "input-1", "type": "Input", "position": 0 },
                            { "id": "navigate-1", "type": "Navigate", "position": 1 },
                            { "id": "filter-1", "type": "Filter", "position": 2 },
                            { "id": "hash-1", "type": "HashCompare", "position": 3 },
                            { "id": "output-1", "type": "Output", "position": 4 }
                        ],
                        "estimatedLlmCallsPerRun": 0
                    }
                    """),
                SuccessResponse("""{ "valid": true, "issues": [], "suggestions": [] }"""));

        contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = setupHtml,
                HttpStatusCode = 200,
                DurationMs = 100
            });

        pipelineExecutor.ExecuteAsync(
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
                ExecutionDurationMs = 100,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        var sut = new ComposableSetupPipeline(
            llmChain,
            contentFetcher,
            pipelineExecutor,
            pipelineValidator,
            blockRegistry,
            new PlatformDetector(),
            new PipelineTemplateRegistry(),
            watchRepo,
            new SetupFlowEnhancements(CreateLogger<SetupFlowEnhancements>()),
            null!,  // TODO: PipelineSecurityValidator - missing parameter
            null!,  // TODO: ContentSanitizer - missing parameter
            CreateLogger<ComposableSetupPipeline>());

        var request = new SetupRequest { UserInput = "Watch https://example.com/page for changes" };
        string? sessionId = null;

        await foreach (var progress in sut.StartSetupAsync(request))
        {
            if (progress.Phase == SetupPhase.Checkpoint1 && progress.Detail is not null)
                sessionId = progress.Detail.Replace("Session: ", "");
        }

        await foreach (var progress in sut.ConfirmIntentAsync(sessionId!, confirmed: true))
        {
            if (progress.Phase == SetupPhase.Checkpoint2 && progress.Detail is not null)
                sessionId = progress.Detail.Replace("Session: ", "");
        }

        sessionId.ShouldNotBeNull();

        await foreach (var _ in sut.ConfirmPipelineAsync(sessionId, confirmed: true))
        {
        }

        await watchRepo.Received(1).InsertAsync(
            Arg.Is<WatchedSite>(watch =>
                watch.Url == "https://example.com/page" &&
                watch.SetupTimeHtml == setupHtml &&
                watch.PipelineDefinitionJson != null),
            Arg.Any<CancellationToken>());
    }

    private IPipelineExecutor CreateExecutor(BlockRegistry registry, IServiceProvider services)
    {
        var validator = new PipelineValidator(CreateLogger<PipelineValidator>());
        return new PipelineExecutor(registry, validator, services, CreateLogger<PipelineExecutor>());
    }

    private static IServiceProvider CreatePipelineServices(
        IContentFetcher fetcher,
        IContentExtractor extractor,
        params (Type ServiceType, object Service)[] extras)
    {
        var urlValidator = Substitute.For<IUrlValidator>();
        urlValidator.Validate(Arg.Any<string>()).Returns((string?)null);

        var services = new List<(Type ServiceType, object Service)>
        {
            (typeof(ILoggerFactory), NullLoggerFactory.Instance),
            (typeof(IUrlValidator), urlValidator),
            (typeof(IContentFetcher), fetcher),
            (typeof(IContentExtractor), extractor),
            (typeof(IStructuredDataExtractor), new StructuredDataExtractor())
        };

        services.AddRange(extras);
        return BuildServiceProvider(services.ToArray());
    }

    private static IServiceProvider BuildServiceProvider(params (Type ServiceType, object Service)[] services)
    {
        var provider = Substitute.For<IServiceProvider>();

        foreach (var (serviceType, service) in services)
            provider.GetService(serviceType).Returns(service);

        return provider;
    }

    private static IContentFetcher CreateFetcher(Func<string> htmlAccessor)
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(_ => new FetchResult
            {
                IsSuccess = true,
                Html = htmlAccessor(),
                HttpStatusCode = 200,
                DurationMs = 5
            });
        return fetcher;
    }

    private static IContentExtractor CreateExtractor(
        IReadOnlyDictionary<string, Dictionary<string, string?>> extractionMap)
    {
        var extractor = Substitute.For<IContentExtractor>();

        extractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(call =>
            {
                var html = call.ArgAt<string>(0);
                var selector = call.ArgAt<string?>(1) ?? string.Empty;

                if (extractionMap.TryGetValue(html, out var selectors) &&
                    selectors.TryGetValue(selector, out var value))
                {
                    return value ?? string.Empty;
                }

                return string.Empty;
            });

        extractor.ExtractHtml(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(call => call.ArgAt<string>(0));

        extractor.ComputeHash(Arg.Any<string>())
            .Returns(call => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(call.ArgAt<string>(0)))));

        extractor.ExtractTitle(Arg.Any<string>()).Returns((string?)null);
        extractor.CleanHtml(Arg.Any<string>()).Returns(call => call.ArgAt<string>(0));

        return extractor;
    }

    private static JsonElement CreateExtractSchemaConfig(bool allowEmpty = false, bool preferStructuredData = false)
        => JsonSerializer.SerializeToElement(new
        {
            allowEmpty,
            preferStructuredData,
            enableLlmFallback = false,
            schema = new[]
            {
                new { field = "title", selector = ".title", type = "text" },
                new { field = "price", selector = ".price", type = "text" },
                new { field = "status", selector = ".status", type = "text" }
            }
        });

    private static BlockRegistry CreateReliabilityRegistry()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);
        registry.Register(
            "Output",
            inputPorts: [new PortDescriptor { Name = "notification", Type = PortType.Notification }],
            outputPorts: [],
            factory: _ => new NotificationOutputBlock());
        return registry;
    }

    private static BlockRegistry CreateComparisonOutputRegistry()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);
        registry.Register(
            "Output",
            inputPorts: [new PortDescriptor { Name = "result", Type = PortType.DiffResult }],
            outputPorts: [],
            factory: _ => new ResultOutputBlock());
        return registry;
    }

    private static BlockRegistry CreateDryRunRegistry()
    {
        var registry = CreateReliabilityRegistry();
        registry.Register(
            "LlmExtract",
            inputPorts: [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }],
            outputPorts: [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }],
            factory: _ => new PreviewLlmExtractBlock());
        return registry;
    }

    private static IBlockRegistry CreateSetupBlockRegistry()
    {
        var blockRegistry = Substitute.For<IBlockRegistry>();
        blockRegistry.IsRegistered(Arg.Any<string>()).Returns(true);
        blockRegistry.RegisteredBlockTypes.Returns(new List<string>
        {
            "Input", "Output", "Navigate", "Filter", "ExtractSchema", "HashCompare", "Condition", "Notify"
        });

        blockRegistry.GetInputPorts("Input").Returns(new List<PortDescriptor>());
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

        blockRegistry.GetInputPorts("Filter").Returns(new List<PortDescriptor>
        {
            new() { Name = "html", Type = PortType.HtmlContent }
        });
        blockRegistry.GetOutputPorts("Filter").Returns(new List<PortDescriptor>
        {
            new() { Name = "html", Type = PortType.HtmlContent }
        });

        blockRegistry.GetInputPorts("ExtractSchema").Returns(new List<PortDescriptor>
        {
            new() { Name = "html", Type = PortType.HtmlContent }
        });
        blockRegistry.GetOutputPorts("ExtractSchema").Returns(new List<PortDescriptor>
        {
            new() { Name = "data", Type = PortType.ExtractedObjects }
        });

        blockRegistry.GetInputPorts("HashCompare").Returns(new List<PortDescriptor>
        {
            new() { Name = "data", Type = PortType.ExtractedObjects }
        });
        blockRegistry.GetOutputPorts("HashCompare").Returns(new List<PortDescriptor>
        {
            new() { Name = "result", Type = PortType.BooleanSignal }
        });

        blockRegistry.GetInputPorts("Output").Returns(new List<PortDescriptor>
        {
            new() { Name = "data", Type = PortType.ExtractedObjects }
        });
        blockRegistry.GetOutputPorts("Output").Returns(new List<PortDescriptor>());

        return blockRegistry;
    }

    private static PipelineDefinition BuildReliabilityPipeline(JsonElement extractConfig)
        => new()
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition
                {
                    Id = "input-1",
                    Type = "Input",
                    Config = JsonSerializer.SerializeToElement(new { url = WatchUrl })
                },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "extract-1", Type = "ExtractSchema", Config = extractConfig },
                new BlockDefinition { Id = "hash-1", Type = "HashCompare" },
                new BlockDefinition
                {
                    Id = "condition-1",
                    Type = "Condition",
                    Config = JsonSerializer.SerializeToElement(new { field = "changed", @operator = "equals", value = true })
                },
                new BlockDefinition
                {
                    Id = "notify-1",
                    Type = "Notify",
                    Config = JsonSerializer.SerializeToElement(new { channel = "email", template = "changed" })
                },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "extract-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "hash-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "hash-1", FromPort = "result", ToBlockId = "condition-1", ToPort = "result" },
                new ConnectionDefinition { FromBlockId = "condition-1", FromPort = "signal", ToBlockId = "notify-1", ToPort = "signal" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "notify-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "notify-1", FromPort = "notification", ToBlockId = "output-1", ToPort = "notification" }
            ]
        };

    private static PipelineDefinition BuildDryRunPipeline()
        => new()
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition
                {
                    Id = "input-1",
                    Type = "Input",
                    Config = JsonSerializer.SerializeToElement(new { url = WatchUrl })
                },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "llm-1", Type = "LlmExtract" },
                new BlockDefinition { Id = "hash-1", Type = "HashCompare" },
                new BlockDefinition
                {
                    Id = "condition-1",
                    Type = "Condition",
                    Config = JsonSerializer.SerializeToElement(new { field = "changed", @operator = "equals", value = true })
                },
                new BlockDefinition
                {
                    Id = "notify-1",
                    Type = "Notify",
                    Config = JsonSerializer.SerializeToElement(new { channel = "email", template = "changed" })
                },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "llm-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "llm-1", FromPort = "data", ToBlockId = "hash-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "hash-1", FromPort = "result", ToBlockId = "condition-1", ToPort = "result" },
                new ConnectionDefinition { FromBlockId = "condition-1", FromPort = "signal", ToBlockId = "notify-1", ToPort = "signal" },
                new ConnectionDefinition { FromBlockId = "llm-1", FromPort = "data", ToBlockId = "notify-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "notify-1", FromPort = "notification", ToBlockId = "output-1", ToPort = "notification" }
            ]
        };

    private static PipelineDefinition BuildCachingPipeline(JsonElement extractConfig)
        => new()
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition
                {
                    Id = "input-1",
                    Type = "Input",
                    Config = JsonSerializer.SerializeToElement(new { url = WatchUrl })
                },
                new BlockDefinition { Id = "navigate-1", Type = "Navigate" },
                new BlockDefinition { Id = "extract-1", Type = "ExtractSchema", Config = extractConfig },
                new BlockDefinition { Id = "hash-1", Type = "HashCompare" },
                new BlockDefinition { Id = "output-1", Type = "Output" }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "input-1", FromPort = "url", ToBlockId = "navigate-1", ToPort = "url" },
                new ConnectionDefinition { FromBlockId = "navigate-1", FromPort = "html", ToBlockId = "extract-1", ToPort = "html" },
                new ConnectionDefinition { FromBlockId = "extract-1", FromPort = "data", ToBlockId = "hash-1", ToPort = "data" },
                new ConnectionDefinition { FromBlockId = "hash-1", FromPort = "result", ToBlockId = "output-1", ToPort = "result" }
            ]
        };

    private static LlmResponse SuccessResponse(string content) => new()
    {
        IsSuccess = true,
        Content = content,
        ProviderUsed = "test",
        Model = "test-model",
        DurationMs = 50
    };

    private sealed class NotificationOutputBlock : IPipelineBlock
    {
        public string BlockType => "Output";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "notification", Type = PortType.Notification }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Delivery;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var output = context.Inputs.TryGetValue("notification", out var notification)
                ? notification
                : JsonSerializer.SerializeToElement(new { });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private sealed class ResultOutputBlock : IPipelineBlock
    {
        public string BlockType => "Output";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "result", Type = PortType.DiffResult }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Delivery;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var output = context.Inputs.TryGetValue("result", out var result)
                ? result
                : JsonSerializer.SerializeToElement(new { });
            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private sealed class PreviewLlmExtractBlock : IPipelineBlock
    {
        public string BlockType => "LlmExtract";
        public IReadOnlyList<PortDescriptor> InputPorts => [new PortDescriptor { Name = "html", Type = PortType.HtmlContent }];
        public IReadOnlyList<PortDescriptor> OutputPorts => [new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }];
        public BlockCriticalityTier CriticalityTier => BlockCriticalityTier.Extraction;
        public bool IsCacheable => false;

        public Task<BlockResult> ExecuteAsync(BlockContext context)
        {
            var html = context.Inputs["html"];
            var content = html.ValueKind == JsonValueKind.String
                ? html.GetString()
                : html.GetProperty("html").GetString();

            var output = JsonSerializer.SerializeToElement(new
            {
                title = "Preview title",
                price = "$41.00",
                status = "New",
                sourceHtml = content,
                inputTokens = 120,
                outputTokens = 80,
                model = "gpt-test"
            });

            return Task.FromResult(BlockResult.Succeeded(output));
        }
    }

    private sealed class InMemoryBlockStateStore : IBlockStateStore
    {
        private readonly ConcurrentDictionary<string, JsonElement> _latest = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, JsonElement> _cache = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ConcurrentQueue<BlockExecutionSnapshot>> _history = new(StringComparer.Ordinal);

        public void SeedPreviousOutput(string watchId, string blockInstanceId, JsonElement output)
        {
            _latest[LatestKey(watchId, blockInstanceId)] = output.Clone();
        }

        public Task<JsonElement?> GetPreviousOutputAsync(string watchId, string blockInstanceId, CancellationToken ct = default)
        {
            var key = LatestKey(watchId, blockInstanceId);
            return Task.FromResult(_latest.TryGetValue(key, out var output) ? (JsonElement?)output.Clone() : null);
        }

        public Task<JsonElement?> GetCachedOutputAsync(
            string watchId,
            string blockInstanceId,
            string inputHash,
            string pipelineHash,
            CancellationToken ct = default)
        {
            var key = CacheKey(watchId, blockInstanceId, inputHash, pipelineHash);
            return Task.FromResult(_cache.TryGetValue(key, out var output) ? (JsonElement?)output.Clone() : null);
        }

        public Task SaveOutputAsync(
            string watchId,
            string blockInstanceId,
            JsonElement output,
            string? inputHash = null,
            string? pipelineHash = null,
            CancellationToken ct = default)
        {
            var clone = output.Clone();
            _latest[LatestKey(watchId, blockInstanceId)] = clone;

            if (!string.IsNullOrWhiteSpace(inputHash) && !string.IsNullOrWhiteSpace(pipelineHash))
                _cache[CacheKey(watchId, blockInstanceId, inputHash, pipelineHash)] = clone;

            var history = _history.GetOrAdd(LatestKey(watchId, blockInstanceId), _ => new ConcurrentQueue<BlockExecutionSnapshot>());
            history.Enqueue(new BlockExecutionSnapshot
            {
                WatchId = watchId,
                BlockInstanceId = blockInstanceId,
                Timestamp = DateTime.UtcNow,
                Output = clone
            });

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BlockExecutionSnapshot>> GetHistoryAsync(
            string watchId,
            string blockInstanceId,
            int maxResults = 10,
            CancellationToken ct = default)
        {
            var key = LatestKey(watchId, blockInstanceId);
            if (!_history.TryGetValue(key, out var history))
                return Task.FromResult<IReadOnlyList<BlockExecutionSnapshot>>([]);

            var snapshots = history.Reverse().Take(maxResults).ToList();
            return Task.FromResult<IReadOnlyList<BlockExecutionSnapshot>>(snapshots);
        }

        private static string LatestKey(string watchId, string blockInstanceId) => $"{watchId}:{blockInstanceId}";
        private static string CacheKey(string watchId, string blockInstanceId, string inputHash, string pipelineHash)
            => $"{watchId}:{blockInstanceId}:{inputHash}:{pipelineHash}";
    }
}
