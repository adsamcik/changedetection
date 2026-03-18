using ChangeDetection.Core.Pipeline.Setup;
using Shouldly;

namespace ChangeDetection.Core.Tests.Pipeline.Setup;

[Category("Unit")]
public class PipelineTemplateRegistryTests
{
    private readonly IPipelineTemplateRegistry _sut = new PipelineTemplateRegistry();

    [Test]
    public async Task ListTemplates_ShouldExposeBuiltInTemplates()
    {
        var templates = _sut.ListTemplates();

        templates.Count.ShouldBeGreaterThanOrEqualTo(3);
        templates.ShouldContain(t => t.PlatformId == "workday");
        templates.ShouldContain(t => t.PlatformId == "wordpress");
        templates.ShouldContain(t => t.PlatformId == "shopify");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetTemplate_ShouldReturnWorkdayTemplateForJobIntent()
    {
        var template = _sut.GetTemplate("workday", "track new job openings");

        template.ShouldNotBeNull();
        template.PlatformId.ShouldBe("workday");
        template.Pipeline.Blocks.ShouldContain(b => b.Type == "ListDiff");
        template.Pipeline.Blocks.ShouldContain(b => b.Type == "Notify");
        await Task.CompletedTask;
    }

    [Test]
    public async Task GetTemplate_ShouldReturnGenericPriceTemplateForShopifyPriceIntent()
    {
        var template = _sut.GetTemplate("shopify", "watch price changes");

        template.ShouldNotBeNull();
        template.PlatformId.ShouldBe("shopify");
        template.Pipeline.Blocks.ShouldContain(b => b.Type == "NumericDelta");
        template.Pipeline.Metadata!.CardType.ShouldBe("price");
        await Task.CompletedTask;
    }
}
