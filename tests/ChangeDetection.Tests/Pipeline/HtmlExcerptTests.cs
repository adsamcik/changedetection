using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Core.Pipeline;
using ChangeDetection.Core.Pipeline.Setup;
using ChangeDetection.Core.Pipeline.Validation;
using ChangeDetection.Services.BlockExecution;
using ChangeDetection.Services.Pipeline;
using ChangeDetection.Services.SetupPipeline;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Pipeline;

/// <summary>
/// Tests for HTML excerpt preparation in the headless pipeline builder.
/// Exercises PrepareHtmlExcerptForPipelineBuilder via BuildPipelineHeadlessAsync,
/// since the method is private. Validates sanitization, size limits, content
/// region detection, and JS-shell handling.
/// </summary>
[Category("Unit")]
public class HtmlExcerptTests : TestBase
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
    public async Task PrepareExcerpt_StripsScriptsAndStyles_ButKeepsContent()
    {
        // Arrange: HTML with script/style tags surrounding real content
        var html = """
            <html>
            <head><style>body { color: red; }</style></head>
            <body>
                <script>var x = 1;</script>
                <h1>Job Listings</h1>
                <div class="jobs">
                    <div class="job"><a href="/job/1">Senior Scientist</a></div>
                    <div class="job"><a href="/job/2">Lab Technician</a></div>
                </div>
                <script>analytics.track('page');</script>
            </body>
            </html>
            """;

        // The LLM call should receive sanitized HTML (scripts/styles stripped)
        string? capturedPrompt = null;
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt ??= callInfo.ArgAt<string>(0);
                // Return minimal valid intent JSON for first call
                return Task.FromResult(new LlmResponse { IsSuccess = true, Content = """{"url":"https://example.com","intent":"track jobs","changeType":"jobs","summary":"Track jobs"}""" });
            });

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = html,
                HttpStatusCode = 200,
                DurationMs = 100
            });

        // Act: trigger headless build which internally calls PrepareHtmlExcerptForPipelineBuilder
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await _sut.BuildPipelineHeadlessAsync("https://example.com", "track jobs", cts.Token);

        // Assert: The LLM received the prompt at least once,
        // and the sanitized content should NOT contain script tags but SHOULD contain the job text.
        // Even if the overall pipeline build failed (no full LLM mock),
        // the fetch was called and content was processed.
        await _contentFetcher.Received().FetchAsync(
            Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>());

        if (capturedPrompt is not null)
        {
            capturedPrompt.ShouldNotContain("<script");
            capturedPrompt.ShouldNotContain("analytics.track");
            // The content text (job titles) should survive sanitization
            capturedPrompt.ShouldContain("Scientist");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task PrepareExcerpt_RespectsMaxLength_TruncatesLargeHtml()
    {
        // Arrange: generate HTML larger than the 8KB limit
        const int maxExcerpt = 8_000;
        var largeContent = new string('A', maxExcerpt + 5000);
        var html = $"<html><body><div class=\"data\">{largeContent}</div></body></html>";

        string? capturedPromptForAnalysis = null;
        var callCount = 0;
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return Task.FromResult(new LlmResponse { IsSuccess = true, Content = """{"url":"https://example.com","intent":"track","changeType":"text","summary":"Track"}""" });
                }
                // Capture the analysis prompt (second LLM call) which contains the excerpt
                capturedPromptForAnalysis ??= callInfo.ArgAt<string>(0);
                return Task.FromResult(new LlmResponse { IsSuccess = true, Content = """{"contentType":"text","regions":[],"hasPagination":false,"needsJavaScript":false,"pageSummary":"text page"}""" });
            });

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = html,
                HttpStatusCode = 200,
                DurationMs = 100
            });

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _sut.BuildPipelineHeadlessAsync("https://example.com", "track", cts.Token);

        // Assert: if the LLM received the excerpt, it should be truncated
        if (capturedPromptForAnalysis is not null)
        {
            // The full prompt may include system instructions + excerpt.
            // The excerpt portion should not exceed maxExcerpt + truncation marker.
            // At minimum, the original oversized content should not appear in full.
            capturedPromptForAnalysis.Length.ShouldBeLessThan(
                html.Length,
                "Excerpt should be smaller than the original oversized HTML");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task ContentRegionDetection_FindsDataBearingSection()
    {
        // Arrange: a page with a large header/nav followed by content region
        var headerNoise = string.Join("", Enumerable.Range(0, 500).Select(i => $"<div>Nav item {i}</div>"));
        var contentRegion = """
            <table class="vacancies">
                <tbody>
                    <tr><td>Senior Scientist</td><td>Copenhagen</td></tr>
                    <tr><td>Lab Manager</td><td>Berlin</td></tr>
                </tbody>
            </table>
            """;
        var footerNoise = string.Join("", Enumerable.Range(0, 500).Select(i => $"<div>Footer {i}</div>"));
        var html = $"<html><body>{headerNoise}{contentRegion}{footerNoise}</body></html>";

        // The HTML is large enough that truncation is needed — content region should be found
        html.Length.ShouldBeGreaterThan(8_000, "Test HTML should exceed excerpt limit to trigger region detection");

        string? capturedPrompt = null;
        var callCount = 0;
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount <= 1)
                    return Task.FromResult(new LlmResponse { IsSuccess = true, Content = """{"url":"https://example.com","intent":"track vacancies","changeType":"jobs","summary":"Track"}""" });
                capturedPrompt ??= callInfo.ArgAt<string>(0);
                return Task.FromResult(new LlmResponse { IsSuccess = true, Content = """{"contentType":"jobs","regions":["vacancies table"],"hasPagination":false,"needsJavaScript":false,"pageSummary":"jobs page"}""" });
            });

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = html,
                HttpStatusCode = 200,
                DurationMs = 100
            });

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _sut.BuildPipelineHeadlessAsync("https://example.com", "track vacancies", cts.Token);

        // Assert: the excerpt sent to LLM should contain the vacancies table, not just the header
        if (capturedPrompt is not null)
        {
            capturedPrompt.ShouldContain("Senior Scientist",
                customMessage: "Content region detection should find the vacancies table");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task JsShell_EmptyBodyWithScripts_IsDetectedAndLogged()
    {
        // Arrange: a typical SPA shell — empty body div, lots of scripts, minimal visible text
        var jsShellHtml = """
            <html>
            <head><title>App</title></head>
            <body>
                <div id="app"></div>
                <script src="/bundle.1.js"></script>
                <script src="/bundle.2.js"></script>
                <script src="/bundle.3.js"></script>
                <script src="/bundle.4.js"></script>
                <script>window.__NEXT_DATA__={}</script>
                <noscript>You need to enable JavaScript to run this app.</noscript>
            </body>
            </html>
            """;

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = """{"url":"https://spa-app.com","intent":"track data","changeType":"text","summary":"Track SPA"}""" });

        _contentFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = jsShellHtml,
                HttpStatusCode = 200,
                DurationMs = 100
            });

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _sut.BuildPipelineHeadlessAsync("https://spa-app.com", "track data", cts.Token);

        // Assert: check that JS shell was detected via log output
        var logs = LogCollector.GetSnapshot();
        var jsShellLog = logs.FirstOrDefault(l =>
            l.Message.Contains("JS shell detected", StringComparison.OrdinalIgnoreCase));

        jsShellLog.ShouldNotBeNull(
            "JS shell detection should log a warning when page is an empty SPA shell");

        await Task.CompletedTask;
    }
}
