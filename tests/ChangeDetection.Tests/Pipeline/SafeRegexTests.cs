using ChangeDetection.Core.Pipeline;
using Shouldly;

namespace ChangeDetection.Tests.Pipeline;

[Category("Unit")]
public class SafeRegexTests
{
    [Test]
    public async Task TryMatch_ValidPattern_ReturnsMatch()
    {
        var match = SafeRegex.TryMatch("hello world", @"\w+");

        match.ShouldNotBeNull();
        match.Value.ShouldBe("hello");

        await Task.CompletedTask;
    }

    [Test]
    public async Task TryIsMatch_ValidPattern_ReturnsTrue()
    {
        var result = SafeRegex.TryIsMatch("hello world", @"\w+");

        result.ShouldBeTrue();

        await Task.CompletedTask;
    }

    [Test]
    public async Task TryMatch_ReDoSPattern_ReturnsNullOnTimeout()
    {
        // Classic ReDoS pattern with catastrophic backtracking
        var result = SafeRegex.TryMatch(
            new string('a', 30) + "!",
            @"(a+)+b",
            timeout: TimeSpan.FromMilliseconds(500));

        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task TryIsMatch_ReDoSPattern_ReturnsFalseOnTimeout()
    {
        var result = SafeRegex.TryIsMatch(
            new string('a', 30) + "!",
            @"(a+)+b",
            timeout: TimeSpan.FromMilliseconds(500));

        result.ShouldBeFalse();

        await Task.CompletedTask;
    }

    [Test]
    public async Task TryMatch_InvalidPattern_ReturnsNull()
    {
        var result = SafeRegex.TryMatch("hello", @"[invalid(");

        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task TryIsMatch_InvalidPattern_ReturnsFalse()
    {
        var result = SafeRegex.TryIsMatch("hello", @"[invalid(");

        result.ShouldBeFalse();

        await Task.CompletedTask;
    }

    [Test]
    public async Task TryMatch_EmptyInput_ReturnsNullForNonEmptyPattern()
    {
        var result = SafeRegex.TryMatch("", @"\w+");

        result.ShouldBeNull();

        await Task.CompletedTask;
    }

    [Test]
    public async Task TryIsMatch_EmptyInput_ReturnsFalseForNonEmptyPattern()
    {
        var result = SafeRegex.TryIsMatch("", @"\w+");

        result.ShouldBeFalse();

        await Task.CompletedTask;
    }

    [Test]
    public async Task TryMatch_NoMatch_ReturnsNull()
    {
        var result = SafeRegex.TryMatch("hello", @"\d+");

        result.ShouldBeNull();

        await Task.CompletedTask;
    }
}
