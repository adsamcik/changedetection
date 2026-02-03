using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.LLM;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Llm;

/// <summary>
/// Advanced tests for InputProcessor covering edge cases and error scenarios.
/// </summary>
[Category("Unit")]
public class InputProcessorAdvancedTests
{
    private readonly ILlmProviderChain _llmChain;
    private readonly IWatchService _watchService;
    private readonly ILogger<InputProcessor> _logger;
    private readonly InputProcessor _sut;

    public InputProcessorAdvancedTests()
    {
        _llmChain = Substitute.For<ILlmProviderChain>();
        _watchService = Substitute.For<IWatchService>();
        _logger = Substitute.For<ILogger<InputProcessor>>();
        _sut = new InputProcessor(_llmChain, _watchService, _logger);
    }

    // URL Analysis Edge Cases

    [Test]
    [Arguments("http://192.168.1.1")]
    [Arguments("http://192.168.1.1:8080")]
    [Arguments("http://192.168.1.1:8080/path")]
    public async Task Analyze_IpAddressUrl_RecognizesAsUrl(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.IsValid.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("http://[::1]")]
    [Arguments("http://[2001:db8::1]")]
    public async Task Analyze_Ipv6Url_RecognizesAsUrl(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("https://user:pass@example.com")]
    [Arguments("https://user@example.com")]
    public async Task Analyze_UrlWithCredentials_RecognizesAsUrl(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("subdomain.example.com")]
    [Arguments("deep.subdomain.example.com")]
    [Arguments("a.b.c.d.example.com")]
    public async Task Analyze_SubdomainWithoutScheme_TreatedAsNaturalLanguage(string input)
    {
        // Act - The implementation only recognizes www prefix or standard TLDs
        var result = _sut.Analyze(input);

        // Assert - Complex subdomains without www are treated as natural language
        result.Type.ShouldBe(InputType.NaturalLanguage);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("example.co.uk")]
    [Arguments("example.com.br")]
    public async Task Analyze_CommonMultiPartTld_RecognizedAsUrl(string input)
    {
        // Act - Common multi-part TLDs ARE recognized by the enhanced regex
        var result = _sut.Analyze(input);

        // Assert - These well-known TLDs are treated as URLs
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl.ShouldStartWith("https://");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("example.gov.uk")]      // gov.uk not in common pattern
    [Arguments("example.ac.uk")]       // academic TLD not in pattern
    [Arguments("example.org.uk")]      // org.uk not in common pattern
    public async Task Analyze_UncommonMultiPartTld_TreatedAsNaturalLanguage(string input)
    {
        // Act - Uncommon multi-part TLDs may not be recognized
        var result = _sut.Analyze(input);

        // Assert - These are treated as natural language (could be improved later)
        result.Type.ShouldBe(InputType.NaturalLanguage);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("https://example.com/path/to/page.html")]
    [Arguments("https://example.com/path/to/resource.json")]
    [Arguments("https://example.com/api/v2/data")]
    public async Task Analyze_UrlWithPath_PreservesPath(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl!.ShouldContain("/");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_UrlWithEncodedCharacters_PreservesEncoding()
    {
        // Arrange
        var input = "https://example.com/search?q=hello%20world";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl!.ShouldContain("%20");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_UrlWithUnicodeChars_IsRecognized()
    {
        // Arrange
        var input = "https://example.com/日本語";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        await Task.CompletedTask;
    }

    // Natural Language Edge Cases

    [Test]
    [Arguments("Monitor example for updates")] // "example" could be confused with domain
    [Arguments("Check www for new content")] // "www" prefix but incomplete
    [Arguments("Watch http changes")] // "http" as text
    public async Task Analyze_AmbiguousText_TreatsAsNaturalLanguage(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.NaturalLanguage);
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("   https://example.com   ")]
    [Arguments("\thttps://example.com\t")]
    [Arguments("\nhttps://example.com\n")]
    public async Task Analyze_UrlWithWhitespace_TrimsAndRecognizes(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.NormalizedUrl!.ShouldNotContain(" ");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("a")]
    [Arguments("ab")]
    [Arguments("abc")]
    public async Task Analyze_ShortInput_IsValid(string input)
    {
        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.IsValid.ShouldBeTrue();
        result.Type.ShouldBe(InputType.NaturalLanguage);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Analyze_VeryLongInput_Handles()
    {
        // Arrange
        var input = new string('a', 10000);

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.ShouldNotBeNull();
        result.Type.ShouldBe(InputType.NaturalLanguage);
        await Task.CompletedTask;
    }

    // ProcessWithLlm Edge Cases

    [Test]
    public async Task ProcessWithLlmAsync_LlmFailure_ReturnsError()
    {
        // Arrange
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = false, ErrorMessage = "Provider unavailable" });

        // Act
        var result = await _sut.ProcessWithLlmAsync("Create a watch for example.com");

        // Assert
        result.IsSuccess.ShouldBeFalse();
    }

    [Test]
    public async Task ProcessWithLlmAsync_EmptyLlmResponse_HandlesGracefully()
    {
        // Arrange
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = "" });

        // Act
        var result = await _sut.ProcessWithLlmAsync("Create a watch");

        // Assert
        // Should handle empty response without crashing
        result.ShouldNotBeNull();
    }

    [Test]
    public async Task ProcessWithLlmAsync_CancellationRequested_ThrowsOrReturnsError()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns<LlmResponse>(x => throw new OperationCanceledException());

        // Act
        var result = await _sut.ProcessWithLlmAsync("Test input", cts.Token);

        // Assert - Should handle cancellation gracefully
        result.ShouldNotBeNull();
        result.IsSuccess.ShouldBeFalse();
    }

    [Test]
    public async Task ProcessWithLlmAsync_HelpIntent_ReturnsHelpResponse()
    {
        // Arrange
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse { IsSuccess = true, Content = "help" });

        // Act
        var result = await _sut.ProcessWithLlmAsync("How do I use this?");

        // Assert
        result.ShouldNotBeNull();
        result.Intent.ShouldBe(IntentType.Help);
    }

    // Input Analysis Result

    [Test]
    public async Task InputAnalysis_Url_HasAllProperties()
    {
        // Arrange
        var input = "https://example.com/page?q=test#section";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.Type.ShouldBe(InputType.Url);
        result.IsValid.ShouldBeTrue();
        result.DetectedUrl.ShouldNotBeNull();
        result.NormalizedUrl.ShouldNotBeNull();
        result.ValidationMessage.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task InputAnalysis_Invalid_HasValidationMessage()
    {
        // Arrange
        var input = "";

        // Act
        var result = _sut.Analyze(input);

        // Assert
        result.IsValid.ShouldBeFalse();
        result.ValidationMessage.ShouldNotBeNullOrEmpty();
        await Task.CompletedTask;
    }
}

/// <summary>
/// Tests for InputType enum edge cases.
/// </summary>
public class InputTypeTests
{
    [Test]
    public async Task InputType_HasExpectedValues()
    {
        // Assert
        Enum.GetValues<InputType>().ShouldContain(InputType.Unknown);
        Enum.GetValues<InputType>().ShouldContain(InputType.Url);
        Enum.GetValues<InputType>().ShouldContain(InputType.NaturalLanguage);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Tests for IntentType enum.
/// </summary>
public class IntentTypeTests
{
    [Test]
    public async Task IntentType_HasExpectedValues()
    {
        // Assert
        var values = Enum.GetValues<IntentType>();
        values.ShouldContain(IntentType.Unknown);
        values.ShouldContain(IntentType.CreateWatch);
        values.ShouldContain(IntentType.ModifyWatch);
        values.ShouldContain(IntentType.DeleteWatch);
        values.ShouldContain(IntentType.Help);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Tests for LlmProcessResult.
/// </summary>
public class LlmProcessResultTests
{
    [Test]
    public async Task LlmProcessResult_HasCorrectDefaults()
    {
        // Act
        var result = new LlmProcessResult();

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.NeedsClarification.ShouldBeFalse();
        result.ParsedRequest.ShouldBeNull();
        result.ErrorMessage.ShouldBeNull();
        result.Suggestions.ShouldBeEmpty();
        result.ClarificationQuestions.ShouldNotBeNull(); // Defaults to empty list
        await Task.CompletedTask;
    }

    [Test]
    public async Task LlmProcessResult_CanSetAllProperties()
    {
        // Arrange & Act
        var result = new LlmProcessResult
        {
            IsSuccess = true,
            Intent = IntentType.CreateWatch,
            ParsedRequest = new ParsedWatchRequest { Url = "https://example.com" },
            NeedsClarification = true,
            ClarificationQuestions = ["What CSS selector?"],
            Suggestions = [new SuggestionChip { Label = "Default", Value = "default", Type = SuggestionType.SetValue }],
            Summary = "Created watch",
            CreatedWatchId = Guid.NewGuid()
        };

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Intent.ShouldBe(IntentType.CreateWatch);
        result.ParsedRequest.ShouldNotBeNull();
        result.NeedsClarification.ShouldBeTrue();
        result.ClarificationQuestions.ShouldContain("What CSS selector?");
        result.Suggestions.Count.ShouldBe(1);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Tests for ParsedWatchRequest.
/// </summary>
public class ParsedWatchRequestTests
{
    [Test]
    public async Task ParsedWatchRequest_HasNullableProperties()
    {
        // Act
        var request = new ParsedWatchRequest();

        // Assert
        request.Url.ShouldBeNull();
        request.Name.ShouldBeNull();
        request.CssSelector.ShouldBeNull();
        request.CheckInterval.ShouldBeNull();
        request.UseJavaScript.ShouldBeNull();
        request.Tags.ShouldBeNull(); // Tags is nullable list
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParsedWatchRequest_CanSetAllProperties()
    {
        // Arrange & Act
        var request = new ParsedWatchRequest
        {
            Url = "https://example.com",
            Name = "Test Watch",
            CssSelector = ".content",
            CheckInterval = TimeSpan.FromHours(1),
            UseJavaScript = true,
            Tags = ["tag1", "tag2"],
            NotificationEmail = "test@example.com",
            Description = "Monitor for changes"
        };

        // Assert
        request.Url.ShouldBe("https://example.com");
        request.Name.ShouldBe("Test Watch");
        request.CssSelector.ShouldBe(".content");
        request.CheckInterval.ShouldBe(TimeSpan.FromHours(1));
        request.UseJavaScript.ShouldBe(true);
        request.Tags.Count.ShouldBe(2);
        request.NotificationEmail.ShouldBe("test@example.com");
        request.Description.ShouldBe("Monitor for changes");
        await Task.CompletedTask;
    }
}

/// <summary>
/// Tests for SuggestionChip.
/// </summary>
public class SuggestionChipTests
{
    [Test]
    public async Task SuggestionChip_HasAllProperties()
    {
        // Arrange & Act
        var chip = new SuggestionChip
        {
            Label = "Create Watch",
            Value = "create",
            Type = SuggestionType.SetValue
        };

        // Assert
        chip.Label.ShouldBe("Create Watch");
        chip.Value.ShouldBe("create");
        chip.Type.ShouldBe(SuggestionType.SetValue);
        await Task.CompletedTask;
    }
}

/// <summary>
/// Tests for SuggestionType enum.
/// </summary>
public class SuggestionTypeTests
{
    [Test]
    public async Task SuggestionType_HasExpectedValues()
    {
        // Assert
        var values = Enum.GetValues<SuggestionType>();
        values.Length.ShouldBeGreaterThan(0);
        await Task.CompletedTask;
    }
}
