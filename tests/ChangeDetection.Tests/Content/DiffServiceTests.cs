using ChangeDetection.Services.Content;
using Shouldly;

namespace ChangeDetection.Tests.Content;

public class DiffServiceTests
{
    private readonly DiffService _sut = new();

    [Fact]
    public void Compare_IdenticalContent_NoChanges()
    {
        // Arrange
        var content = "Line 1\nLine 2\nLine 3";

        // Act
        var result = _sut.Compare(content, content);

        // Assert
        result.HasChanges.ShouldBeFalse();
        result.LinesAdded.ShouldBe(0);
        result.LinesRemoved.ShouldBe(0);
    }

    [Fact]
    public void Compare_AddedLines_DetectsAdditions()
    {
        // Arrange
        var oldContent = "Line 1\nLine 2";
        var newContent = "Line 1\nLine 2\nLine 3";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesAdded.ShouldBe(1);
        result.LinesRemoved.ShouldBe(0);
    }

    [Fact]
    public void Compare_RemovedLines_DetectsRemovals()
    {
        // Arrange
        var oldContent = "Line 1\nLine 2\nLine 3";
        var newContent = "Line 1\nLine 2";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesRemoved.ShouldBe(1);
    }

    [Fact]
    public void Compare_ModifiedLines_DetectsChanges()
    {
        // Arrange
        var oldContent = "Line 1\nOriginal Line\nLine 3";
        var newContent = "Line 1\nModified Line\nLine 3";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
    }

    [Fact]
    public void Compare_EmptyOldContent_AllLinesAdded()
    {
        // Arrange
        var oldContent = "";
        var newContent = "Line 1\nLine 2";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesAdded.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Compare_EmptyNewContent_AllLinesRemoved()
    {
        // Arrange
        var oldContent = "Line 1\nLine 2";
        var newContent = "";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesRemoved.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GenerateDiffHtml_WithChanges_ProducesHtml()
    {
        // Arrange
        var oldContent = "Original";
        var newContent = "Modified";
        var diff = _sut.Compare(oldContent, newContent);

        // Act
        var html = _sut.GenerateDiffHtml(diff);

        // Assert
        html.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void GenerateSummary_WithChanges_ProducesSummary()
    {
        // Arrange
        var oldContent = "Line 1";
        var newContent = "Line 1\nLine 2";
        var diff = _sut.Compare(oldContent, newContent);

        // Act
        var summary = _sut.GenerateSummary(diff);

        // Assert
        summary.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Compare_LargeContent_HandlesEfficiently()
    {
        // Arrange
        var lines = Enumerable.Range(1, 1000).Select(i => $"Line {i}");
        var oldContent = string.Join("\n", lines);
        var newContent = oldContent + "\nNew Line";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesAdded.ShouldBe(1);
    }
}
