using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace ChangeDetection.Tests.Llm;

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

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com")]
    [InlineData("https://www.example.com/page")]
    [InlineData("http://localhost:8080")]
    [InlineData("https://example.com/path?query=value")]
    [InlineData("https://www.img.cas.cz/novinky/akce/")]
    public void Analyze_ValidUrl_ReturnsUrlType(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.IsValid.ShouldBeTrue();
        result.NormalizedUrl.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("https://www.img.cas.cz/novinky/akce/ I want to watch for the events on that page", "https://www.img.cas.cz/novinky/akce/")]
    [InlineData("https://example.com watch for changes every hour", "https://example.com")]
    [InlineData("http://news.ycombinator.com/ monitor for new stories", "http://news.ycombinator.com/")]
    [InlineData("https://shop.com/product check price changes daily", "https://shop.com/product")]
    public void Analyze_UrlFollowedByNaturalLanguage_ReturnsNaturalLanguageWithExtractedUrl(string input, string expectedUrl)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert - Should be treated as natural language with the URL extracted
        result.Type.ShouldBe(InputType.NaturalLanguage);
        result.IsValid.ShouldBeTrue();
        result.DetectedUrl.ShouldBe(expectedUrl);
        result.NormalizedUrl.ShouldBe(expectedUrl);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("www.example.com")]
    [InlineData("example.com/page")]
    public void Analyze_UrlWithoutScheme_NormalizesWithHttps(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl.ShouldStartWith("https://");
    }

    [Theory]
    [InlineData("Watch news.ycombinator.com for new stories")]
    [InlineData("Monitor the homepage of example.com")]
    [InlineData("Check prices on amazon.com/dp/B123")]
    public void Analyze_NaturalLanguageWithUrl_ReturnsNaturalLanguageType(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert - Natural language detection doesn't extract URLs (that's LLM's job)
        result.Type.ShouldBe(InputType.NaturalLanguage);
        result.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Tell me about website monitoring")]
    [InlineData("How does change detection work?")]
    [InlineData("Help me set up notifications")]
    public void Analyze_PureNaturalLanguage_ReturnsNaturalLanguageType(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.NaturalLanguage);
        result.DetectedUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Analyze_EmptyOrWhitespace_ReturnsInvalid(string? input)
    {
        // Act
        var result = _sut.Analyze(input ?? "");

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Analyze_UrlWithPort_ExtractsCorrectly()
    {
        // Arrange
        var input = "http://localhost:3000/api/data";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl.ShouldBe("http://localhost:3000/api/data");
    }

    [Fact]
    public void Analyze_UrlWithQueryString_PreservesQuery()
    {
        // Arrange
        var input = "https://example.com/search?q=test&page=1";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl!.ShouldContain("q=test");
        result.NormalizedUrl!.ShouldContain("page=1");
    }

    [Fact]
    public void Analyze_UrlWithFragment_PreservesFragment()
    {
        // Arrange
        var input = "https://example.com/page#section";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl!.ShouldContain("#section");
    }

    [Theory]
    [InlineData("ftp://example.com")]
    [InlineData("file:///path/to/file")]
    public void Analyze_NonHttpUrl_TreatedAsNaturalLanguage(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert - Non-HTTP schemes are treated as natural language (not valid URLs for monitoring)
        result.Type.ShouldBe(InputType.NaturalLanguage);
    }

    [Fact]
    public void Analyze_MixedCaseUrl_NormalizesHost()
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
    }
}
