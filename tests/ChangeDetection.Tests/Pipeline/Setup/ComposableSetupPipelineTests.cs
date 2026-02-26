using System.Text.Json;
using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.SetupPipeline;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.Setup;

[Category("Unit")]
public class ComposableSetupPipelineTests : TestBase
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

        // Default block registry behavior
        _blockRegistry.IsRegistered(Arg.Any<string>()).Returns(true);
        _blockRegistry.RegisteredBlockTypes.Returns(new List<string>
        {
            "Input", "Output", "Navigate", "Filter", "ExtractSchema",
            "HashCompare", "NumericDelta", "Condition", "Notify"
        });
        _blockRegistry.GetOutputPorts("Input").Returns(new List<PortDescriptor>
        {
            new() { Name = "url", Type = PortType.Url },
            new() { Name = "config", Type = PortType.Configuration }
        });
        _blockRegistry.GetInputPorts("Navigate").Returns(new List<PortDescriptor>
        {
            new() { Name = "url", Type = PortType.Url }
        });
        _blockRegistry.GetOutputPorts("Navigate").Returns(new List<PortDescriptor>
        {
            new() { Name = "page", Type = PortType.PageReference },
            new() { Name = "html", Type = PortType.HtmlContent }
        });
        _blockRegistry.GetInputPorts("Filter").Returns(new List<PortDescriptor>
        {
            new() { Name = "html", Type = PortType.HtmlContent }
        });
        _blockRegistry.GetOutputPorts("Filter").Returns(new List<PortDescriptor>
        {
            new() { Name = "html", Type = PortType.HtmlContent }
        });
        _blockRegistry.GetInputPorts("ExtractSchema").Returns(new List<PortDescriptor>
        {
            new() { Name = "html", Type = PortType.HtmlContent }
        });
        _blockRegistry.GetOutputPorts("ExtractSchema").Returns(new List<PortDescriptor>
        {
            new() { Name = "data", Type = PortType.ExtractedObjects }
        });
        _blockRegistry.GetInputPorts("Output").Returns(new List<PortDescriptor>
        {
            new() { Name = "data", Type = PortType.ExtractedObjects }
        });
        _blockRegistry.GetOutputPorts("Output").Returns(new List<PortDescriptor>());
        _blockRegistry.GetInputPorts("Input").Returns(new List<PortDescriptor>());
        _blockRegistry.GetInputPorts("HashCompare").Returns(new List<PortDescriptor>
        {
            new() { Name = "data", Type = PortType.ExtractedObjects }
        });
        _blockRegistry.GetOutputPorts("HashCompare").Returns(new List<PortDescriptor>
        {
            new() { Name = "result", Type = PortType.BooleanSignal }
        });

        // Default validator returns valid
        _pipelineValidator.Validate(Arg.Any<PipelineDefinition>(), Arg.Any<IBlockRegistry>())
            .Returns(ChangeDetection.Core.Pipeline.Validation.ValidationResult.Valid());

        var logger = CreateLogger<ComposableSetupPipeline>();
        _sut = new ComposableSetupPipeline(
            _llmChain, _contentFetcher, _pipelineExecutor,
            _pipelineValidator, _blockRegistry, _watchRepo, logger);
    }

    [Test]
    public async Task StartSetupAsync_ParsesIntentAndReachesCheckpoint1()
    {
        // Arrange
        var intentJson = """
            {
                "url": "https://example.com/product",
                "intent": "Track price changes",
                "changeType": "price",
                "summary": "I'll watch example.com for price changes",
                "thresholds": { "maxPrice": "50" },
                "frequency": "30m",
                "notificationPreference": "email"
            }
            """;

        var analysisJson = """
            {
                "contentType": "product",
                "regions": ["price section", "product details"],
                "hasPagination": false,
                "needsJavaScript": true,
                "recommendedSelector": ".price",
                "pageSummary": "A product page with pricing information"
            }
            """;

        SetupLlmResponses(intentJson, analysisJson);

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><div class='price'>$49.99</div></body></html>",
                HttpStatusCode = 200,
                DurationMs = 500
            });

        var request = new SetupRequest { UserInput = "Track price of https://example.com/product and notify me when below $50" };

        // Act
        var progressList = new List<SetupProgress>();
        await foreach (var progress in _sut.StartSetupAsync(request))
        {
            progressList.Add(progress);
            Log($"Phase: {progress.Phase}, Type: {progress.Type}, Message: {progress.Message}");
        }

        // Assert
        progressList.ShouldNotBeEmpty();

        var checkpoint = progressList.Last();
        checkpoint.Phase.ShouldBe(SetupPhase.Checkpoint1);
        checkpoint.Type.ShouldBe(SetupProgressType.CheckpointReached);
        checkpoint.Intent.ShouldNotBeNull();
        checkpoint.Intent.Url.ShouldBe("https://example.com/product");
        checkpoint.Intent.ChangeType.ShouldBe("price");
        checkpoint.Detail!.ShouldContain("Session:");

        // Verify there was thinking/progress before checkpoint
        progressList.ShouldContain(p => p.Type == SetupProgressType.Thinking);
        progressList.ShouldContain(p => p.Phase == SetupPhase.ContentFetching);
        progressList.ShouldContain(p => p.Phase == SetupPhase.ContentAnalysis);

        await Task.CompletedTask;
    }

    [Test]
    public async Task ConfirmIntentAsync_BuildsPipelineAndReachesCheckpoint2()
    {
        // Arrange — first run StartSetup to create a session
        var intentJson = """
            {
                "url": "https://example.com/product",
                "intent": "Track price changes",
                "changeType": "price",
                "summary": "I'll watch example.com for price changes"
            }
            """;

        var analysisJson = """
            {
                "contentType": "product",
                "regions": ["price section"],
                "hasPagination": false,
                "needsJavaScript": false,
                "recommendedSelector": ".price",
                "pageSummary": "Product page"
            }
            """;

        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "config": null, "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "config": { "timeoutSeconds": 30 }, "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "extract-1", "type": "ExtractSchema", "config": null, "position": 3 },
                    { "id": "output-1", "type": "Output", "config": null, "position": 4 }
                ],
                "estimatedLlmCallsPerRun": 1
            }
            """;

        var qcJson = """
            {
                "valid": true,
                "issues": [],
                "suggestions": ["Consider adding a Condition block for threshold alerts"]
            }
            """;

        // First two calls for StartSetup (intent + analysis), next two for ConfirmIntent (pipeline + QC)
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson),
                SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson),
                SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><div class='price'>$49.99</div></body></html>",
                HttpStatusCode = 200,
                DurationMs = 300
            });

        // Mock dry run
        _pipelineExecutor.ExecuteAsync(
                Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(),
                Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult
            {
                Success = true,
                BlockResults = new Dictionary<string, BlockResult>(),
                OutputData = JsonDocument.Parse("""{"price": 49.99}""").RootElement,
                ExecutionDurationMs = 200,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        // Run StartSetup and extract session ID
        var request = new SetupRequest { UserInput = "Track price at https://example.com/product" };
        string? sessionId = null;

        await foreach (var progress in _sut.StartSetupAsync(request))
        {
            if (progress.Phase == SetupPhase.Checkpoint1 && progress.Detail != null)
            {
                sessionId = progress.Detail.Replace("Session: ", "");
            }
        }

        sessionId.ShouldNotBeNull();

        // Act — confirm intent
        var confirmProgress = new List<SetupProgress>();
        await foreach (var progress in _sut.ConfirmIntentAsync(sessionId, confirmed: true))
        {
            confirmProgress.Add(progress);
            Log($"Phase: {progress.Phase}, Type: {progress.Type}, Message: {progress.Message}");
        }

        // Assert
        confirmProgress.ShouldNotBeEmpty();

        var checkpoint2 = confirmProgress.Last();
        checkpoint2.Phase.ShouldBe(SetupPhase.Checkpoint2);
        checkpoint2.Type.ShouldBe(SetupProgressType.CheckpointReached);
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.Pipeline.Blocks.Count.ShouldBeGreaterThan(0);
        checkpoint2.Proposal.HumanSummary.ShouldNotBeNullOrEmpty();
        checkpoint2.Proposal.DryRun.ShouldNotBeNull();
        checkpoint2.Proposal.QcValidation.ShouldNotBeNull();

        confirmProgress.ShouldContain(p => p.Phase == SetupPhase.PipelineBuilding);
        confirmProgress.ShouldContain(p => p.Phase == SetupPhase.DryRun);
        confirmProgress.ShouldContain(p => p.Phase == SetupPhase.AdversarialTest);
        confirmProgress.ShouldContain(p => p.Phase == SetupPhase.QcValidation);
    }

    [Test]
    public async Task ConfirmPipelineAsync_SavesWatch()
    {
        // Arrange — run full flow to checkpoint 2
        var intentJson = """
            {
                "url": "https://example.com/page",
                "intent": "Monitor content changes",
                "changeType": "content"
            }
            """;

        var analysisJson = """
            {
                "contentType": "article",
                "regions": ["main content"],
                "hasPagination": false,
                "needsJavaScript": false,
                "pageSummary": "Article page"
            }
            """;

        var pipelineBlocksJson = """
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
            """;

        var qcJson = """{ "valid": true, "issues": [], "suggestions": [] }""";

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson),
                SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson),
                SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><p>Content</p></body></html>",
                HttpStatusCode = 200,
                DurationMs = 100
            });

        _pipelineExecutor.ExecuteAsync(
                Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(),
                Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult
            {
                Success = true,
                BlockResults = new Dictionary<string, BlockResult>(),
                ExecutionDurationMs = 100,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        // Run through StartSetup → ConfirmIntent to get session ID at checkpoint 2
        var request = new SetupRequest { UserInput = "Watch https://example.com/page for changes" };
        string? sessionId = null;

        await foreach (var p in _sut.StartSetupAsync(request))
        {
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null)
                sessionId = p.Detail.Replace("Session: ", "");
        }

        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
        {
            if (p.Phase == SetupPhase.Checkpoint2 && p.Detail != null)
                sessionId = p.Detail.Replace("Session: ", "");
        }

        sessionId.ShouldNotBeNull();

        // Act — confirm pipeline
        var saveProgress = new List<SetupProgress>();
        await foreach (var progress in _sut.ConfirmPipelineAsync(sessionId, confirmed: true))
        {
            saveProgress.Add(progress);
            Log($"Phase: {progress.Phase}, Type: {progress.Type}, Message: {progress.Message}");
        }

        // Assert
        var completed = saveProgress.Last();
        completed.Phase.ShouldBe(SetupPhase.Saving);
        completed.Type.ShouldBe(SetupProgressType.Completed);
        completed.WatchId.ShouldNotBeNull();

        // Verify WatchedSite was saved
        await _watchRepo.Received(1).InsertAsync(
            Arg.Is<WatchedSite>(w =>
                w.Url == "https://example.com/page" &&
                w.PipelineDefinitionJson != null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartSetupAsync_LlmFails_YieldsFailedProgress()
    {
        // Arrange
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "LLM provider unavailable"
            });

        var request = new SetupRequest { UserInput = "Track something" };

        // Act
        var progressList = new List<SetupProgress>();
        await foreach (var progress in _sut.StartSetupAsync(request))
        {
            progressList.Add(progress);
            Log($"Phase: {progress.Phase}, Type: {progress.Type}, Message: {progress.Message}");
        }

        // Assert
        progressList.ShouldNotBeEmpty();
        var failed = progressList.Last();
        failed.Type.ShouldBe(SetupProgressType.Failed);
        failed.Error.ShouldNotBeNullOrEmpty();

        await Task.CompletedTask;
    }

    [Test]
    public async Task StartSetupAsync_FetchFails_YieldsFailedProgress()
    {
        // Arrange
        var intentJson = """
            {
                "url": "https://broken.example.com",
                "intent": "Track changes",
                "changeType": "content"
            }
            """;

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResponse(intentJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = false,
                ErrorMessage = "DNS resolution failed",
                ErrorCategory = FetchErrorCategory.DnsResolutionFailed
            });

        var request = new SetupRequest { UserInput = "Watch https://broken.example.com" };

        // Act
        var progressList = new List<SetupProgress>();
        await foreach (var progress in _sut.StartSetupAsync(request))
        {
            progressList.Add(progress);
            Log($"Phase: {progress.Phase}, Type: {progress.Type}, Message: {progress.Message}");
        }

        // Assert
        progressList.ShouldNotBeEmpty();
        var failed = progressList.Last();
        failed.Phase.ShouldBe(SetupPhase.ContentFetching);
        failed.Type.ShouldBe(SetupProgressType.Failed);
        failed.Error!.ShouldContain("DNS resolution failed");

        await Task.CompletedTask;
    }

    [Test]
    public async Task ConfirmIntentAsync_WithLargeModel_RunsAdversarialTest()
    {
        // Arrange
        var intentJson = """
            {
                "url": "https://example.com/product",
                "intent": "Track price changes",
                "changeType": "price",
                "summary": "I'll watch example.com for price changes"
            }
            """;

        var analysisJson = """
            {
                "contentType": "product",
                "regions": ["price section"],
                "hasPagination": false,
                "needsJavaScript": false,
                "recommendedSelector": ".price",
                "pageSummary": "Product page"
            }
            """;

        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "config": null, "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "config": { "timeoutSeconds": 30 }, "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "extract-1", "type": "ExtractSchema", "config": null, "position": 3 },
                    { "id": "output-1", "type": "Output", "config": null, "position": 4 }
                ],
                "estimatedLlmCallsPerRun": 1
            }
            """;

        var adversarialJson = """
            {
                "mutations": [
                    { "description": "Price div class renamed", "predictedFragileBlocks": ["filter-1"] },
                    { "description": "Price format changed to EUR", "predictedFragileBlocks": [] },
                    { "description": "Product page completely redesigned", "predictedFragileBlocks": ["filter-1", "extract-1"] }
                ]
            }
            """;

        var qcJson = """
            {
                "valid": true,
                "issues": [],
                "suggestions": [],
                "blockJustifications": {
                    "input-1": "Entry point providing URL",
                    "navigate-1": "Loads the page",
                    "filter-1": "Extracts price region",
                    "extract-1": "Structures price data",
                    "output-1": "Pipeline output"
                },
                "unjustifiedBlocks": []
            }
            """;

        // HasLargeModelAsync returns true to enable adversarial testing
        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(true);

        // 5 LLM calls: intent, analysis, pipeline, adversarial, QC
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson),
                SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson),
                SuccessResponse(adversarialJson),
                SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><div class='price'>$49.99</div></body></html>",
                HttpStatusCode = 200,
                DurationMs = 300
            });

        _pipelineExecutor.ExecuteAsync(
                Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(),
                Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult
            {
                Success = true,
                BlockResults = new Dictionary<string, BlockResult>(),
                OutputData = JsonDocument.Parse("""{"price": 49.99}""").RootElement,
                ExecutionDurationMs = 200,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        // Run StartSetup to get session
        var request = new SetupRequest { UserInput = "Track price at https://example.com/product" };
        string? sessionId = null;

        await foreach (var progress in _sut.StartSetupAsync(request))
        {
            if (progress.Phase == SetupPhase.Checkpoint1 && progress.Detail != null)
                sessionId = progress.Detail.Replace("Session: ", "");
        }

        sessionId.ShouldNotBeNull();

        // Act
        var confirmProgress = new List<SetupProgress>();
        await foreach (var progress in _sut.ConfirmIntentAsync(sessionId, confirmed: true))
        {
            confirmProgress.Add(progress);
            Log($"Phase: {progress.Phase}, Type: {progress.Type}, Message: {progress.Message}");
        }

        // Assert — adversarial test ran and found fragile blocks
        confirmProgress.ShouldContain(p => p.Phase == SetupPhase.AdversarialTest);

        var checkpoint2 = confirmProgress.Last();
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest!.Skipped.ShouldBeFalse();
        checkpoint2.Proposal.AdversarialTest.MutationsTested.ShouldBe(3);
        checkpoint2.Proposal.AdversarialTest.FragileBlocks.ShouldContain("filter-1");

        // Assert — QC has block justifications
        checkpoint2.Proposal.QcValidation.ShouldNotBeNull();
        checkpoint2.Proposal.QcValidation!.BlockJustifications.ShouldNotBeEmpty();
        checkpoint2.Proposal.QcValidation.BlockJustifications.ShouldContainKey("filter-1");
    }

    [Test]
    public async Task ConfirmIntentAsync_WithoutLargeModel_SkipsAdversarialTest()
    {
        // Arrange
        var intentJson = """
            {
                "url": "https://example.com/product",
                "intent": "Track price changes",
                "changeType": "price",
                "summary": "I'll watch example.com for price changes"
            }
            """;

        var analysisJson = """
            {
                "contentType": "product",
                "regions": ["price section"],
                "hasPagination": false,
                "needsJavaScript": false,
                "recommendedSelector": ".price",
                "pageSummary": "Product page"
            }
            """;

        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "config": null, "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "config": { "timeoutSeconds": 30 }, "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "extract-1", "type": "ExtractSchema", "config": null, "position": 3 },
                    { "id": "output-1", "type": "Output", "config": null, "position": 4 }
                ],
                "estimatedLlmCallsPerRun": 1
            }
            """;

        var qcJson = """
            {
                "valid": true,
                "issues": [],
                "suggestions": [],
                "blockJustifications": {
                    "input-1": "Entry point providing URL",
                    "navigate-1": "Loads the page",
                    "filter-1": "Extracts price region",
                    "extract-1": "Structures price data",
                    "output-1": "Pipeline output"
                },
                "unjustifiedBlocks": []
            }
            """;

        // HasLargeModelAsync returns false (default) — adversarial testing will be skipped
        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(false);

        // 4 LLM calls: intent, analysis, pipeline, QC (no adversarial)
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson),
                SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson),
                SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body><div class='price'>$49.99</div></body></html>",
                HttpStatusCode = 200,
                DurationMs = 300
            });

        _pipelineExecutor.ExecuteAsync(
                Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(),
                Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult
            {
                Success = true,
                BlockResults = new Dictionary<string, BlockResult>(),
                OutputData = JsonDocument.Parse("""{"price": 49.99}""").RootElement,
                ExecutionDurationMs = 200,
                WasBaseline = true,
                IsDegraded = false,
                SkippedBlockIds = []
            });

        // Run StartSetup to get session
        var request = new SetupRequest { UserInput = "Track price at https://example.com/product" };
        string? sessionId = null;

        await foreach (var progress in _sut.StartSetupAsync(request))
        {
            if (progress.Phase == SetupPhase.Checkpoint1 && progress.Detail != null)
                sessionId = progress.Detail.Replace("Session: ", "");
        }

        sessionId.ShouldNotBeNull();

        // Act
        var confirmProgress = new List<SetupProgress>();
        await foreach (var progress in _sut.ConfirmIntentAsync(sessionId, confirmed: true))
        {
            confirmProgress.Add(progress);
            Log($"Phase: {progress.Phase}, Type: {progress.Type}, Message: {progress.Message}");
        }

        // Assert — adversarial test was skipped
        confirmProgress.ShouldContain(p => p.Phase == SetupPhase.AdversarialTest);

        var checkpoint2 = confirmProgress.Last();
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest!.Skipped.ShouldBeTrue();
        checkpoint2.Proposal.AdversarialTest.SkipReason.ShouldContain("large model");
    }

    [Test]
    public async Task ConfirmIntentAsync_AdversarialLlmFails_SkipsGracefully()
    {
        // Arrange — adversarial LLM call returns failure
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "output-1", "type": "Output", "position": 3 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var qcJson = """{ "valid": true, "issues": [], "suggestions": [] }""";

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(true);

        // 5 calls: intent, analysis, pipeline, adversarial (FAILS), QC
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson),
                SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson),
                new LlmResponse { IsSuccess = false, ErrorMessage = "Provider overloaded" },
                SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = true, BlockResults = new Dictionary<string, BlockResult>(), ExecutionDurationMs = 100, WasBaseline = true, IsDegraded = false, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
        {
            progress.Add(p);
            Log($"Phase: {p.Phase}, Type: {p.Type}, Message: {p.Message}");
        }

        // Assert — adversarial test should be skipped due to LLM failure, pipeline continues to checkpoint
        var checkpoint2 = progress.Last();
        checkpoint2.Phase.ShouldBe(SetupPhase.Checkpoint2);
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest!.Skipped.ShouldBeTrue();
        checkpoint2.Proposal.AdversarialTest.Passed.ShouldBeTrue();
        checkpoint2.Proposal.AdversarialTest.SkipReason.ShouldContain("failed");
    }

    [Test]
    public async Task ConfirmIntentAsync_AdversarialMalformedJson_SkipsGracefully()
    {
        // Arrange — adversarial LLM returns unparseable content
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "output-1", "type": "Output", "position": 3 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var qcJson = """{ "valid": true, "issues": [], "suggestions": [] }""";

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(true);

        // Adversarial response is valid HTTP success but garbled JSON
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson),
                SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson),
                SuccessResponse("Sure! Here are some mutations that could break the pipeline..."),
                SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = true, BlockResults = new Dictionary<string, BlockResult>(), ExecutionDurationMs = 100, WasBaseline = true, IsDegraded = false, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
        {
            progress.Add(p);
            Log($"Phase: {p.Phase}, Type: {p.Type}, Message: {p.Message}");
        }

        // Assert — deserialization failure results in skip, not crash
        var checkpoint2 = progress.Last();
        checkpoint2.Phase.ShouldBe(SetupPhase.Checkpoint2);
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest!.Skipped.ShouldBeTrue();
        checkpoint2.Proposal.AdversarialTest.Passed.ShouldBeTrue();
        checkpoint2.Proposal.AdversarialTest.SkipReason.ShouldContain("parse");
    }

    [Test]
    public async Task ConfirmIntentAsync_AdversarialNoFragileBlocks_Passes()
    {
        // Arrange — adversarial test finds no fragile blocks (all mutations are safe)
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "output-1", "type": "Output", "position": 3 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var adversarialJson = """
            {
                "mutations": [
                    { "description": "Price div class renamed", "predictedFragileBlocks": [] },
                    { "description": "Price format changed", "predictedFragileBlocks": [] },
                    { "description": "Page redesigned", "predictedFragileBlocks": [] }
                ]
            }
            """;
        var qcJson = """{ "valid": true, "issues": [], "suggestions": [] }""";

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(true);
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson), SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson), SuccessResponse(adversarialJson), SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = true, BlockResults = new Dictionary<string, BlockResult>(), ExecutionDurationMs = 100, WasBaseline = true, IsDegraded = false, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
            progress.Add(p);

        // Assert — all mutations passed, no fragile blocks
        var checkpoint2 = progress.Last();
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest!.Skipped.ShouldBeFalse();
        checkpoint2.Proposal.AdversarialTest.Passed.ShouldBeTrue();
        checkpoint2.Proposal.AdversarialTest.MutationsTested.ShouldBe(3);
        checkpoint2.Proposal.AdversarialTest.MutationsPassed.ShouldBe(3);
        checkpoint2.Proposal.AdversarialTest.FragileBlocks.ShouldBeEmpty();
        checkpoint2.Proposal.AdversarialTest.Warnings.ShouldBeEmpty();
    }

    [Test]
    public async Task ConfirmIntentAsync_DryRunFails_SkipsAdversarialTest()
    {
        // Arrange — dry run fails, adversarial test should be skipped
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "output-1", "type": "Output", "position": 3 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var qcJson = """{ "valid": true, "issues": [], "suggestions": [] }""";

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(true);

        // 4 LLM calls: intent, analysis, pipeline, QC (no adversarial since dry run fails)
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson), SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson), SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        // Dry run FAILS
        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = false, BlockResults = new Dictionary<string, BlockResult>(), ExecutionDurationMs = 100, WasBaseline = false, IsDegraded = true, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
            progress.Add(p);

        // Assert — adversarial test skipped because dry run failed
        var checkpoint2 = progress.Last();
        checkpoint2.Phase.ShouldBe(SetupPhase.Checkpoint2);
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest.ShouldNotBeNull();
        checkpoint2.Proposal.AdversarialTest!.Skipped.ShouldBeTrue();
        checkpoint2.Proposal.AdversarialTest.Passed.ShouldBeTrue();
        checkpoint2.Proposal.AdversarialTest.SkipReason.ShouldContain("Dry run");
    }

    [Test]
    public async Task ConfirmIntentAsync_QcWithUnjustifiedBlocks_IncludesInProposal()
    {
        // Arrange — QC returns unjustified blocks
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "filter-2", "type": "Filter", "config": { "cssSelector": ".sidebar" }, "position": 3 },
                    { "id": "output-1", "type": "Output", "position": 4 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var qcJson = """
            {
                "valid": true,
                "issues": [],
                "suggestions": ["Remove unnecessary sidebar filter"],
                "blockJustifications": {
                    "input-1": "Entry point",
                    "navigate-1": "Loads the page",
                    "filter-1": "Extracts price region",
                    "output-1": "Pipeline output"
                },
                "unjustifiedBlocks": ["filter-2"]
            }
            """;

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(false);
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson), SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson), SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = true, BlockResults = new Dictionary<string, BlockResult>(), ExecutionDurationMs = 100, WasBaseline = true, IsDegraded = false, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
            progress.Add(p);

        // Assert — QC validation includes justifications and unjustified blocks
        var checkpoint2 = progress.Last();
        checkpoint2.Phase.ShouldBe(SetupPhase.Checkpoint2);
        checkpoint2.Proposal.ShouldNotBeNull();
        checkpoint2.Proposal.QcValidation.ShouldNotBeNull();
        checkpoint2.Proposal.QcValidation!.BlockJustifications.ShouldContainKey("filter-1");
        checkpoint2.Proposal.QcValidation.BlockJustifications.ShouldNotContainKey("filter-2");
        checkpoint2.Proposal.QcValidation.UnjustifiedBlocks.ShouldContain("filter-2");
        checkpoint2.Proposal.QcValidation.UnjustifiedBlocks.Count.ShouldBe(1);
    }

    [Test]
    public async Task ConfirmIntentAsync_AdversarialFragileBlockDeduplication_Works()
    {
        // Arrange — multiple mutations report same fragile block
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "output-1", "type": "Output", "position": 3 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var adversarialJson = """
            {
                "mutations": [
                    { "description": "CSS class renamed", "predictedFragileBlocks": ["filter-1"] },
                    { "description": "ID attribute removed", "predictedFragileBlocks": ["filter-1"] },
                    { "description": "Page restructured", "predictedFragileBlocks": ["filter-1", "navigate-1"] }
                ]
            }
            """;
        var qcJson = """{ "valid": true, "issues": [], "suggestions": [] }""";

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(true);
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson), SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson), SuccessResponse(adversarialJson), SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = true, BlockResults = new Dictionary<string, BlockResult>(), OutputData = JsonDocument.Parse("""{"price": 9.99}""").RootElement, ExecutionDurationMs = 100, WasBaseline = true, IsDegraded = false, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
            progress.Add(p);

        // Assert — filter-1 appears in all 3 mutations but should be deduplicated
        var adversarial = progress.Last().Proposal!.AdversarialTest!;
        adversarial.Skipped.ShouldBeFalse();
        adversarial.Passed.ShouldBeFalse();
        adversarial.MutationsTested.ShouldBe(3);
        adversarial.MutationsPassed.ShouldBe(0);
        adversarial.FragileBlocks.Count.ShouldBe(2); // filter-1 and navigate-1 (deduplicated)
        adversarial.FragileBlocks.ShouldContain("filter-1");
        adversarial.FragileBlocks.ShouldContain("navigate-1");
        adversarial.Warnings.Count.ShouldBe(3); // one warning per mutation with fragile blocks
    }

    [Test]
    public async Task ConfirmIntentAsync_HumanSummary_IncludesAdversarialResults()
    {
        // Arrange — adversarial test finds fragile blocks, summary should mention them
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "output-1", "type": "Output", "position": 3 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var adversarialJson = """
            {
                "mutations": [
                    { "description": "CSS class renamed", "predictedFragileBlocks": ["filter-1"] },
                    { "description": "Format changed", "predictedFragileBlocks": [] }
                ]
            }
            """;
        var qcJson = """
            {
                "valid": true, "issues": [], "suggestions": ["Add threshold check"],
                "blockJustifications": { "input-1": "Entry", "navigate-1": "Load", "filter-1": "Extract", "output-1": "Output" },
                "unjustifiedBlocks": []
            }
            """;

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(true);
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson), SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson), SuccessResponse(adversarialJson), SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = true, BlockResults = new Dictionary<string, BlockResult>(), OutputData = JsonDocument.Parse("""{"price": 9.99}""").RootElement, ExecutionDurationMs = 100, WasBaseline = true, IsDegraded = false, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
            progress.Add(p);

        // Assert — HumanSummary includes adversarial test results and QC suggestions
        var summary = progress.Last().Proposal!.HumanSummary;
        summary.ShouldContain("Adversarial test");
        summary.ShouldContain("fragile");
        summary.ShouldContain("filter-1");
        summary.ShouldContain("Quality check");
        summary.ShouldContain("Suggestions");
        summary.ShouldContain("Add threshold check");
    }

    [Test]
    public async Task ConfirmIntentAsync_HumanSummary_IncludesUnjustifiedBlocks()
    {
        // Arrange — QC returns unjustified blocks, summary should display warning
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "filter-2", "type": "Filter", "config": { "cssSelector": ".ads" }, "position": 3 },
                    { "id": "output-1", "type": "Output", "position": 4 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var qcJson = """
            {
                "valid": true, "issues": [], "suggestions": [],
                "blockJustifications": { "input-1": "Entry", "navigate-1": "Load", "filter-1": "Extract price", "output-1": "Output" },
                "unjustifiedBlocks": ["filter-2"]
            }
            """;

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(false);
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson), SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson), SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = true, BlockResults = new Dictionary<string, BlockResult>(), ExecutionDurationMs = 100, WasBaseline = true, IsDegraded = false, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
            progress.Add(p);

        // Assert — HumanSummary includes unjustified blocks warning
        var summary = progress.Last().Proposal!.HumanSummary;
        summary.ShouldContain("Unjustified blocks");
        summary.ShouldContain("filter-2");
    }

    [Test]
    public async Task ConfirmIntentAsync_HumanSummary_SkippedAdversarialShowsReason()
    {
        // Arrange — adversarial test skipped, summary should show skip reason
        var intentJson = """{ "url": "https://example.com/p", "intent": "Track price", "changeType": "price", "summary": "Watch price" }""";
        var analysisJson = """{ "contentType": "product", "regions": ["price"], "hasPagination": false, "needsJavaScript": false, "recommendedSelector": ".price", "pageSummary": "Product" }""";
        var pipelineBlocksJson = """
            {
                "blocks": [
                    { "id": "input-1", "type": "Input", "position": 0 },
                    { "id": "navigate-1", "type": "Navigate", "position": 1 },
                    { "id": "filter-1", "type": "Filter", "config": { "cssSelector": ".price" }, "position": 2 },
                    { "id": "output-1", "type": "Output", "position": 3 }
                ],
                "estimatedLlmCallsPerRun": 0
            }
            """;
        var qcJson = """{ "valid": true, "issues": [], "suggestions": [] }""";

        _llmChain.HasLargeModelAsync(Arg.Any<CancellationToken>()).Returns(false);
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(
                SuccessResponse(intentJson), SuccessResponse(analysisJson),
                SuccessResponse(pipelineBlocksJson), SuccessResponse(qcJson));

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = true, Html = "<html><body><div class='price'>$9.99</div></body></html>", HttpStatusCode = 200, DurationMs = 100 });

        _pipelineExecutor.ExecuteAsync(Arg.Any<PipelineDefinition>(), Arg.Any<Guid>(), Arg.Any<IBlockStateStore>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineExecutionResult { Success = true, BlockResults = new Dictionary<string, BlockResult>(), ExecutionDurationMs = 100, WasBaseline = true, IsDegraded = false, SkippedBlockIds = [] });

        var request = new SetupRequest { UserInput = "Track price at https://example.com/p" };
        string? sessionId = null;
        await foreach (var p in _sut.StartSetupAsync(request))
            if (p.Phase == SetupPhase.Checkpoint1 && p.Detail != null) sessionId = p.Detail.Replace("Session: ", "");

        // Act
        var progress = new List<SetupProgress>();
        await foreach (var p in _sut.ConfirmIntentAsync(sessionId!, confirmed: true))
            progress.Add(p);

        // Assert — HumanSummary shows adversarial test was skipped with reason
        var summary = progress.Last().Proposal!.HumanSummary;
        summary.ShouldContain("Adversarial test");
        summary.ShouldContain("Skipped");
        summary.ShouldContain("large model");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static LlmResponse SuccessResponse(string content) => new()
    {
        IsSuccess = true,
        Content = content,
        ProviderUsed = "test",
        Model = "test-model",
        DurationMs = 100
    };

    private void SetupLlmResponses(params string[] responses)
    {
        var returnValues = responses.Select(SuccessResponse).ToArray();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(returnValues[0], returnValues.Skip(1).ToArray());
    }
}
