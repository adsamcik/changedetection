using ChangeDetection.Core.Entities;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class SearchConfigTests : TestBase
{
    [Test]
    public async Task SourceType_DefaultsToUrl()
    {
        var site = new WatchedSite { Url = "https://example.com" };

        site.SourceType.ShouldBe(SourceType.Url);
        site.SearchConfig.ShouldBeNull();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SourceType_CanBeSetToSearch()
    {
        var site = new WatchedSite
        {
            Url = "",
            SourceType = SourceType.Search,
            SearchConfig = new SearchConfig
            {
                Query = "test query",
                ProviderId = "searxng",
                Category = "news",
                MaxResults = 10
            }
        };

        site.SourceType.ShouldBe(SourceType.Search);
        site.SearchConfig.ShouldNotBeNull();
        site.SearchConfig.Query.ShouldBe("test query");
        site.SearchConfig.ProviderId.ShouldBe("searxng");
        site.SearchConfig.Category.ShouldBe("news");
        site.SearchConfig.MaxResults.ShouldBe(10);
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchConfig_DefaultMaxResults_Is20()
    {
        var config = new SearchConfig { Query = "test" };

        config.MaxResults.ShouldBe(20);
        config.ProviderId.ShouldBeNull();
        config.Category.ShouldBeNull();
        config.Language.ShouldBeNull();
        config.TimeRange.ShouldBeNull();
        await Task.CompletedTask;
    }
}
