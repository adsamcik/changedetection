using ChangeDetection.Core;
using Shouldly;

namespace ChangeDetection.Core.Tests;

[Category("Unit")]
public class TagNormalizerTests
{
    [Test]
    public async Task Normalize_ShouldLowercaseAndTrim()
    {
        TagNormalizer.Normalize("  Hello World  ").ShouldBe("hello world");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Normalize_ShouldCollapseWhitespace()
    {
        TagNormalizer.Normalize("hello   world").ShouldBe("hello world");
        await Task.CompletedTask;
    }

    [Test]
    public async Task Normalize_ShouldReturnNull_ForNullOrWhitespace()
    {
        TagNormalizer.Normalize(null).ShouldBeNull();
        TagNormalizer.Normalize("").ShouldBeNull();
        TagNormalizer.Normalize("   ").ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task NormalizeList_ShouldDeduplicateAndSort()
    {
        var result = TagNormalizer.NormalizeList(["Beta", "alpha", "BETA", "alpha"]);

        result.ShouldBe(["alpha", "beta"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task NormalizeList_ShouldRemoveEmptyTags()
    {
        var result = TagNormalizer.NormalizeList(["valid", "", "   ", "also-valid"]);

        result.ShouldBe(["also-valid", "valid"]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task NormalizeList_ShouldReturnEmpty_ForNull()
    {
        TagNormalizer.NormalizeList(null).ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValid_ShouldReturnTrue_ForValidTag()
    {
        TagNormalizer.IsValid("hello").ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsValid_ShouldReturnFalse_ForInvalidTag()
    {
        TagNormalizer.IsValid(null).ShouldBeFalse();
        TagNormalizer.IsValid("").ShouldBeFalse();
        TagNormalizer.IsValid("   ").ShouldBeFalse();
        await Task.CompletedTask;
    }
}
