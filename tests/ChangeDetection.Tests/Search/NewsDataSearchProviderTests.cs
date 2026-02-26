using ChangeDetection.Core.Entities;
using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class NewsDataSearchProviderTests : TestBase
{
    [Test]
    public async Task ProviderId_ReturnsNewsdata()
    {
        var sut = CreateProvider();
        sut.ProviderId.ShouldBe("newsdata");
        sut.DisplayName.ShouldBe("NewsData.io");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_NoApiKey_ReturnsFalse()
    {
        var sut = CreateProvider(apiKey: null);
        sut.IsAvailable.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WithApiKey_ReturnsTrue()
    {
        var sut = CreateProvider(apiKey: "test-key");
        sut.IsAvailable.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchAsync_NotConfigured_ReturnsError()
    {
        var sut = CreateProvider(apiKey: null);
        var result = await sut.SearchAsync(new SearchQuery { Query = "test" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("not configured");
        result.Results.ShouldBeEmpty();
    }

    // --- BuildRequestUrl tests ---

    [Test]
    public async Task BuildRequestUrl_BasicQuery_IncludesQueryParam()
    {
        var url = NewsDataSearchProvider.BuildRequestUrl(new SearchQuery { Query = "test news" });
        url.ShouldContain("q=test%20news");
        url.ShouldContain("newsdata.io");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithLanguage_IncludesLanguageParam()
    {
        var url = NewsDataSearchProvider.BuildRequestUrl(new SearchQuery
        {
            Query = "test",
            Language = "en"
        });
        url.ShouldContain("language=en");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithCategory_IncludesCategoryParam()
    {
        var url = NewsDataSearchProvider.BuildRequestUrl(new SearchQuery
        {
            Query = "test",
            Category = "technology"
        });
        url.ShouldContain("category=technology");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_GeneralCategory_OmitsCategory()
    {
        var url = NewsDataSearchProvider.BuildRequestUrl(new SearchQuery
        {
            Query = "test",
            Category = "general"
        });
        url.ShouldNotContain("category=");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_DayTimeRange_MapsToHours()
    {
        var url = NewsDataSearchProvider.BuildRequestUrl(new SearchQuery
        {
            Query = "test",
            TimeRange = "day"
        });
        url.ShouldContain("timeframe=24");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WeekTimeRange_MapsToHours()
    {
        var url = NewsDataSearchProvider.BuildRequestUrl(new SearchQuery
        {
            Query = "test",
            TimeRange = "week"
        });
        url.ShouldContain("timeframe=168");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_MaxResults_CapsAt50()
    {
        var url = NewsDataSearchProvider.BuildRequestUrl(new SearchQuery
        {
            Query = "test",
            MaxResults = 100
        });
        url.ShouldContain("size=50");
        await Task.CompletedTask;
    }

    // --- MapResults tests ---

    [Test]
    public async Task MapResults_ValidArticles_MapsCorrectly()
    {
        var articles = new List<NewsDataSearchProvider.NewsDataArticle>
        {
            new()
            {
                Title = "Breaking News",
                Link = "https://news.com/article1",
                Description = "First article",
                PubDate = new DateTime(2026, 1, 15, 10, 0, 0),
                Category = ["technology"],
                SourceName = "Tech News"
            },
            new()
            {
                Title = "Second Story",
                Link = "https://news.com/article2",
                Description = "Second article"
            }
        };

        var results = NewsDataSearchProvider.MapResults(articles, 10);
        results.Count.ShouldBe(2);
        results[0].Title.ShouldBe("Breaking News");
        results[0].Url.ShouldBe("https://news.com/article1");
        results[0].Position.ShouldBe(1);
        results[0].Category.ShouldBe("technology");
        results[0].Engine.ShouldBe("newsdata.io");
        results[1].Position.ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_SkipsArticlesWithNoLink()
    {
        var articles = new List<NewsDataSearchProvider.NewsDataArticle>
        {
            new() { Title = "Has link", Link = "https://a.com", Description = "ok" },
            new() { Title = "No link", Link = null, Description = "skip" },
            new() { Title = "Empty link", Link = "", Description = "skip" }
        };

        var results = NewsDataSearchProvider.MapResults(articles, 10);
        results.Count.ShouldBe(1);
        results[0].Title.ShouldBe("Has link");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_RespectsMaxResults()
    {
        var articles = Enumerable.Range(1, 10).Select(i => new NewsDataSearchProvider.NewsDataArticle
        {
            Title = $"Article {i}",
            Link = $"https://news.com/{i}"
        }).ToList();

        var results = NewsDataSearchProvider.MapResults(articles, 3);
        results.Count.ShouldBe(3);
        await Task.CompletedTask;
    }

    private static NewsDataSearchProvider CreateProvider(string? apiKey = "test-key")
    {
        var settings = Options.Create(new SearchSettings { NewsDataApiKey = apiKey });
        var httpClient = new HttpClient();
        return new NewsDataSearchProvider(httpClient, settings,
            NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<NewsDataSearchProvider>>());
    }
}
