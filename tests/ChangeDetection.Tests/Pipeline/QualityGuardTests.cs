using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services;
using ChangeDetection.Services.Authentication;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Background;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.SetupPipeline;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

[Category("Unit")]
public class QualityGuardTests : TestBase
{
    private ILlmProviderChain _llmChain = null!;
    private IContentFetcher _contentFetcher = null!;
    private IPipelineExecutor _pipelineExecutor = null!;
    private IPipelineValidator _pipelineValidator = null!;
    private IBlockRegistry _blockRegistry = null!;
    private IRepository<WatchedSite> _watchRepo = null!;
    private ComposableSetupPipeline _sut = null!;

    [Before(Test)]
    public void Setup()
    {
        _llmChain = Substitute.For<ILlmProviderChain>();
        _contentFetcher = Substitute.For<IContentFetcher>();
        _pipelineExecutor = Substitute.For<IPipelineExecutor>();
        _pipelineValidator = Substitute.For<IPipelineValidator>();
        _blockRegistry = Substitute.For<IBlockRegistry>();
        _watchRepo = Substitute.For<IRepository<WatchedSite>>();

        _blockRegistry.IsRegistered(Arg.Any<string>()).Returns(true);
        _blockRegistry.RegisteredBlockTypes.Returns(["Input", "Navigate", "ExtractSchema", "Output"]);
        _blockRegistry.GetInputPorts("Input").Returns([]);
        _blockRegistry.GetOutputPorts("Input").Returns(
        [
            new PortDescriptor { Name = "url", Type = PortType.Url }
        ]);
        _blockRegistry.GetInputPorts("Navigate").Returns(
        [
            new PortDescriptor { Name = "url", Type = PortType.Url }
        ]);
        _blockRegistry.GetOutputPorts("Navigate").Returns(
        [
            new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
        ]);
        _blockRegistry.GetInputPorts("ExtractSchema").Returns(
        [
            new PortDescriptor { Name = "html", Type = PortType.HtmlContent }
        ]);
        _blockRegistry.GetOutputPorts("ExtractSchema").Returns(
        [
            new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }
        ]);
        _blockRegistry.GetInputPorts("Output").Returns(
        [
            new PortDescriptor { Name = "data", Type = PortType.ExtractedObjects }
        ]);
        _blockRegistry.GetOutputPorts("Output").Returns([]);
        _pipelineValidator.Validate(Arg.Any<PipelineDefinition>(), Arg.Any<IBlockRegistry>())
            .Returns(ValidationResult.Valid());

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><div class='job'>Scientist</div></body></html>",
                HttpStatusCode = 200
            });

        _sut = new ComposableSetupPipeline(
            _llmChain,
            _contentFetcher,
            _pipelineExecutor,
            _pipelineValidator,
            _blockRegistry,
            new PlatformDetector(),
            new PipelineTemplateRegistry(),
            _watchRepo,
            new SetupFlowEnhancements(CreateLogger<SetupFlowEnhancements>()),
            new PipelineSecurityValidator(
                new DomainPinValidator(CreateLogger<DomainPinValidator>()),
                CreateLogger<PipelineSecurityValidator>()),
            new ContentSanitizer(),
            CreateLogger<ComposableSetupPipeline>());
    }

    [Test]
    public async Task BuildPipelineHeadlessAsync_WhenLlmCannotExtract_ReturnsNull()
    {
        ConfigureLlmResponses(
            intentJson: """
                {"url":"https://jobs.example.com","intent":"Track jobs","changeType":"listing","summary":"Track jobs"}
                """,
            analysisJson: """
                {"contentType":"jobs","regions":["jobs"],"hasPagination":false,"needsJavaScript":false,"recommendedSelector":".job","pageSummary":"Jobs"}
                """,
            pipelineJson: """
                {"cannotExtract": true, "reason": "content hidden behind captcha"}
                """);

        var pipeline = await _sut.BuildPipelineHeadlessAsync("https://jobs.example.com", "track jobs");

        pipeline.ShouldBeNull();
    }

    [Test]
    public async Task BuildPipelineHeadlessAsync_WhenDryRunExtractsZeroItems_ReturnsNull()
    {
        ConfigureSuccessfulPipelineBuild("""
            {
              "blocks": [
                { "id": "input-1", "type": "Input", "position": 0, "config": { "url": "https://jobs.example.com" } },
                { "id": "navigate-1", "type": "Navigate", "position": 1, "config": { "useJavaScript": false } },
                { "id": "extract-1", "type": "ExtractSchema", "position": 2, "config": { "scope": ".job-list", "schema": [{ "name": "title", "selector": ".job-title", "type": "text" }] } },
                { "id": "output-1", "type": "Output", "position": 3 }
              ]
            }
            """);

        _pipelineExecutor.ExecuteAsync(
                Arg.Any<PipelineDefinition>(),
                Arg.Any<Guid>(),
                Arg.Any<IBlockStateStore>(),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<bool>())
            .Returns(new PipelineExecutionResult
            {
                Success = true,
                OutputData = JsonDocument.Parse("[]").RootElement,
                BlockResults = new Dictionary<string, BlockResult>(),
                ExecutionDurationMs = 25,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        var pipeline = await _sut.BuildPipelineHeadlessAsync("https://jobs.example.com", "track jobs");

        pipeline.ShouldBeNull();
    }

    [Test]
    public async Task BuildPipelineHeadlessAsync_WhenSelectorsArePlaceholders_ReturnsNull()
    {
        ConfigureSuccessfulPipelineBuild("""
            {
              "blocks": [
                { "id": "input-1", "type": "Input", "position": 0, "config": { "url": "https://jobs.example.com" } },
                { "id": "navigate-1", "type": "Navigate", "position": 1, "config": { "useJavaScript": false } },
                { "id": "extract-1", "type": "ExtractSchema", "position": 2, "config": { "scope": ".job-card", "schema": [{ "name": "title", "selector": ".job-item", "type": "text" }] } },
                { "id": "output-1", "type": "Output", "position": 3 }
              ]
            }
            """);

        var pipeline = await _sut.BuildPipelineHeadlessAsync("https://jobs.example.com", "track jobs");

        pipeline.ShouldBeNull();
        await _pipelineExecutor.DidNotReceive().ExecuteAsync(
            Arg.Any<PipelineDefinition>(),
            Arg.Any<Guid>(),
            Arg.Any<IBlockStateStore>(),
            Arg.Any<object?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>());
    }

    [Test]
    public async Task TryBuildLlmPipelineAsync_WhenHeadlessAttemptsAlreadyMaxedOut_SkipsBuilding()
    {
        var watch = new WatchedSite
        {
            Id = Guid.NewGuid(),
            Url = "https://jobs.example.com",
            HeadlessBuildAttempts = 2
        };

        var composable = Substitute.For<IComposableSetupPipeline>();
        var watchRepo = Substitute.For<IRepository<WatchedSite>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(IComposableSetupPipeline)).Returns(composable);
        serviceProvider.GetService(typeof(IRepository<WatchedSite>)).Returns(watchRepo);

        var scopeFactory = Substitute.For<IBackgroundServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);
        scopeFactory.CreateBackgroundScope().Returns(scope);

        var backgroundService = new ChangeCheckBackgroundService(
            scopeFactory,
            CreateLogger<ChangeCheckBackgroundService>(),
            new WatchExecutionLock());

        var method = typeof(ChangeCheckBackgroundService)
            .GetMethod("TryBuildLlmPipelineAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        method.ShouldNotBeNull();
        var task = (Task<PipelineDefinition?>)method!.Invoke(backgroundService, [serviceProvider, watch, CancellationToken.None])!;
        var pipeline = await task;

        pipeline.ShouldBeNull();
        await composable.DidNotReceive().BuildPipelineHeadlessAsync(
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    private void ConfigureSuccessfulPipelineBuild(string pipelineJson)
        => ConfigureLlmResponses(
            intentJson: """
                {"url":"https://jobs.example.com","intent":"Track jobs","changeType":"listing","summary":"Track jobs"}
                """,
            analysisJson: """
                {"contentType":"jobs","regions":["jobs"],"hasPagination":false,"needsJavaScript":false,"recommendedSelector":".job-list","pageSummary":"Jobs"}
                """,
            pipelineJson: pipelineJson);

    private void ConfigureLlmResponses(string intentJson, string analysisJson, string pipelineJson)
    {
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                new LlmResponse { IsSuccess = true, Content = intentJson },
                new LlmResponse { IsSuccess = true, Content = analysisJson },
                new LlmResponse { IsSuccess = true, Content = pipelineJson });
    }
}
