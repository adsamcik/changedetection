using System.Reflection;
using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Hubs;
using ChangeDetection.Services;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.Background;
using ChangeDetection.Services.GroupWatch;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Services.Background;

[Category("Unit")]
public class WatchRegressionTests : TestBase
{
    [Test]
    public async Task FirstGarbageExtraction_PreservesPipeline_AndMarksDegraded()
    {
        var watch = CreateGroupWatch(CatalogVerificationStatus.Unverified, headlessBuildAttempts: 1);
        var pipelineExecutor = Substitute.For<IPipelineExecutor>();
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
                OutputData = JsonDocument.Parse("""[{"title":"HOME","url":"#"}]""").RootElement,
                BlockResults = new Dictionary<string, BlockResult>(),
                ExecutionDurationMs = 50,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        var (sut, provider, hubContext) = CreateSutWithServices(watch, pipelineExecutor);

        await InvokeCheckWithPipelineExecutorAsync(sut, provider, watch, hubContext);

        watch.CatalogStatus.ShouldBe(CatalogVerificationStatus.Degraded);
        watch.PipelineDefinitionJson.ShouldNotBeNullOrWhiteSpace();
        watch.HeadlessBuildAttempts.ShouldBe(1);
    }

    [Test]
    public async Task SecondConsecutiveGarbage_ClearsPipeline_AndResetsHeadlessAttempts()
    {
        var watch = CreateGroupWatch(CatalogVerificationStatus.Degraded, headlessBuildAttempts: 2);
        var pipelineExecutor = Substitute.For<IPipelineExecutor>();
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
                OutputData = JsonDocument.Parse("""[{"title":"ABOUT","url":"#"}]""").RootElement,
                BlockResults = new Dictionary<string, BlockResult>(),
                ExecutionDurationMs = 50,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        var (sut, provider, hubContext) = CreateSutWithServices(watch, pipelineExecutor);

        await InvokeCheckWithPipelineExecutorAsync(sut, provider, watch, hubContext);

        watch.CatalogStatus.ShouldBe(CatalogVerificationStatus.Degraded);
        watch.PipelineDefinitionJson.ShouldBeNull();
        watch.HeadlessBuildAttempts.ShouldBe(0);
    }

    [Test]
    public async Task SuccessfulExtractionAfterDegraded_RecoversToVerified()
    {
        var watch = CreateGroupWatch(CatalogVerificationStatus.Degraded, headlessBuildAttempts: 1);
        watch.ConsecutiveSuccessfulChecks = 2;
        watch.TotalItemsExtracted = 4;

        var pipelineExecutor = Substitute.For<IPipelineExecutor>();
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
                OutputData = JsonDocument.Parse("""[{"title":"Research Scientist","url":"https://jobs.example.com/1"}]""").RootElement,
                BlockResults = new Dictionary<string, BlockResult>(),
                ExecutionDurationMs = 50,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        var (sut, provider, hubContext) = CreateSutWithServices(watch, pipelineExecutor);

        await InvokeCheckWithPipelineExecutorAsync(sut, provider, watch, hubContext);

        watch.CatalogStatus.ShouldBe(CatalogVerificationStatus.Verified);
        watch.ConsecutiveSuccessfulChecks.ShouldBe(3);
        watch.Status.ShouldBe(WatchStatus.Active);
    }

    private (ChangeCheckBackgroundService Sut, IServiceProvider Provider, IHubContext<ChangeDetectionHub> HubContext) CreateSutWithServices(
        WatchedSite watch,
        IPipelineExecutor pipelineExecutor)
    {
        var hubContext = Substitute.For<IHubContext<ChangeDetectionHub>>();
        hubContext.Clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());

        var stateStore = Substitute.For<IBlockStateStore>();
        var watchRepo = Substitute.For<IRepository<WatchedSite>>();
        var snapshotRepo = Substitute.For<IRepository<ChangeSnapshot>>();
        var eventRepo = Substitute.For<IRepository<ChangeEvent>>();
        var diffService = Substitute.For<IDiffService>();
        var summaryStore = Substitute.For<IPipelineRunSummaryStore>();
        var portalDiscoveryAnalyzer = Substitute.For<IPortalDiscoveryAnalyzer>();
        portalDiscoveryAnalyzer.AnalyzeForNewPortalsAsync(Arg.Any<Guid>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var portalSuggestionService = Substitute.For<IPortalSuggestionService>();

        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IPipelineExecutor)).Returns(pipelineExecutor);
        serviceProvider.GetService(typeof(IBlockStateStore)).Returns(stateStore);
        serviceProvider.GetService(typeof(IRepository<WatchedSite>)).Returns(watchRepo);
        serviceProvider.GetService(typeof(IRepository<ChangeSnapshot>)).Returns(snapshotRepo);
        serviceProvider.GetService(typeof(IRepository<ChangeEvent>)).Returns(eventRepo);
        serviceProvider.GetService(typeof(IDiffService)).Returns(diffService);
        serviceProvider.GetService(typeof(IPipelineRunSummaryStore)).Returns(summaryStore);
        serviceProvider.GetService(typeof(IPortalDiscoveryAnalyzer)).Returns(portalDiscoveryAnalyzer);
        serviceProvider.GetService(typeof(IPortalSuggestionService)).Returns(portalSuggestionService);

        var scopeFactory = Substitute.For<IBackgroundServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateBackgroundScope().Returns(scope);

        var sut = new ChangeCheckBackgroundService(
            scopeFactory,
            CreateLogger<ChangeCheckBackgroundService>(),
            new WatchExecutionLock());

        return (sut, serviceProvider, hubContext);
    }

    private static WatchedSite CreateGroupWatch(CatalogVerificationStatus status, int headlessBuildAttempts)
        => new()
        {
            Id = Guid.NewGuid(),
            Url = "https://jobs.example.com",
            Name = "Jobs",
            GroupId = Guid.NewGuid(),
            CatalogStatus = status,
            HeadlessBuildAttempts = headlessBuildAttempts,
            PipelineDefinitionJson = PipelineSerializer.Serialize(new PipelineDefinition
            {
                SchemaVersion = 1,
                Blocks =
                [
                    new BlockDefinition
                    {
                        Id = "input-1",
                        Type = "Input",
                        Position = 0,
                        Config = JsonSerializer.SerializeToElement(new { url = "https://jobs.example.com" })
                    },
                    new BlockDefinition { Id = "output-1", Type = "Output", Position = 1 }
                ],
                Connections = [],
                Metadata = new PipelineMetadata
                {
                    DisplayTitle = "Jobs",
                    CreatedAt = DateTime.UtcNow,
                    UserIntent = "jobs"
                }
            })
        };

    private static async Task InvokeCheckWithPipelineExecutorAsync(
        ChangeCheckBackgroundService sut,
        IServiceProvider serviceProvider,
        WatchedSite watch,
        IHubContext<ChangeDetectionHub> hubContext)
    {
        var method = typeof(ChangeCheckBackgroundService)
            .GetMethod("CheckWithPipelineExecutorAsync", BindingFlags.Instance | BindingFlags.NonPublic);

        method.ShouldNotBeNull();

        var task = (Task)method!.Invoke(sut, [serviceProvider, watch, hubContext, "dashboard", CancellationToken.None])!;
        await task;
    }
}
