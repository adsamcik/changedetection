using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class BraveSearchProviderTests : TestBase
{
    [Test]
    public async Task ProviderId_ReturnsBrave()
    {
        var provider = CreateProvider();
        provider.ProviderId.ShouldBe("brave");
        provider.DisplayName.ShouldBe("Brave Search");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WhenApiKeyConfigured_ReturnsTrue()
    {
        var provider = CreateProvider(apiKey: "test-key");
        provider.IsAvailable.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WhenApiKeyMissing_ReturnsFalse()
    {
        var provider = CreateProvider(apiKey: null);
        provider.IsAvailable.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_BasicQuery()
    {
        var query = new SearchQuery { Query = "dotnet news", MaxResults = 10 };
        var url = BraveSearchProvider.BuildRequestUrl(query);

        url.ShouldContain("q=dotnet%20news");
        url.ShouldContain("count=10");
        url.ShouldContain("api.search.brave.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_CapsMaxResultsAt20()
    {
        var query = new SearchQuery { Query = "test", MaxResults = 50 };
        var url = BraveSearchProvider.BuildRequestUrl(query);

        url.ShouldContain("count=20");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithLanguage()
    {
        var query = new SearchQuery { Query = "test", Language = "cs" };
        var url = BraveSearchProvider.BuildRequestUrl(query);

        url.ShouldContain("search_lang=cs");
        await Task.CompletedTask;
    }

    [Test]
    [Arguments("day", "freshness=pd")]
    [Arguments("week", "freshness=pw")]
    [Arguments("month", "freshness=pm")]
    [Arguments("year", "freshness=py")]
    public async Task BuildRequestUrl_WithTimeRange(string timeRange, string expectedParam)
    {
        var query = new SearchQuery { Query = "test", TimeRange = timeRange };
        var url = BraveSearchProvider.BuildRequestUrl(query);

        url.ShouldContain(expectedParam);
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithoutTimeRange_NoFreshness()
    {
        var query = new SearchQuery { Query = "test" };
        var url = BraveSearchProvider.BuildRequestUrl(query);

        url.ShouldNotContain("freshness");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_MapsBraveResponseToSearchResults()
    {
        var response = new BraveSearchProvider.BraveSearchResponse
        {
            Web = new BraveSearchProvider.BraveWebResults
            {
                Results =
                [
                    new BraveSearchProvider.BraveWebResult
                    {
                        Url = "https://example.com/page1",
                        Title = "Page One",
                        Description = "A description"
                    },
                    new BraveSearchProvider.BraveWebResult
                    {
                        Url = "https://example.com/page2",
                        Title = "Page Two",
                        Description = "Another description"
                    }
                ]
            }
        };

        var results = BraveSearchProvider.MapResults(response, 10);

        results.Count.ShouldBe(2);
        results[0].Url.ShouldBe("https://example.com/page1");
        results[0].Title.ShouldBe("Page One");
        results[0].Snippet.ShouldBe("A description");
        results[0].Engine.ShouldBe("brave");
        results[0].Position.ShouldBe(1);
        results[1].Position.ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_FiltersEmptyUrls()
    {
        var response = new BraveSearchProvider.BraveSearchResponse
        {
            Web = new BraveSearchProvider.BraveWebResults
            {
                Results =
                [
                    new BraveSearchProvider.BraveWebResult { Url = "https://valid.com", Title = "Valid" },
                    new BraveSearchProvider.BraveWebResult { Url = "", Title = "Empty" },
                    new BraveSearchProvider.BraveWebResult { Url = null, Title = "Null" }
                ]
            }
        };

        var results = BraveSearchProvider.MapResults(response, 10);
        results.Count.ShouldBe(1);
        results[0].Url.ShouldBe("https://valid.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_RespectsMaxResults()
    {
        var response = new BraveSearchProvider.BraveSearchResponse
        {
            Web = new BraveSearchProvider.BraveWebResults
            {
                Results = Enumerable.Range(1, 20)
                    .Select(i => new BraveSearchProvider.BraveWebResult
                    {
                        Url = $"https://example.com/{i}",
                        Title = $"Result {i}"
                    })
                    .ToList()
            }
        };

        var results = BraveSearchProvider.MapResults(response, 5);
        results.Count.ShouldBe(5);
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_NullWeb_ReturnsEmpty()
    {
        var response = new BraveSearchProvider.BraveSearchResponse { Web = null };
        var results = BraveSearchProvider.MapResults(response, 10);
        results.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchAsync_WhenNotConfigured_ReturnsError()
    {
        var provider = CreateProvider(apiKey: null);
        var result = await provider.SearchAsync(new SearchQuery { Query = "test" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("not configured");
        result.ProviderId.ShouldBe("brave");
    }

    private BraveSearchProvider CreateProvider(string? apiKey = "test-key")
    {
        var httpClient = new HttpClient();
        var settings = Microsoft.Extensions.Options.Options.Create(
            new Core.Entities.SearchSettings { BraveApiKey = apiKey });
        var logger = CreateLogger<BraveSearchProvider>();
        return new BraveSearchProvider(httpClient, settings, logger);
    }
}
