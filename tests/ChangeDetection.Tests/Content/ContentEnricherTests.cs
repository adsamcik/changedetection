using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for ContentEnricher LLM-powered content enrichment service.
/// </summary>
public class ContentEnricherTests
{
    private readonly ILlmProviderChain _llmChain;
    private readonly ILogger<ContentEnricher> _logger;
    private readonly ContentEnricher _sut;

    public ContentEnricherTests()
    {
        _llmChain = Substitute.For<ILlmProviderChain>();
        _logger = Substitute.For<ILogger<ContentEnricher>>();
        _sut = new ContentEnricher(_llmChain, _logger);
    }

    [Fact]
    public async Task EnrichContentAsync_WithValidContent_ReturnsEnrichedResult()
    {
        // Arrange
        var request = new ContentEnrichmentRequest
        {
            Content = "Apple Inc. announced the new iPhone 15 Pro today at $999. The event will be held on September 12, 2024 in Cupertino.",
            Url = "https://example.com/news",
            Title = "Tech News",
            UserIntent = "Track product announcements"
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "summary": "Apple announced the new iPhone 15 Pro priced at $999, with an event scheduled for September 12, 2024 in Cupertino.",
                    "contentType": "Article",
                    "language": "en",
                    "entities": [
                        {"type": "Organization", "text": "Apple Inc.", "normalizedValue": "Apple", "count": 1, "confidence": 0.95, "isProminent": true},
                        {"type": "Product", "text": "iPhone 15 Pro", "normalizedValue": null, "count": 1, "confidence": 0.95, "isProminent": true},
                        {"type": "Location", "text": "Cupertino", "normalizedValue": "Cupertino, CA", "count": 1, "confidence": 0.9, "isProminent": false}
                    ],
                    "topics": [
                        {"name": "Product Launch", "relevance": 0.95, "category": "Technology", "keywords": ["announcement", "new product", "iPhone"]},
                        {"name": "Consumer Electronics", "relevance": 0.85, "category": "Technology", "keywords": ["phone", "smartphone"]}
                    ],
                    "sentiment": {
                        "overall": "Neutral",
                        "score": 0.1,
                        "confidence": 0.8,
                        "dominantEmotion": null
                    },
                    "structuredData": [
                        {"type": "Price", "rawText": "$999", "normalizedValue": "999", "label": "iPhone 15 Pro price", "unit": "USD", "confidence": 0.95},
                        {"type": "Date", "rawText": "September 12, 2024", "normalizedValue": "2024-09-12", "label": "Event date", "unit": null, "confidence": 0.95}
                    ],
                    "keyPhrases": ["iPhone 15 Pro", "Apple announcement", "September event"],
                    "readingLevel": "HighSchool",
                    "confidence": 0.9
                }
                """,
                InputTokens = 150,
                OutputTokens = 300
            });

        // Act
        var result = await _sut.EnrichContentAsync(request);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Summary.ShouldNotBeNullOrEmpty();
        result.ContentType.ShouldBe("Article");
        result.Language.ShouldBe("en");
        
        result.Entities.ShouldNotBeEmpty();
        result.Entities.Count.ShouldBe(3);
        result.Entities.ShouldContain(e => e.Type == "Organization" && e.Text == "Apple Inc.");
        result.Entities.ShouldContain(e => e.Type == "Product" && e.Text == "iPhone 15 Pro");
        
        result.Topics.ShouldNotBeEmpty();
        result.Topics.ShouldContain(t => t.Name == "Product Launch");
        
        result.Sentiment.ShouldNotBeNull();
        result.Sentiment!.Overall.ShouldBe("Neutral");
        
        result.StructuredData.ShouldNotBeEmpty();
        result.StructuredData.ShouldContain(s => s.Type == "Price" && s.NormalizedValue == "999");
        result.StructuredData.ShouldContain(s => s.Type == "Date");
        
        result.KeyPhrases.ShouldNotBeEmpty();
        result.Confidence.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task EnrichContentAsync_WhenLlmFails_ReturnsFailureResult()
    {
        // Arrange
        var request = new ContentEnrichmentRequest
        {
            Content = "Some content",
            Url = "https://example.com"
        };

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "LLM service unavailable"
            });

        // Act
        var result = await _sut.EnrichContentAsync(request);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task QuickClassifyAsync_ReturnsContentClassification()
    {
        // Arrange
        var content = "Buy the new Widget Pro for only $49.99! Sale ends December 31st.";

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "contentType": "ECommerce",
                    "language": "en",
                    "hasStructuredData": true,
                    "isTimeSensitive": true,
                    "suggestedEnrichments": ["entities", "structuredData"],
                    "confidence": 0.9
                }
                """
            });

        // Act
        var result = await _sut.QuickClassifyAsync(content);

        // Assert
        result.ContentType.ShouldBe("ECommerce");
        result.Language.ShouldBe("en");
        result.HasStructuredData.ShouldBeTrue();
        result.IsTimeSensitive.ShouldBeTrue();
        result.SuggestedEnrichments.ShouldContain("entities");
        result.Confidence.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task QuickClassifyAsync_WhenLlmFails_ReturnsDefaultClassification()
    {
        // Arrange
        var content = "Some content";

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "Error"
            });

        // Act
        var result = await _sut.QuickClassifyAsync(content);

        // Assert
        result.ContentType.ShouldBe("Unknown");
        result.Confidence.ShouldBe(0);
    }

    [Fact]
    public async Task GenerateFingerprintAsync_ReturnsSemanticFingerprint()
    {
        // Arrange
        var content = "Breaking news: Major earthquake hits California coast. Emergency services responding.";

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = true,
                Content = """
                {
                    "keyTopics": ["earthquake", "California", "emergency response"],
                    "keyEntities": ["California", "emergency services"],
                    "structureSignature": "news article with headline",
                    "semanticHash": "California earthquake emergency news"
                }
                """
            });

        // Act
        var result = await _sut.GenerateFingerprintAsync(content);

        // Assert
        result.SemanticHash.ShouldNotBeNullOrEmpty();
        result.KeyTopics.ShouldNotBeEmpty();
        result.KeyEntities.ShouldNotBeEmpty();
        result.StructureSignature.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task GenerateFingerprintAsync_WhenLlmFails_ReturnsFallbackFingerprint()
    {
        // Arrange
        var content = "Some content here";

        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(new LlmResponse
            {
                IsSuccess = false,
                ErrorMessage = "Error"
            });

        // Act
        var result = await _sut.GenerateFingerprintAsync(content);

        // Assert
        // Should return a fallback hash-based fingerprint
        result.SemanticHash.ShouldNotBeNullOrEmpty();
        result.SemanticHash.Length.ShouldBe(16); // SHA256 truncated to 16 chars
    }

    [Fact]
    public void ContentFingerprint_CompareSimilarity_CalculatesOverlap()
    {
        // Arrange
        var fingerprint1 = new ContentFingerprint
        {
            SemanticHash = "test1",
            KeyTopics = ["tech", "apple", "iphone"],
            KeyEntities = ["Apple Inc", "iPhone", "Tim Cook"]
        };

        var fingerprint2 = new ContentFingerprint
        {
            SemanticHash = "test2",
            KeyTopics = ["tech", "apple", "macbook"],
            KeyEntities = ["Apple Inc", "MacBook", "Tim Cook"]
        };

        // Act
        var similarity = fingerprint1.CompareSimilarity(fingerprint2);

        // Assert
        similarity.ShouldBeGreaterThan(0);
        similarity.ShouldBeLessThan(1);
    }

    [Fact]
    public void ContentFingerprint_CompareSimilarity_WithIdentical_ReturnsOne()
    {
        // Arrange
        var fingerprint1 = new ContentFingerprint
        {
            SemanticHash = "test",
            KeyTopics = ["tech", "apple"],
            KeyEntities = ["Apple Inc"]
        };

        var fingerprint2 = new ContentFingerprint
        {
            SemanticHash = "test",
            KeyTopics = ["tech", "apple"],
            KeyEntities = ["Apple Inc"]
        };

        // Act
        var similarity = fingerprint1.CompareSimilarity(fingerprint2);

        // Assert
        similarity.ShouldBe(1.0f);
    }

    [Fact]
    public void ContentFingerprint_CompareSimilarity_WithNoOverlap_ReturnsZero()
    {
        // Arrange
        var fingerprint1 = new ContentFingerprint
        {
            SemanticHash = "test1",
            KeyTopics = ["sports", "football"],
            KeyEntities = ["NFL", "Super Bowl"]
        };

        var fingerprint2 = new ContentFingerprint
        {
            SemanticHash = "test2",
            KeyTopics = ["cooking", "recipes"],
            KeyEntities = ["Gordon Ramsay", "MasterChef"]
        };

        // Act
        var similarity = fingerprint1.CompareSimilarity(fingerprint2);

        // Assert
        similarity.ShouldBe(0);
    }

    [Fact]
    public async Task EnrichContentAsync_TruncatesLongContent()
    {
        // Arrange
        var longContent = new string('x', 10000); // Very long content
        var request = new ContentEnrichmentRequest
        {
            Content = longContent,
            Url = "https://example.com",
            MaxContentLength = 1000
        };

        string? capturedPrompt = null;
        _llmChain.ExecuteAsync(Arg.Any<string>(), Arg.Any<LlmRequestOptions>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedPrompt = callInfo.ArgAt<string>(0);
                return Task.FromResult(new LlmResponse
                {
                    IsSuccess = true,
                    Content = """{"summary": "test", "confidence": 0.5}"""
                });
            });

        // Act
        await _sut.EnrichContentAsync(request);

        // Assert
        capturedPrompt.ShouldNotBeNull();
        // The content in the prompt should be truncated
        capturedPrompt!.Length.ShouldBeLessThan(longContent.Length);
    }
}
