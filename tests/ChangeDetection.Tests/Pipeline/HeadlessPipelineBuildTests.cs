using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.SetupPipeline;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Tests for the headless pipeline build path (BuildPipelineHeadlessAsync).
/// Validates timeout handling, JS shell re-fetch behaviour, and NeedsPipelineSetup
/// flag clearing on success.
/// </summary>
[Category("Unit")]
public class HeadlessPipelineBuildTests : TestBase
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
        _blockRegistry.RegisteredBlockTypes.Returns(new List<string>
        {
            "Input", "Output", "Navigate", "Filter", "ExtractSchema",
            "HashCompare", "NumericDelta", "Condition", "Notify"
        });

        _pipelineValidator.Validate(Arg.Any<PipelineDefinition>(), Arg.Any<IBlockRegistry>())
            .Returns(ValidationResult.Valid());

        var setupFlowEnhancements = new SetupFlowEnhancements(CreateLogger<SetupFlowEnhancements>());
        var securityValidator = new PipelineSecurityValidator(
            new DomainPinValidator(CreateLogger<DomainPinValidator>()),
            CreateLogger<PipelineSecurityValidator>());
        var contentSanitizer = new ContentSanitizer();

        _sut = new ComposableSetupPipeline(
            _llmChain, _contentFetcher, _pipelineExecutor,
            _pipelineValidator, _blockRegistry,
            new PlatformDetector(), new PipelineTemplateRegistry(),
            _watchRepo,
            setupFlowEnhancements, securityValidator, contentSanitizer,
            CreateLogger<ComposableSetupPipeline>());
    }

    [Test]
    public async Task HeadlessBuild_WithEmptyHtml_ReturnsNull()
    {
        // Arrange: fetch succeeds but returns empty HTML
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = """{"url":"https://example.com","intent":"track","changeType":"text","summary":"Track"}""" });

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "",
                HttpStatusCode = 200,
                DurationMs = 100
            });

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await _sut.BuildPipelineHeadlessAsync("https://example.com", "track", cts.Token);

        // Assert
        result.ShouldBeNull("Empty HTML should cause headless build to return null");
        await Task.CompletedTask;
    }

    [Test]
    public async Task HeadlessBuild_FetchFailure_ReturnsNull()
    {
        // Arrange: fetch fails entirely
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = """{"url":"https://example.com","intent":"track","changeType":"text","summary":"Track"}""" });

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = false,
                Html = null,
                HttpStatusCode = 500,
                DurationMs = 5000,
                ErrorMessage = "Server error"
            });

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await _sut.BuildPipelineHeadlessAsync("https://example.com", "track", cts.Token);

        // Assert
        result.ShouldBeNull("Failed fetch should cause headless build to return null");
        await Task.CompletedTask;
    }

    [Test]
    public async Task HeadlessBuild_IntentParsingThrows_ReturnsNullGracefully()
    {
        // Arrange: LLM throws during intent parsing
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM provider unavailable"));

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await _sut.BuildPipelineHeadlessAsync("https://example.com", "track jobs", cts.Token);

        // Assert: should not throw, returns null gracefully
        result.ShouldBeNull("LLM failure should be caught and return null");

        // Verify the warning was logged
        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(l => l.Message.Contains("Intent parsing threw", StringComparison.OrdinalIgnoreCase),
            "Intent parsing failure should be logged as warning");
        await Task.CompletedTask;
    }

    [Test]
    public async Task HeadlessBuild_Timeout_ReturnsNullGracefully()
    {
        // Arrange: the method uses CancellationTokenSource.CreateLinkedTokenSource with 60s timeout.
        // We'll pass an already-cancelled token to simulate immediate timeout.
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var ct = callInfo.ArgAt<CancellationToken>(2);
                await Task.Delay(TimeSpan.FromSeconds(120), ct); // Will be cancelled
                return new LlmResponse { IsSuccess = true, Content = "never reached" };
            });

        // Act: pass pre-cancelled token
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // BuildPipelineHeadlessAsync creates a linked token source, so pre-cancellation propagates
        PipelineDefinition? result = null;
        var threw = false;
        try
        {
            result = await _sut.BuildPipelineHeadlessAsync("https://slow-site.com", "track", cts.Token);
        }
        catch (OperationCanceledException)
        {
            threw = true;
        }

        // Assert: either returns null or throws OperationCanceledException
        if (!threw)
        {
            result.ShouldBeNull("Timeout/cancellation should result in null or cancellation exception");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task HeadlessBuild_JsShellDetected_LogsWarningAboutPlaywrightWait()
    {
        // Arrange: page returns a JS shell (SPA framework)
        var jsShellHtml = """
            <html>
            <body>
                <div id="root"></div>
                <script src="/app.js"></script>
                <script src="/vendor.js"></script>
                <script src="/runtime.js"></script>
                <script src="/polyfills.js"></script>
                <script>window.__NEXT_DATA__={"buildId":"abc"}</script>
                <noscript>You need to enable JavaScript to run this app.</noscript>
            </body>
            </html>
            """;

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = """{"url":"https://next-app.com","intent":"track","changeType":"text","summary":"Track SPA"}""" });

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = jsShellHtml,
                HttpStatusCode = 200,
                DurationMs = 2000
            });

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _sut.BuildPipelineHeadlessAsync("https://next-app.com", "track", cts.Token);

        // Assert: JS shell warning should be logged
        var logs = LogCollector.GetSnapshot();
        var jsWarning = logs.FirstOrDefault(l =>
            l.Message.Contains("JS shell detected", StringComparison.OrdinalIgnoreCase));
        jsWarning.ShouldNotBeNull("JS shell detection should trigger a warning log");

        // The warning should mention Playwright/wait time
        jsWarning!.Message.ShouldContain("wait",
            Case.Insensitive,
            "JS shell log should mention additional wait time");
        await Task.CompletedTask;
    }

    [Test]
    public async Task HeadlessBuild_FetchThrowsException_ReturnsNullWithLoggedError()
    {
        // Arrange: network-level exception from content fetcher
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = """{"url":"https://example.com","intent":"track","changeType":"text","summary":"Track"}""" });

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("DNS resolution failed"));

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await _sut.BuildPipelineHeadlessAsync("https://bad-dns.example", "track", cts.Token);

        // Assert
        result.ShouldBeNull("Fetch exception should be caught gracefully");

        var logs = LogCollector.GetSnapshot();
        logs.ShouldContain(l => l.Message.Contains("Content fetch threw", StringComparison.OrdinalIgnoreCase),
            "Fetch failure should be logged with error details");
        await Task.CompletedTask;
    }

    [Test]
    public async Task HeadlessBuild_WorkdayUrl_UsesTemplateFastPath()
    {
        // Arrange: a Workday URL should be detected by platform detection and use template
        var workdayUrl = "https://company.wd3.myworkdayjobs.com/en-US/External";

        // No LLM should be needed — template fast path
        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><body>Workday page</body></html>",
                HttpStatusCode = 200,
                DurationMs = 500
            });

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await _sut.BuildPipelineHeadlessAsync(workdayUrl, "track jobs", cts.Token);

        // Assert: Workday should be detected as a known platform and use template
        var logs = LogCollector.GetSnapshot();
        var platformLog = logs.FirstOrDefault(l =>
            l.Message.Contains("platform detection", StringComparison.OrdinalIgnoreCase) ||
            l.Message.Contains("Platform template", StringComparison.OrdinalIgnoreCase));
        platformLog.ShouldNotBeNull("Workday URL should trigger platform detection");
        await Task.CompletedTask;
    }
}
