using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.GroupWatch;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.Search;
using ChangeDetection.Services.SetupPipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using System.Net;
using TUnit.Core;

namespace ChangeDetection.Tests.Services;

/// <summary>
/// Regression tests for L1–L6 bug fixes.
/// </summary>
[Category("Unit")]
public class BugfixRegressionTests : TestBase
{
    // ───────────────────────────────────────────────────
    // L1: SkipInitialCheck must always be true
    // ───────────────────────────────────────────────────

    [Test]
    public async Task L1_CreateWatchForPortal_WithPipeline_SkipInitialCheckIsTrue()
    {
        var (sut, watchService) = CreateGroupWatchSut();
        var createdWatch = new WatchedSite { Id = Guid.NewGuid(), Url = "https://example.com/jobs" };
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(createdWatch);

        // Use CreateWatchesAsync to exercise CreateWatchForPortalAsync
        // DiscoveredPortal doesn't carry pipeline — pipeline is built internally
        var portals = new List<DiscoveredPortal>
        {
            new("https://example.com/jobs", "example.com", "relevant", "Example Jobs")
        };

        await foreach (var _ in sut.CreateWatchesAsync("test jobs", portals)) { }

        await watchService.Received().CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(r => r.SkipInitialCheck == true),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task L1_CreateWatchForPortal_WithoutPipeline_SkipInitialCheckIsTrue()
    {
        var (sut, watchService) = CreateGroupWatchSut();
        var createdWatch = new WatchedSite { Id = Guid.NewGuid(), Url = "https://unknown.com/careers" };
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(createdWatch);

        // Portal with unknown platform — pipeline will be built via fallback chain
        var portals = new List<DiscoveredPortal>
        {
            new("https://unknown.com/careers", "unknown.com", "maybe relevant", "Unknown Careers")
        };

        await foreach (var _ in sut.CreateWatchesAsync("test jobs", portals)) { }

        await watchService.Received().CreateWatchAsync(
            Arg.Is<CreateWatchRequest>(r => r.SkipInitialCheck == true),
            Arg.Any<CancellationToken>());
    }

    // ───────────────────────────────────────────────────
    // L2+L3: BuildPipelineForPortalAsync fallback chain
    // ───────────────────────────────────────────────────

    [Test]
    public async Task L2L3_BuildPipelineForPortal_UnknownPlatform_ReturnsPipelineNotNull()
    {
        // When the URL doesn't match any known platform template,
        // BuildPipelineForPortalAsync should try LLM then fall back to generic — never return null.
        var composableSetup = Substitute.For<IComposableSetupPipeline>();

        // LLM-based flow fails
        composableSetup.StartSetupAsync(Arg.Any<SetupRequest>(), Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new SetupProgress
            {
                Phase = SetupPhase.Checkpoint1,
                Type = SetupProgressType.Failed,
                Message = "LLM unavailable",
                Error = "LLM unavailable"
            }));

        var (sut, watchService) = CreateGroupWatchSut(composableSetup: composableSetup);
        var createdWatch = new WatchedSite { Id = Guid.NewGuid(), Url = "https://example.com/careers" };
        watchService.CreateWatchAsync(Arg.Any<CreateWatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(createdWatch);
        watchService.UpdateWatchAsync(Arg.Any<WatchedSite>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Use a URL that won't match any platform template
        var portals = new List<DiscoveredPortal>
        {
            new("https://example.com/careers", "example.com", "Example Careers", "relevant", null)
        };

        await foreach (var _ in sut.CreateWatchesAsync("find jobs", portals)) { }

        // The watch should have been created AND updated with a pipeline (from generic fallback)
        await watchService.Received().UpdateWatchAsync(
            Arg.Is<WatchedSite>(w => w.PipelineDefinitionJson != null),
            Arg.Any<CancellationToken>());
    }

    // ───────────────────────────────────────────────────
    // L4: PipelineExecutor cycle detection returns gracefully
    // ───────────────────────────────────────────────────

    [Test]
    public async Task L4_ExecuteAsync_CyclicPipeline_ReturnsFailedResult()
    {
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        // Register minimal test blocks
        registry.Register("TestBlock",
            inputPorts: [new PortDescriptor { Name = "in", Type = PortType.ExtractedObjects }],
            outputPorts: [new PortDescriptor { Name = "out", Type = PortType.ExtractedObjects }],
            factory: _ => throw new NotImplementedException("Should not execute"));

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "a", Type = "TestBlock", Position = 0 },
                new BlockDefinition { Id = "b", Type = "TestBlock", Position = 1 }
            ],
            Connections =
            [
                new ConnectionDefinition { FromBlockId = "a", FromPort = "out", ToBlockId = "b", ToPort = "in" },
                new ConnectionDefinition { FromBlockId = "b", FromPort = "out", ToBlockId = "a", ToPort = "in" }
            ]
        };

        var validator = new PipelineValidator(CreateLogger<PipelineValidator>());
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ILoggerFactory)).Returns(NullLoggerFactory.Instance);
        var executor = new PipelineExecutor(registry, validator, sp, CreateLogger<PipelineExecutor>());

        var stateStore = Substitute.For<IBlockStateStore>();
        stateStore.GetPreviousOutputAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((JsonElement?)null);

        var result = await executor.ExecuteAsync(pipeline, Guid.NewGuid(), stateStore, null);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNullOrEmpty();
        result.Error.ShouldContain("cycle", Case.Insensitive);
    }

    // ───────────────────────────────────────────────────
    // L5: CopilotSearchProvider excludes itself from MultiProvider
    // ───────────────────────────────────────────────────

    [Test]
    public async Task L5_MultiProviderSearchAllAsync_ExcludesSpecifiedProviders()
    {
        var excludedProvider = Substitute.For<ISearchProvider>();
        excludedProvider.ProviderId.Returns("copilot");
        excludedProvider.IsAvailable.Returns(true);

        var includedProvider = Substitute.For<ISearchProvider>();
        includedProvider.ProviderId.Returns("brave");
        includedProvider.IsAvailable.Returns(true);
        includedProvider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = "brave",
                Query = "test",
                Results = [new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }],
                IsSuccess = true
            });

        var svc = new MultiProviderSearchService(
            [excludedProvider, includedProvider],
            CreateLogger<MultiProviderSearchService>());

        var result = await svc.SearchAllAsync(
            new SearchQuery { Query = "test" },
            excludeProviderIds: ["copilot"]);

        // The copilot provider should NOT have been called
        await excludedProvider.DidNotReceive().SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
        // The brave provider should have been called
        await includedProvider.Received(1).SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>());
        result.MergedResults.Count.ShouldBe(1);
    }

    // ───────────────────────────────────────────────────
    // L6: Port type validator strict compatibility
    // ───────────────────────────────────────────────────

    [Test]
    public async Task L6_SearchResults_To_ExtractedObjects_IsNotValid()
    {
        var validator = new PipelineValidator(CreateLogger<PipelineValidator>());
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        registry.Register("TestSearchSource",
            inputPorts: [],
            outputPorts: [new PortDescriptor { Name = "out", Type = PortType.SearchResults }],
            factory: _ => throw new NotImplementedException());

        registry.Register("TestExtractedTarget",
            inputPorts: [new PortDescriptor { Name = "in", Type = PortType.ExtractedObjects }],
            outputPorts: [],
            factory: _ => throw new NotImplementedException());

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "src", Type = "TestSearchSource" },
                new BlockDefinition { Id = "tgt", Type = "TestExtractedTarget" }
            ],
            Connections =
            [
                new ConnectionDefinition
                {
                    FromBlockId = "src", FromPort = "out",
                    ToBlockId = "tgt", ToPort = "in"
                }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeTrue("SearchResults → ExtractedObjects should NOT be compatible (strict type matching)");
        await Task.CompletedTask;
    }

    [Test]
    public async Task L6_SearchResults_To_PlainText_IsNotValid()
    {
        var validator = new PipelineValidator(CreateLogger<PipelineValidator>());
        var registry = new BlockRegistry();
        BlockRegistry.RegisterCoreBlocks(registry);

        registry.Register("TestSearchSrc",
            inputPorts: [],
            outputPorts: [new PortDescriptor { Name = "out", Type = PortType.SearchResults }],
            factory: _ => throw new NotImplementedException());

        registry.Register("TestPlainTgt",
            inputPorts: [new PortDescriptor { Name = "in", Type = PortType.PlainText }],
            outputPorts: [],
            factory: _ => throw new NotImplementedException());

        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "src", Type = "TestSearchSrc" },
                new BlockDefinition { Id = "tgt", Type = "TestPlainTgt" }
            ],
            Connections =
            [
                new ConnectionDefinition
                {
                    FromBlockId = "src", FromPort = "out",
                    ToBlockId = "tgt", ToPort = "in"
                }
            ]
        };

        var result = validator.Validate(pipeline, registry);

        var hasTypeMismatch = result.Errors.Any(e => e.Code == "PORT_TYPE_MISMATCH");
        hasTypeMismatch.ShouldBeTrue("SearchResults → PlainText should NOT be compatible (strict type matching)");
        await Task.CompletedTask;
    }

    // ───────────────────────────────────────────────────
    // Helpers
    // ───────────────────────────────────────────────────

    private (GroupWatchDiscoveryService sut, IWatchService watchService) CreateGroupWatchSut(
        IComposableSetupPipeline? composableSetup = null)
    {
        var searchProvider = Substitute.For<ISearchProvider>();
        searchProvider.ProviderId.Returns("test");
        searchProvider.DisplayName.Returns("Test");
        searchProvider.IsAvailable.Returns(true);

        var multiSearch = new MultiProviderSearchService([searchProvider], CreateLogger<MultiProviderSearchService>());
        var llmChain = Substitute.For<ILlmProviderChain>();
        var watchGroupService = Substitute.For<IWatchGroupService>();
        var watchService = Substitute.For<IWatchService>();
        composableSetup ??= Substitute.For<IComposableSetupPipeline>();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();

        httpClientFactory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(new StubHandler()));

        llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """{"location":"test","roleTypes":["scientist"],"field":"biology","searchQueries":["test"]}"""
            });

        watchGroupService.CreateGroupAsync(Arg.Any<WatchGroupCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new WatchGroup { Id = Guid.NewGuid(), Name = "test-group" });

        var setupFlowEnhancements = new SetupFlowEnhancements(
            CreateLogger<SetupFlowEnhancements>(), httpClientFactory);

        var sut = new GroupWatchDiscoveryService(
            multiSearch,
            llmChain,
            watchGroupService,
            watchService,
            setupFlowEnhancements,
            composableSetup,
            httpClientFactory,
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<IWatchExecutionLock>(),
            CreateLogger<GroupWatchDiscoveryService>(),
            Options.Create(new GroupWatchDiscoveryOptions()));

        return (sut, watchService);
    }

    private static async IAsyncEnumerable<SetupProgress> ToAsyncEnumerable(params SetupProgress[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>jobs</body></html>"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }
}
