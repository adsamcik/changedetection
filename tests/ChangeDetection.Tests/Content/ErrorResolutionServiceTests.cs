using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for ErrorResolutionService LLM-based error resolution.
/// Uses NSubstitute mocking - no real LLM calls are made.
/// </summary>
[Category("Unit")]
public class ErrorResolutionServiceTests : TestBase
{
    private readonly ILlmProviderChain _llmChain;
    private readonly IContentExtractor _contentExtractor;
    private readonly ErrorResolutionService _sut;

    public ErrorResolutionServiceTests()
    {
        _llmChain = Substitute.For<ILlmProviderChain>();
        _contentExtractor = Substitute.For<IContentExtractor>();
        _sut = new ErrorResolutionService(_llmChain, _contentExtractor, CreateLogger<ErrorResolutionService>());
    }

    private static WatchedSite CreateTestWatch(string? cssSelector = null, string? xpathSelector = null) => new()
    {
        Url = "https://example.com/products",
        Name = "Test Watch",
        Description = "Monitor product prices",
        CssSelector = cssSelector,
        XPathSelector = xpathSelector
    };

    private static ErrorResolutionContext CreateTestContext(
        WatchedSite? watch = null,
        string? html = null,
        string? errorMessage = null,
        ErrorType errorType = ErrorType.SelectorNoMatch,
        int consecutiveFailures = 1) => new()
    {
        Watch = watch ?? CreateTestWatch(cssSelector: ".price"),
        CurrentHtml = html ?? "<html><body><div class='product-price'>$29.99</div></body></html>",
        ErrorMessage = errorMessage ?? "Selector '.price' matched no elements",
        ErrorType = errorType,
        PreviousContent = "Price: $24.99",
        ConsecutiveFailures = consecutiveFailures
    };

    [Test]
    public async Task Constructor_WithValidDependencies_CreatesService()
    {
        // The service was created in the constructor without throwing
        _sut.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task TryResolveAsync_WithValidInput_ReturnsDiagnosisAndNewSelector()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "diagnosis": "The CSS class was renamed from 'price' to 'product-price'",
                    "majorStructureChange": false,
                    "newCssSelector": ".product-price",
                    "newXPathSelector": null,
                    "confidence": 0.92,
                    "reasoning": "The page still has a price element but the class name changed",
                    "suggestedAction": "Update the CSS selector to .product-price"
                }
                """
            });

        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("$29.99");

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.IsResolved.ShouldBeTrue();
        result.Diagnosis.ShouldContain("product-price");
        result.NewCssSelector.ShouldBe(".product-price");
        result.Confidence.ShouldBeGreaterThanOrEqualTo(0.85f);
        result.MajorStructureChange.ShouldBeFalse();
        result.AutoFixApplied.ShouldBeTrue();
        result.RequiresUserApproval.ShouldBeFalse();
    }

    [Test]
    public async Task TryResolveAsync_WhenLlmUnavailable_ReturnsFailedResult()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "LLM service unavailable"
            });

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.IsResolved.ShouldBeFalse();
        result.AutoFixApplied.ShouldBeFalse();
        result.Diagnosis.ShouldContain("LLM unavailable");
        result.RequiresUserApproval.ShouldBeTrue();
    }

    [Test]
    public async Task TryResolveAsync_WhenLlmReturnsEmptyContent_ReturnsFailedResult()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = ""
            });

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.IsResolved.ShouldBeFalse();
        result.Diagnosis.ShouldContain("LLM unavailable");
    }

    [Test]
    public async Task TryResolveAsync_WithLowConfidence_RequiresUserApproval()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "diagnosis": "Page structure changed significantly",
                    "majorStructureChange": false,
                    "newCssSelector": ".maybe-price",
                    "newXPathSelector": null,
                    "confidence": 0.5,
                    "reasoning": "Not very sure about this selector",
                    "suggestedAction": "Manually verify the selector"
                }
                """
            });

        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("Some content");

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.Confidence.ShouldBeLessThan(0.85f);
        result.RequiresUserApproval.ShouldBeTrue();
        result.AutoFixApplied.ShouldBeFalse();
    }

    [Test]
    public async Task TryResolveAsync_WithMajorStructureChange_RequiresUserApproval()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "diagnosis": "Website was completely redesigned",
                    "majorStructureChange": true,
                    "newCssSelector": ".new-price-widget span",
                    "newXPathSelector": null,
                    "confidence": 0.9,
                    "reasoning": "The entire page layout has changed",
                    "suggestedAction": "Review the new page structure"
                }
                """
            });

        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("$29.99");

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.MajorStructureChange.ShouldBeTrue();
        result.RequiresUserApproval.ShouldBeTrue();
        result.AutoFixApplied.ShouldBeFalse();
    }

    [Test]
    public async Task TryResolveAsync_WhenLlmThrowsException_ReturnsFailedResult()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Connection refused"));

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.IsResolved.ShouldBeFalse();
        result.AutoFixApplied.ShouldBeFalse();
        result.Diagnosis.ShouldContain("Resolution failed");
    }

    [Test]
    public async Task TryResolveAsync_WhenLlmReturnsInvalidJson_ReturnsFailedResult()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = "This is not JSON at all, just plain text with no braces"
            });

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.IsResolved.ShouldBeFalse();
        result.Diagnosis.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task TryResolveAsync_WithXPathSelector_ReturnsXPathFix()
    {
        // Arrange
        var watch = CreateTestWatch(xpathSelector: "//div[@class='price']");
        var context = CreateTestContext(
            watch: watch,
            html: "<html><body><span id='product-price'>$19.99</span></body></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "diagnosis": "Price element changed from div to span with different id",
                    "majorStructureChange": false,
                    "newCssSelector": null,
                    "newXPathSelector": "//span[@id='product-price']",
                    "confidence": 0.88,
                    "reasoning": "Element type and identifier changed",
                    "suggestedAction": "Update XPath selector"
                }
                """
            });

        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("$19.99");

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.IsResolved.ShouldBeTrue();
        result.NewXPathSelector.ShouldBe("//span[@id='product-price']");
        result.NewCssSelector.ShouldBeNull();
    }

    [Test]
    public async Task TryResolveAsync_WhenProposedSelectorMatchesNothing_ReturnsUnresolved()
    {
        // Arrange
        var context = CreateTestContext(html: "<html><body><p>No matching elements</p></body></html>");

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "diagnosis": "Element moved to a new location",
                    "majorStructureChange": false,
                    "newCssSelector": ".nonexistent-class",
                    "newXPathSelector": null,
                    "confidence": 0.7,
                    "reasoning": "Guessing at new selector",
                    "suggestedAction": "Check page manually"
                }
                """
            });

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.IsResolved.ShouldBeFalse();
        result.AutoFixApplied.ShouldBeFalse();
        result.RequiresUserApproval.ShouldBeTrue();
    }

    [Test]
    public async Task TryResolveAsync_WithNoDiagnosisFields_UsesDefaults()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                }
                """
            });

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.Diagnosis.ShouldBe("Unknown issue");
        result.IsResolved.ShouldBeFalse();
        result.Confidence.ShouldBe(0.5f);
    }

    [Test]
    public async Task TryResolveAsync_ClampsConfidenceToValidRange()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "diagnosis": "Out of range confidence",
                    "confidence": 1.5
                }
                """
            });

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.Confidence.ShouldBeInRange(0f, 1f);
    }

    [Test]
    public async Task TryResolveAsync_WithJsonInMarkdownCodeBlock_ParsesCorrectly()
    {
        // Arrange
        var context = CreateTestContext();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                Here's my analysis:
                ```json
                {
                    "diagnosis": "Class name changed",
                    "majorStructureChange": false,
                    "newCssSelector": null,
                    "newXPathSelector": null,
                    "confidence": 0.8,
                    "reasoning": "Minor class rename",
                    "suggestedAction": "Update selector"
                }
                ```
                """
            });

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert
        result.Diagnosis.ShouldBe("Class name changed");
        result.Confidence.ShouldBe(0.8f);
    }

    // --- ValidateSelectorFixAsync tests ---

    [Test]
    public async Task ValidateSelectorFix_WithMatchingXPath_ReturnsValid()
    {
        // Arrange
        var html = "<html><body><div id='price'>$29.99</div></body></html>";
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("$29.99");

        // Act
        var result = await _sut.ValidateSelectorFixAsync(html, "//*[@id='price']", SelectorType.XPath);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.MatchCount.ShouldBe(1);
        result.ExtractedSample.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task ValidateSelectorFix_WithCssIdSelector_ReturnsValid()
    {
        // Arrange
        var html = "<html><body><div id='product'>Widget</div></body></html>";
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("Widget");

        // Act
        var result = await _sut.ValidateSelectorFixAsync(html, "#product", SelectorType.CssSelector);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.MatchCount.ShouldBe(1);
    }

    [Test]
    public async Task ValidateSelectorFix_WithCssClassSelector_ReturnsValid()
    {
        // Arrange
        var html = "<html><body><span class='highlight'>Important</span></body></html>";
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("Important");

        // Act
        var result = await _sut.ValidateSelectorFixAsync(html, ".highlight", SelectorType.CssSelector);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.MatchCount.ShouldBe(1);
    }

    [Test]
    public async Task ValidateSelectorFix_WithNoMatches_ReturnsInvalid()
    {
        // Arrange
        var html = "<html><body><p>No matching elements</p></body></html>";

        // Act
        var result = await _sut.ValidateSelectorFixAsync(html, "#nonexistent", SelectorType.CssSelector);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.MatchCount.ShouldBe(0);
        result.ErrorMessage.ShouldContain("no elements");
    }

    [Test]
    public async Task ValidateSelectorFix_WithInvalidXPath_ReturnsError()
    {
        // Arrange
        var html = "<html><body><p>Content</p></body></html>";

        // Act
        var result = await _sut.ValidateSelectorFixAsync(html, "[[[invalid", SelectorType.XPath);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("validation error");
    }

    [Test]
    public async Task ValidateSelectorFix_WithElementDotClass_ReturnsValid()
    {
        // Arrange
        var html = "<html><body><div class='price'>$10</div><span class='price'>$20</span></body></html>";
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("$10");

        // Act
        var result = await _sut.ValidateSelectorFixAsync(html, "div.price", SelectorType.CssSelector);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.MatchCount.ShouldBe(1);
    }

    [Test]
    public async Task ValidateSelectorFix_WithElementHashId_ReturnsValid()
    {
        // Arrange
        var html = "<html><body><div id='main'>Content</div></body></html>";
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("Content");

        // Act
        var result = await _sut.ValidateSelectorFixAsync(html, "div#main", SelectorType.CssSelector);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.MatchCount.ShouldBe(1);
    }

    [Test]
    public async Task ValidateSelectorFix_WithPlainElementName_ReturnsValid()
    {
        // Arrange
        var html = "<html><body><article>Article content</article></body></html>";
        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("Article content");

        // Act
        var result = await _sut.ValidateSelectorFixAsync(html, "article", SelectorType.CssSelector);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.MatchCount.ShouldBe(1);
    }

    [Test]
    public async Task TryResolveAsync_WithNullPreviousContent_BuildsPromptSuccessfully()
    {
        // Arrange
        var context = new ErrorResolutionContext
        {
            Watch = CreateTestWatch(cssSelector: ".price"),
            CurrentHtml = "<html><body><div class='cost'>$5</div></body></html>",
            ErrorMessage = "Selector failed",
            ErrorType = ErrorType.SelectorNoMatch,
            PreviousContent = null,
            ConsecutiveFailures = 3
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "diagnosis": "Selector no longer matches",
                    "majorStructureChange": false,
                    "newCssSelector": ".cost",
                    "confidence": 0.9
                }
                """
            });

        _contentExtractor.ExtractText(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns("$5");

        // Act
        var result = await _sut.TryResolveAsync(context);

        // Assert - should not throw and should produce a result
        result.ShouldNotBeNull();
        result.Diagnosis.ShouldNotBeNullOrEmpty();
    }

    [Test]
    public async Task TryResolveAsync_WithNoSelectorInWatch_IncludesFullPageInPrompt()
    {
        // Arrange
        var watch = CreateTestWatch(); // no selector
        var context = CreateTestContext(watch: watch);

        string? capturedPrompt = null;
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.ArgAt<string>(0);
                return Task.FromResult(new LlmResponse
                {
                    IsSuccess = true,
                    Content = """{"diagnosis": "No selector configured", "confidence": 0.5}"""
                });
            });

        // Act
        await _sut.TryResolveAsync(context);

        // Assert
        capturedPrompt.ShouldNotBeNull();
        capturedPrompt!.ShouldContain("Full page (no selector)");
    }

    [Test]
    public async Task TryResolveAsync_SetsCorrectLlmRequestOptions()
    {
        // Arrange
        var context = CreateTestContext();

        LlmRequestOptions? capturedOptions = null;
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedOptions = callInfo.ArgAt<LlmRequestOptions>(1);
                return Task.FromResult(new LlmResponse
                {
                    IsSuccess = true,
                    Content = """{"diagnosis": "test", "confidence": 0.5}"""
                });
            });

        // Act
        await _sut.TryResolveAsync(context);

        // Assert
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.Temperature.ShouldBe(0.2f);
        capturedOptions.ExpectJson.ShouldBeTrue();
        capturedOptions.UsageType.ShouldBe(LlmUsageType.ErrorResolution);
        capturedOptions.WatchedSiteId.ShouldBe(context.Watch.Id);
    }
}
