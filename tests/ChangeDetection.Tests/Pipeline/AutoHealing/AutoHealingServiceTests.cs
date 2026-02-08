using System.Text.Json;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.AutoHealing;
using ChangeDetection.Services.AutoHealing;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline.AutoHealing;

[Category("Unit")]
public class AutoHealingServiceTests
{
    private readonly ILlmProviderChain _llmChain = Substitute.For<ILlmProviderChain>();
    private readonly IContentFetcher _contentFetcher = Substitute.For<IContentFetcher>();
    private readonly FakeLogger<AutoHealingService> _logger = new(FakeLogCollector.Create(new FakeLogCollectorOptions()));
    private readonly AutoHealingService _sut;

    public AutoHealingServiceTests()
    {
        _sut = new AutoHealingService(_llmChain, _contentFetcher, _logger);
    }

    private static PipelineDefinition CreateTestPipeline(string blockId = "filter-1", string blockType = "Filter", JsonElement? config = null)
    {
        return new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition
                {
                    Id = "input-1",
                    Type = "Input",
                    Config = JsonDocument.Parse("""{"url": "https://example.com"}""").RootElement
                },
                new BlockDefinition
                {
                    Id = blockId,
                    Type = blockType,
                    Config = config ?? JsonDocument.Parse("""{"selector": ".old-class"}""").RootElement
                }
            ],
            Connections =
            [
                new ConnectionDefinition
                {
                    FromBlockId = "input-1",
                    FromPort = "html",
                    ToBlockId = blockId,
                    ToPort = "html"
                }
            ]
        };
    }

    private static HealingContext CreateContext(int consecutiveFailures, PipelineDefinition? pipeline = null, string? currentHtml = null, string? setupHtml = null)
    {
        pipeline ??= CreateTestPipeline();
        return new HealingContext
        {
            WatchId = Guid.NewGuid(),
            BlockInstanceId = "filter-1",
            BlockType = "Filter",
            ErrorMessage = "CSS selector '.old-class' returned empty result",
            ConsecutiveFailures = consecutiveFailures,
            Pipeline = pipeline,
            CurrentHtml = currentHtml,
            SetupTimeHtml = setupHtml
        };
    }

    [Test]
    public async Task AttemptHealAsync_BelowThreshold_ReturnsNoAction()
    {
        var context = CreateContext(consecutiveFailures: 1);

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.NoActionNeeded);
        result.Message.ShouldContain("Below failure threshold");
        result.UpdatedBlock.ShouldBeNull();
        result.UpdatedPipeline.ShouldBeNull();
    }

    [Test]
    public async Task AttemptHealAsync_AtThreshold_TriggersLayer1()
    {
        var context = CreateContext(
            consecutiveFailures: 3,
            currentHtml: "<html><div class='new-class'>Content</div></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """{"newConfig": {"selector": ".new-class"}}"""
            });

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.Healed);
        result.Message.ShouldBe("Block config updated by LLM");
        result.UpdatedBlock.ShouldNotBeNull();
        result.UpdatedPipeline.ShouldNotBeNull();
    }

    [Test]
    public async Task AttemptHealAsync_Layer1_LlmSuggestsNewConfig_ReturnsHealed()
    {
        var context = CreateContext(
            consecutiveFailures: 4,
            currentHtml: "<html><div class='updated-selector'>Data</div></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """{"newConfig": {"selector": ".updated-selector"}}"""
            });

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.Healed);
        result.UpdatedBlock.ShouldNotBeNull();
        result.UpdatedBlock!.Id.ShouldBe("filter-1");
        result.UpdatedBlock.Config.ShouldNotBeNull();
        result.UpdatedPipeline.ShouldNotBeNull();
        result.UpdatedPipeline!.Blocks.Count.ShouldBe(2);
    }

    [Test]
    public async Task AttemptHealAsync_Layer1_LlmFails_ReturnsRequiresUser()
    {
        var context = CreateContext(
            consecutiveFailures: 3,
            currentHtml: "<html><div>Content</div></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "Provider unavailable"
            });

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.RequiresUser);
        result.Message.ShouldContain("LLM failed to suggest fix");
    }

    [Test]
    public async Task AttemptHealAsync_Layer1_NoHtml_FetchesCurrent()
    {
        var context = CreateContext(consecutiveFailures: 3, currentHtml: null);

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><div class='fetched'>Content</div></html>"
            });

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """{"newConfig": {"selector": ".fetched"}}"""
            });

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.Healed);
        await _contentFetcher.Received(1).FetchAsync("https://example.com", Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AttemptHealAsync_Layer1_NoHtmlAndFetchFails_ReturnsRequiresUser()
    {
        var context = CreateContext(consecutiveFailures: 3, currentHtml: null);

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = false, ErrorMessage = "Timeout" });

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.RequiresUser);
        result.Message.ShouldContain("Cannot fetch current page content");
    }

    [Test]
    public async Task AttemptHealAsync_Layer1_InvalidJsonResponse_ReturnsRequiresUser()
    {
        var context = CreateContext(
            consecutiveFailures: 3,
            currentHtml: "<html><div>Content</div></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = "This is not valid JSON"
            });

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.RequiresUser);
        result.Message.ShouldContain("did not contain valid fix");
    }

    [Test]
    public async Task AttemptHealAsync_Layer1_LlmThrows_ReturnsRequiresUser()
    {
        var context = CreateContext(
            consecutiveFailures: 3,
            currentHtml: "<html><div>Content</div></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Connection refused"));

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.RequiresUser);
        result.Message.ShouldContain("Connection refused");
    }

    [Test]
    public async Task AttemptHealAsync_Layer2_DiagnosesAndFixes_ReturnsDiagnosedFixable()
    {
        var context = CreateContext(
            consecutiveFailures: 6,
            currentHtml: "<html><div class='v2-content'>New Data</div></html>",
            setupHtml: "<html><div class='old-class'>Original Data</div></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """{"diagnosis": "Site redesigned, class renamed from old-class to v2-content", "newConfig": {"selector": ".v2-content"}}"""
            });

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.DiagnosedFixable);
        result.Message.ShouldContain("redesigned");
        result.UpdatedBlock.ShouldNotBeNull();
        result.UpdatedPipeline.ShouldNotBeNull();
    }

    [Test]
    public async Task AttemptHealAsync_Layer2_DiagnosisOnly_ReturnsRequiresUser()
    {
        var context = CreateContext(
            consecutiveFailures: 6,
            currentHtml: "<html>Completely different</html>",
            setupHtml: "<html><div class='old-class'>Original</div></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """{"diagnosis": "The entire page structure has been replaced with a login wall"}"""
            });

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.RequiresUser);
        result.Message.ShouldContain("login wall");
    }

    [Test]
    public async Task AttemptHealAsync_Layer2_MissingSetupHtml_ReturnsRequiresUser()
    {
        var context = CreateContext(
            consecutiveFailures: 6,
            currentHtml: "<html>Current</html>",
            setupHtml: null);

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.RequiresUser);
        result.Message.ShouldContain("missing");
    }

    [Test]
    public async Task AttemptHealAsync_ExceedsAllThresholds_ReturnsRequiresUser()
    {
        var context = CreateContext(consecutiveFailures: 10);

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.RequiresUser);
        result.Message.ShouldContain("Automatic healing exhausted");
        result.Message.ShouldContain("10");
    }

    [Test]
    public async Task AttemptHealAsync_BlockNotInPipeline_ReturnsRequiresUser()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition
                {
                    Id = "input-1",
                    Type = "Input",
                    Config = JsonDocument.Parse("""{"url": "https://example.com"}""").RootElement
                }
            ],
            Connections = []
        };

        var context = new HealingContext
        {
            WatchId = Guid.NewGuid(),
            BlockInstanceId = "nonexistent-block",
            BlockType = "Filter",
            ErrorMessage = "Block failed",
            ConsecutiveFailures = 3,
            Pipeline = pipeline,
            CurrentHtml = "<html></html>"
        };

        var result = await _sut.AttemptHealAsync(context);

        result.Outcome.ShouldBe(HealingOutcome.RequiresUser);
        result.Message.ShouldContain("Block not found");
    }

    [Test]
    public void FindUrlFromPipeline_FindsUrlFromInputBlock()
    {
        var pipeline = CreateTestPipeline();
        var url = AutoHealingService.FindUrlFromPipeline(pipeline);
        url.ShouldBe("https://example.com");
    }

    [Test]
    public void FindUrlFromPipeline_ReturnsNullWhenNoUrl()
    {
        var pipeline = new PipelineDefinition
        {
            SchemaVersion = 1,
            Blocks =
            [
                new BlockDefinition { Id = "filter-1", Type = "Filter" }
            ],
            Connections = []
        };

        var url = AutoHealingService.FindUrlFromPipeline(pipeline);
        url.ShouldBeNull();
    }
}
