using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Tests for DeduplicationService that prevents duplicate content snapshots.
/// Uses NSubstitute mocking - no real LLM calls are made.
/// </summary>
[Category("Unit")]
public class DeduplicationServiceTests : TestBase
{
    private readonly IContentEnricher _contentEnricher;
    private readonly DeduplicationService _sut;

    public DeduplicationServiceTests()
    {
        _contentEnricher = Substitute.For<IContentEnricher>();
        _sut = new DeduplicationService(_contentEnricher, CreateLogger<DeduplicationService>());
    }

    [Test]
    public async Task CheckForDuplicateAsync_WithExactHashMatch_ReturnsDuplicate()
    {
        // Arrange
        var request = new DeduplicationRequest
        {
            NewContent = "Test content",
            NewContentHash = "abc123",
            PreviousContentHash = "abc123",
            WatchId = Guid.NewGuid()
        };

        // Act
        var result = await _sut.CheckForDuplicateAsync(request);

        // Assert
        result.IsDuplicate.ShouldBeTrue();
        result.DuplicateType.ShouldBe(DuplicateType.ExactHash);
        result.SimilarityScore.ShouldBe(1.0f);
        result.Reason!.ShouldContain("Exact");
    }

    [Test]
    public async Task CheckForDuplicateAsync_WithDifferentHash_ReturnsNotDuplicate()
    {
        // Arrange
        var request = new DeduplicationRequest
        {
            NewContent = "New content",
            NewContentHash = "xyz789",
            PreviousContentHash = "abc123",
            WatchId = Guid.NewGuid(),
            UseSemanticComparison = false
        };

        // Act
        var result = await _sut.CheckForDuplicateAsync(request);

        // Assert
        result.IsDuplicate.ShouldBeFalse();
        result.DuplicateType.ShouldBe(DuplicateType.None);
    }

    [Test]
    public async Task CheckForDuplicateAsync_WithNoPreviousHash_ReturnsNotDuplicate()
    {
        // Arrange
        var request = new DeduplicationRequest
        {
            NewContent = "New content",
            NewContentHash = "abc123",
            PreviousContentHash = null,
            WatchId = Guid.NewGuid(),
            UseSemanticComparison = false
        };

        // Act
        var result = await _sut.CheckForDuplicateAsync(request);

        // Assert
        result.IsDuplicate.ShouldBeFalse();
        result.DuplicateType.ShouldBe(DuplicateType.None);
    }

    [Test]
    public async Task CheckForDuplicateAsync_WithSemanticSimilarityAboveThreshold_ReturnsDuplicate()
    {
        // Arrange
        var previousFingerprint = new ContentFingerprint
        {
            SemanticHash = "product page with pricing",
            KeyTopics = ["electronics", "pricing", "reviews"],
            KeyEntities = ["iPhone", "Apple", "$999"]
        };

        var newFingerprint = new ContentFingerprint
        {
            SemanticHash = "product page showing prices",
            KeyTopics = ["electronics", "pricing", "reviews"],
            KeyEntities = ["iPhone", "Apple", "$999"]
        };

        _contentEnricher.GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(newFingerprint);

        var request = new DeduplicationRequest
        {
            NewContent = "iPhone 15 Pro - $999. Great product with excellent reviews.",
            NewContentHash = "xyz789",
            PreviousContentHash = "abc123", // Different hash
            PreviousFingerprint = previousFingerprint,
            WatchId = Guid.NewGuid(),
            UseSemanticComparison = true,
            SimilarityThreshold = 0.95f
        };

        // Act
        var result = await _sut.CheckForDuplicateAsync(request);

        // Assert - exact overlap in both topics and entities = 1.0 similarity
        result.IsDuplicate.ShouldBeTrue();
        result.DuplicateType.ShouldBe(DuplicateType.SemanticSimilarity);
        result.SimilarityScore.ShouldNotBeNull();
        result.SimilarityScore!.Value.ShouldBeGreaterThanOrEqualTo(0.95f);
    }

    [Test]
    public async Task CheckForDuplicateAsync_WithSemanticSimilarityBelowThreshold_ReturnsNotDuplicate()
    {
        // Arrange
        var previousFingerprint = new ContentFingerprint
        {
            SemanticHash = "product page with pricing",
            KeyTopics = ["electronics", "pricing"],
            KeyEntities = ["iPhone", "Apple"]
        };

        var newFingerprint = new ContentFingerprint
        {
            SemanticHash = "news article about events",
            KeyTopics = ["news", "events"],
            KeyEntities = ["Conference", "2024"]
        };

        _contentEnricher.GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(newFingerprint);

        var request = new DeduplicationRequest
        {
            NewContent = "Annual conference 2024 - Join us for the biggest tech event.",
            NewContentHash = "xyz789",
            PreviousContentHash = "abc123",
            PreviousFingerprint = previousFingerprint,
            WatchId = Guid.NewGuid(),
            UseSemanticComparison = true,
            SimilarityThreshold = 0.95f
        };

        // Act
        var result = await _sut.CheckForDuplicateAsync(request);

        // Assert - no overlap = 0.0 similarity
        result.IsDuplicate.ShouldBeFalse();
        result.DuplicateType.ShouldBe(DuplicateType.None);
        result.SimilarityScore.ShouldNotBeNull();
        result.SimilarityScore!.Value.ShouldBeLessThan(0.95f);
    }

    [Test]
    public async Task CheckForDuplicateAsync_WhenSemanticDisabled_SkipsFingerprinting()
    {
        // Arrange
        var request = new DeduplicationRequest
        {
            NewContent = "Test content",
            NewContentHash = "xyz789",
            PreviousContentHash = "abc123",
            WatchId = Guid.NewGuid(),
            UseSemanticComparison = false
        };

        // Act
        await _sut.CheckForDuplicateAsync(request);

        // Assert - should not call fingerprint generation
        await _contentEnricher.DidNotReceive()
            .GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CheckForDuplicateAsync_WhenNoPreviousFingerprint_GeneratesNewFingerprint()
    {
        // Arrange
        var newFingerprint = new ContentFingerprint
        {
            SemanticHash = "test content hash",
            KeyTopics = ["testing"],
            KeyEntities = ["test"]
        };

        _contentEnricher.GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(newFingerprint);

        var request = new DeduplicationRequest
        {
            NewContent = "Test content",
            NewContentHash = "xyz789",
            PreviousContentHash = "abc123",
            PreviousFingerprint = null, // No previous fingerprint
            WatchId = Guid.NewGuid(),
            UseSemanticComparison = true
        };

        // Act
        var result = await _sut.CheckForDuplicateAsync(request);

        // Assert
        result.IsDuplicate.ShouldBeFalse();
        result.NewFingerprint.ShouldBe(newFingerprint);
    }

    [Test]
    public async Task CheckForDuplicateAsync_WhenFingerprintingFails_TreatsAsUnique()
    {
        // Arrange
        _contentEnricher.GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM unavailable"));

        var previousFingerprint = new ContentFingerprint
        {
            SemanticHash = "previous",
            KeyTopics = ["test"],
            KeyEntities = ["test"]
        };

        var request = new DeduplicationRequest
        {
            NewContent = "Test content",
            NewContentHash = "xyz789",
            PreviousContentHash = "abc123",
            PreviousFingerprint = previousFingerprint,
            WatchId = Guid.NewGuid(),
            UseSemanticComparison = true
        };

        // Act
        var result = await _sut.CheckForDuplicateAsync(request);

        // Assert - should fail gracefully and treat as unique
        result.IsDuplicate.ShouldBeFalse();
        result.DuplicateType.ShouldBe(DuplicateType.None);
    }

    [Test]
    public async Task GenerateFingerprintAsync_WithValidContent_ReturnsFingerprint()
    {
        // Arrange
        var expectedFingerprint = new ContentFingerprint
        {
            SemanticHash = "test hash",
            KeyTopics = ["topic1", "topic2"],
            KeyEntities = ["entity1"]
        };

        _contentEnricher.GenerateFingerprintAsync("Test content", Arg.Any<CancellationToken>())
            .Returns(expectedFingerprint);

        // Act
        var result = await _sut.GenerateFingerprintAsync("Test content");

        // Assert
        result.ShouldBe(expectedFingerprint);
    }

    [Test]
    public async Task GenerateFingerprintAsync_WithEmptyContent_ReturnsNull()
    {
        // Act
        var result = await _sut.GenerateFingerprintAsync("");

        // Assert
        result.ShouldBeNull();
        await _contentEnricher.DidNotReceive()
            .GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GenerateFingerprintAsync_WhenEnricherThrows_ReturnsNull()
    {
        // Arrange
        _contentEnricher.GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("LLM error"));

        // Act
        var result = await _sut.GenerateFingerprintAsync("Test content");

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public async Task CheckForDuplicateAsync_WithPartialTopicOverlap_CalculatesCorrectSimilarity()
    {
        // Arrange
        var previousFingerprint = new ContentFingerprint
        {
            SemanticHash = "previous content",
            KeyTopics = ["sports", "football", "news", "scores"],
            KeyEntities = ["NFL", "SuperBowl", "Patriots"]
        };

        // 2/4 topics overlap (50%), 2/3 entities overlap (66.7%)
        // Average similarity = (0.5 + 0.667) / 2 = 0.583
        var newFingerprint = new ContentFingerprint
        {
            SemanticHash = "new content",
            KeyTopics = ["sports", "football", "basketball", "hockey"],
            KeyEntities = ["NFL", "SuperBowl", "Lakers"]
        };

        _contentEnricher.GenerateFingerprintAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(newFingerprint);

        var request = new DeduplicationRequest
        {
            NewContent = "Sports news update",
            NewContentHash = "xyz789",
            PreviousContentHash = "abc123",
            PreviousFingerprint = previousFingerprint,
            WatchId = Guid.NewGuid(),
            UseSemanticComparison = true,
            SimilarityThreshold = 0.95f
        };

        // Act
        var result = await _sut.CheckForDuplicateAsync(request);

        // Assert
        result.IsDuplicate.ShouldBeFalse();
        result.SimilarityScore.ShouldNotBeNull();
        result.SimilarityScore!.Value.ShouldBeInRange(0.5f, 0.7f);
    }
}
