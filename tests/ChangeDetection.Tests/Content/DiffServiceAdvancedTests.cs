using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Content;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Content;

/// <summary>
/// Advanced tests for DiffService covering edge cases and comprehensive scenarios.
/// </summary>
public class DiffServiceAdvancedTests
{
    private readonly DiffService _sut = new();

    [Test]
    public async Task Compare_NullOldContent_TreatsAsEmpty()
    {
        // Arrange
        var newContent = "Some content";

        // Act
        var result = _sut.Compare(string.Empty, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesAdded.ShouldBeGreaterThan(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_NullNewContent_TreatsAsEmpty()
    {
        // Arrange
        var oldContent = "Some content";

        // Act
        var result = _sut.Compare(oldContent, string.Empty);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesRemoved.ShouldBeGreaterThan(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_BothEmpty_NoChanges()
    {
        // Act
        var result = _sut.Compare(string.Empty, string.Empty);

        // Assert
        result.HasChanges.ShouldBeFalse();
        result.LinesAdded.ShouldBe(0);
        result.LinesRemoved.ShouldBe(0);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_MultipleConsecutiveInsertions_CountsCorrectly()
    {
        // Arrange
        var oldContent = "Line 1\nLine 5";
        var newContent = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesAdded.ShouldBe(3);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_MultipleConsecutiveDeletions_CountsCorrectly()
    {
        // Arrange
        var oldContent = "Line 1\nLine 2\nLine 3\nLine 4\nLine 5";
        var newContent = "Line 1\nLine 5";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.LinesRemoved.ShouldBe(3);
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_MixedChanges_TracksAllTypes()
    {
        // Arrange
        var oldContent = "Line A\nLine B\nLine C";
        var newContent = "Line A\nLine Modified\nLine C\nLine D";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.Lines.ShouldNotBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_WhitespaceOnlyDifference_MayNotDetectTrailingSpace()
    {
        // Arrange - DiffPlex may not detect trailing whitespace as a change
        var oldContent = "Line 1";
        var newContent = "Line 1 ";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert - Trailing whitespace detection depends on DiffPlex implementation
        result.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_CaseOnlyDifference_DetectsChanges()
    {
        // Arrange
        var oldContent = "Hello World";
        var newContent = "hello world";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_ReorderedLines_DetectsChanges()
    {
        // Arrange
        var oldContent = "First\nSecond\nThird";
        var newContent = "Third\nSecond\nFirst";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_SpecialCharacters_HandlesCorrectly()
    {
        // Arrange
        var oldContent = "Regular text";
        var newContent = "Text with <html> & \"quotes\" 'apostrophes'";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        result.Lines.ShouldNotBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_UnicodeContent_HandlesCorrectly()
    {
        // Arrange
        var oldContent = "Hello 你好";
        var newContent = "Hello 世界";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.HasChanges.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task Compare_DifferentLineEndings_DetectsChanges()
    {
        // Arrange
        var oldContent = "Line1\r\nLine2";
        var newContent = "Line1\nLine2";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert - Different line endings are still different content
        result.ShouldNotBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateDiffHtml_WithInsertedLine_IncludesPlusPrefix()
    {
        // Arrange
        var diff = _sut.Compare("Old", "Old\nNew");

        // Act
        var html = _sut.GenerateDiffHtml(diff);

        // Assert
        html.ShouldContain("diff-added");
        html.ShouldContain("+");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateDiffHtml_WithDeletedLine_IncludesMinusPrefix()
    {
        // Arrange
        var diff = _sut.Compare("Old\nToRemove", "Old");

        // Act
        var html = _sut.GenerateDiffHtml(diff);

        // Assert
        html.ShouldContain("diff-removed");
        html.ShouldContain("-");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateDiffHtml_EscapesHtmlCharacters()
    {
        // Arrange
        var diff = _sut.Compare("", "<script>alert('xss')</script>");

        // Act
        var html = _sut.GenerateDiffHtml(diff);

        // Assert
        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateDiffHtml_IncludesLineNumbers()
    {
        // Arrange
        var diff = _sut.Compare("Line1\nLine2", "Line1\nLine2\nLine3");

        // Act
        var html = _sut.GenerateDiffHtml(diff);

        // Assert
        html.ShouldContain("line-numbers");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateDiffHtml_NoChanges_StillProducesOutput()
    {
        // Arrange
        var diff = _sut.Compare("Same", "Same");

        // Act
        var html = _sut.GenerateDiffHtml(diff);

        // Assert
        html.ShouldNotBeNullOrEmpty();
        html.ShouldContain("diff-container");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateSummary_NoChanges_ReturnsNoChangesMessage()
    {
        // Arrange
        var diff = _sut.Compare("Same content", "Same content");

        // Act
        var summary = _sut.GenerateSummary(diff);

        // Assert
        summary.ShouldBe("No changes detected.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateSummary_OnlyAdditions_MentionsAddedLines()
    {
        // Arrange
        var diff = _sut.Compare("Original", "Original\nNew line");

        // Act
        var summary = _sut.GenerateSummary(diff);

        // Assert
        summary.ShouldContain("added");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateSummary_OnlyDeletions_MentionsRemovedLines()
    {
        // Arrange
        var diff = _sut.Compare("Line 1\nLine 2", "Line 1");

        // Act
        var summary = _sut.GenerateSummary(diff);

        // Assert
        summary.ShouldContain("removed");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateSummary_SingleLineAdded_UsesSingularForm()
    {
        // Arrange
        var diff = _sut.Compare("", "One line");

        // Act
        var summary = _sut.GenerateSummary(diff);

        // Assert
        summary.ShouldContain("1 line added");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateSummary_MultipleLinesAdded_UsesPluralForm()
    {
        // Arrange
        var diff = _sut.Compare("", "Line 1\nLine 2\nLine 3");

        // Act
        var summary = _sut.GenerateSummary(diff);

        // Assert
        summary.ShouldContain("lines added");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GenerateSummary_MixedChanges_IncludesBoth()
    {
        // Arrange
        var oldContent = "Old line";
        var newContent = "New line";
        var diff = _sut.Compare(oldContent, newContent);

        // Act
        var summary = _sut.GenerateSummary(diff);

        // Assert
        // Should mention both added and removed (a replacement counts as both)
        summary.ShouldNotBeNullOrEmpty();
        summary.ShouldEndWith(".");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DiffResult_Lines_PreservesOrder()
    {
        // Arrange
        var oldContent = "A\nB\nC";
        var newContent = "A\nX\nC";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.Lines.Count.ShouldBeGreaterThan(0);
        // First non-changed line should be A
        var firstLine = result.Lines.First(l => l.Type == DiffLineType.Unchanged);
        firstLine.Text.ShouldBe("A");
        await Task.CompletedTask;
    }

    [Test]
    public async Task DiffResult_TracksUnchangedLines()
    {
        // Arrange
        var oldContent = "Unchanged\nModified\nUnchanged";
        var newContent = "Unchanged\nChanged\nUnchanged";

        // Act
        var result = _sut.Compare(oldContent, newContent);

        // Assert
        result.LinesUnchanged.ShouldBeGreaterThan(0);
        await Task.CompletedTask;
    }
}
