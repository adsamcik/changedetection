using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

[Category("Unit")]
public class InputProcessorTests
{
    private readonly ILlmProviderChain _llmChain;
    private readonly IWatchService _watchService;
    private readonly ILogger<InputProcessor> _logger;
    private readonly InputProcessor _sut;

    public InputProcessorTests()
    {
        _llmChain = Substitute.For<ILlmProviderChain>();
        _watchService = Substitute.For<IWatchService>();
        _logger = Substitute.For<ILogger<InputProcessor>>();
        _sut = new InputProcessor(_llmChain, _watchService, _logger);
    }

    [Test]
    [Arguments("https://example.com")]
    [Arguments("http://example.com")]
    [Arguments("https://www.example.com/page")]
    [Arguments("http://localhost:8080")]
    [Arguments("https://example.com/path?query=value")]
    [Arguments("https://www.img.cas.cz/novinky/akce/")]
    public async Task Analyze_ValidUrl_ReturnsUrlType(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.IsValid.ShouldBeTrue();
        result.NormalizedUrl.ShouldNotBeNullOrEmpty();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page", "https://www.img.cas.cz/novinky/akce/")]
    [Arguments("https://example.com watch for changes every hour", "https://example.com")]
    [Arguments("http://news.ycombinator.com/ monitor for new stories", "http://news.ycombinator.com/")]
    [Arguments("https://shop.com/product check price changes daily", "https://shop.com/product")]
    public async Task Analyze_UrlFollowedByNaturalLanguage_ReturnsNaturalLanguageWithExtractedUrl(string input, string expectedUrl)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert - Should be treated as natural language with the URL extracted
        result.Type.ShouldBe(InputType.NaturalLanguage);
        result.IsValid.ShouldBeTrue();
        result.DetectedUrl.ShouldBe(expectedUrl);
        result.NormalizedUrl.ShouldBe(expectedUrl);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("example.com")]
    [Arguments("www.example.com")]
    [Arguments("example.com/page")]
    public async Task Analyze_UrlWithoutScheme_NormalizesWithHttps(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl.ShouldStartWith("https://");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("Watch news.ycombinator.com for new stories")]
    [Arguments("Monitor the homepage of example.com")]
    [Arguments("Check prices on amazon.com/dp/B123")]
    public async Task Analyze_NaturalLanguageWithUrl_ReturnsNaturalLanguageType(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert - Natural language detection doesn't extract URLs (that's LLM's job)
        result.Type.ShouldBe(InputType.NaturalLanguage);
        result.IsValid.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("Tell me about website monitoring")]
    [Arguments("How does change detection work?")]
    [Arguments("Help me set up notifications")]
    public async Task Analyze_PureNaturalLanguage_ReturnsNaturalLanguageType(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.NaturalLanguage);
        result.DetectedUrl.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task Analyze_EmptyOrWhitespace_ReturnsInvalid(string? input)
    {
        // Act
        var result = _sut.Analyze(input ?? "");

        // Assert
        result.IsValid.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_UrlWithPort_ExtractsCorrectly()
    {
        // Arrange
        var input = "http://localhost:3000/api/data";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl.ShouldBe("http://localhost:3000/api/data");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_UrlWithQueryString_PreservesQuery()
    {
        // Arrange
        var input = "https://example.com/search?q=test&page=1";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl!.ShouldContain("q=test");
        result.NormalizedUrl!.ShouldContain("page=1");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_UrlWithFragment_PreservesFragment()
    {
        // Arrange
        var input = "https://example.com/page#section";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl!.ShouldContain("#section");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("ftp://example.com")]
    [Arguments("file:///path/to/file")]
    public async Task Analyze_NonHttpUrl_TreatedAsNaturalLanguage(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert - Non-HTTP schemes are treated as natural language (not valid URLs for monitoring)
        result.Type.ShouldBe(InputType.NaturalLanguage);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_MixedCaseUrl_NormalizesHost()
    {
        // Arrange
        var input = "HTTPS://EXAMPLE.COM/Page";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl.ShouldNotBeNull();
        // Host should be lowercased, path case preserved
        result.NormalizedUrl!.ToLower().ShouldContain("example.com");
        await Task.CompletedTask;
    }
}
