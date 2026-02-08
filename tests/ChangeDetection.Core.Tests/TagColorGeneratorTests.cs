using ChangeDetection.Core;
using Shouldly;

namespace ChangeDetection.Core.Tests;

[Category("Unit")]
public class TagColorGeneratorTests
{
    [Test]
    public async Task GetColor_ShouldReturnHexColor()
    {
        var color = TagColorGenerator.GetColor("news");

        color.ShouldStartWith("#");
        color.Length.ShouldBe(7);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetColor_ShouldBeDeterministic()
    {
        var color1 = TagColorGenerator.GetColor("technology");
        var color2 = TagColorGenerator.GetColor("technology");

        color1.ShouldBe(color2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetColor_ShouldBeCaseInsensitive()
    {
        var color1 = TagColorGenerator.GetColor("News");
        var color2 = TagColorGenerator.GetColor("news");

        color1.ShouldBe(color2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetColor_ShouldReturnFirstPaletteColor_ForEmptyInput()
    {
        var palette = TagColorGenerator.GetPalette();
        var color = TagColorGenerator.GetColor("");

        color.ShouldBe(palette[0]);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetColor_WithOverrides_ShouldReturnUserColor()
    {
        var overrides = new Dictionary<string, string> { ["news"] = "#FF0000" };

        TagColorGenerator.GetColor("news", overrides).ShouldBe("#FF0000");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetColor_WithOverrides_ShouldFallbackToGenerated()
    {
        var overrides = new Dictionary<string, string> { ["other"] = "#FF0000" };

        var color = TagColorGenerator.GetColor("news", overrides);
        color.ShouldBe(TagColorGenerator.GetColor("news"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetColor_WithNullOverrides_ShouldReturnGenerated()
    {
        var color = TagColorGenerator.GetColor("news", null);

        color.ShouldBe(TagColorGenerator.GetColor("news"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetPalette_ShouldReturnNonEmptyList()
    {
        var palette = TagColorGenerator.GetPalette();

        palette.ShouldNotBeEmpty();
        palette.Count.ShouldBe(16);
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetPalette_AllColors_ShouldBeValidHex()
    {
        var palette = TagColorGenerator.GetPalette();

        foreach (var color in palette)
        {
            color.ShouldStartWith("#");
            color.Length.ShouldBe(7);
        }
        await Task.CompletedTask;
    }
}
