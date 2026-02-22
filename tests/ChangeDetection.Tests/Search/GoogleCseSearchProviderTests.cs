using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class GoogleCseSearchProviderTests : TestBase
{
    [Test]
    public async Task ProviderId_ReturnsGoogleCse()
    {
        var provider = CreateProvider();
        provider.ProviderId.ShouldBe("google-cse");
        provider.DisplayName.ShouldBe("Google Custom Search");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WhenBothKeysConfigured_ReturnsTrue()
    {
        var provider = CreateProvider(apiKey: "test-key", engineId: "test-cx");
        provider.IsAvailable.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WhenApiKeyMissing_ReturnsFalse()
    {
        var provider = CreateProvider(apiKey: null, engineId: "test-cx");
        provider.IsAvailable.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WhenEngineIdMissing_ReturnsFalse()
    {
        var provider = CreateProvider(apiKey: "test-key", engineId: null);
        provider.IsAvailable.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WhenBothMissing_ReturnsFalse()
    {
        var provider = CreateProvider(apiKey: null, engineId: null);
        provider.IsAvailable.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_BasicQuery()
    {
        var query = new SearchQuery { Query = "dotnet news", MaxResults = 10 };
        var url = GoogleCseSearchProvider.BuildRequestUrl("key123", "cx456", query);

        url.ShouldContain("key=key123");
        url.ShouldContain("cx=cx456");
        url.ShouldContain("q=dotnet%20news");
        url.ShouldContain("num=10");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_CapsMaxResultsAt10()
    {
        var query = new SearchQuery { Query = "test", MaxResults = 50 };
        var url = GoogleCseSearchProvider.BuildRequestUrl("key", "cx", query);

        url.ShouldContain("num=10");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithLanguage()
    {
        var query = new SearchQuery { Query = "test", Language = "cs" };
        var url = GoogleCseSearchProvider.BuildRequestUrl("key", "cx", query);

        url.ShouldContain("lr=lang_cs");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("day", "dateRestrict=d1")]
    [Arguments("week", "dateRestrict=w1")]
    [Arguments("month", "dateRestrict=m1")]
    [Arguments("year", "dateRestrict=y1")]
    public async Task BuildRequestUrl_WithTimeRange(string timeRange, string expectedParam)
    {
        var query = new SearchQuery { Query = "test", TimeRange = timeRange };
        var url = GoogleCseSearchProvider.BuildRequestUrl("key", "cx", query);

        url.ShouldContain(expectedParam);
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithoutTimeRange_NoDateRestrict()
    {
        var query = new SearchQuery { Query = "test" };
        var url = GoogleCseSearchProvider.BuildRequestUrl("key", "cx", query);

        url.ShouldNotContain("dateRestrict");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_MapsGoogleResponseToSearchResults()
    {
        var response = new GoogleCseSearchProvider.GoogleCseResponse
        {
            Items =
            [
                new GoogleCseSearchProvider.GoogleCseItem
                {
                    Link = "https://example.com/page1",
                    Title = "Page One",
                    Snippet = "A snippet"
                },
                new GoogleCseSearchProvider.GoogleCseItem
                {
                    Link = "https://example.com/page2",
                    Title = "Page Two",
                    Snippet = "Another snippet"
                }
            ]
        };

        var results = GoogleCseSearchProvider.MapResults(response, 10);

        results.Count.ShouldBe(2);
        results[0].Url.ShouldBe("https://example.com/page1");
        results[0].Title.ShouldBe("Page One");
        results[0].Snippet.ShouldBe("A snippet");
        results[0].Engine.ShouldBe("google");
        results[0].Position.ShouldBe(1);
        results[1].Position.ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_FiltersEmptyLinks()
    {
        var response = new GoogleCseSearchProvider.GoogleCseResponse
        {
            Items =
            [
                new GoogleCseSearchProvider.GoogleCseItem { Link = "https://valid.com", Title = "Valid" },
                new GoogleCseSearchProvider.GoogleCseItem { Link = "", Title = "Empty" },
                new GoogleCseSearchProvider.GoogleCseItem { Link = null, Title = "Null" },
                new GoogleCseSearchProvider.GoogleCseItem { Link = "   ", Title = "Whitespace" }
            ]
        };

        var results = GoogleCseSearchProvider.MapResults(response, 10);
        results.Count.ShouldBe(1);
        results[0].Url.ShouldBe("https://valid.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_RespectsMaxResults()
    {
        var response = new GoogleCseSearchProvider.GoogleCseResponse
        {
            Items = Enumerable.Range(1, 10)
                .Select(i => new GoogleCseSearchProvider.GoogleCseItem
                {
                    Link = $"https://example.com/{i}",
                    Title = $"Result {i}"
                })
                .ToList()
        };

        var results = GoogleCseSearchProvider.MapResults(response, 5);
        results.Count.ShouldBe(5);
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_EmptyItems_ReturnsEmpty()
    {
        var response = new GoogleCseSearchProvider.GoogleCseResponse { Items = null };
        var results = GoogleCseSearchProvider.MapResults(response, 10);
        results.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchAsync_WhenNotConfigured_ReturnsError()
    {
        var provider = CreateProvider(apiKey: null, engineId: null);
        var result = await provider.SearchAsync(new SearchQuery { Query = "test" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("not configured");
        result.ProviderId.ShouldBe("google-cse");
    }

    private GoogleCseSearchProvider CreateProvider(
        string? apiKey = "test-key",
        string? engineId = "test-cx")
    {
        var httpClient = new HttpClient();
        var settings = Microsoft.Extensions.Options.Options.Create(
            new Core.Entities.SearchSettings
            {
                GoogleCseApiKey = apiKey,
                GoogleCseEngineId = engineId
            });
        var logger = CreateLogger<GoogleCseSearchProvider>();
        return new GoogleCseSearchProvider(httpClient, settings, logger);
    }
}
