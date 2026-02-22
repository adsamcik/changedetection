using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using NSubstitute;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class SearXNGSearchProviderTests : TestBase
{
    [Test]
    public async Task ProviderId_ReturnsSearxng()
    {
        var (provider, _) = CreateProvider();
        provider.ProviderId.ShouldBe("searxng");
        provider.DisplayName.ShouldBe("SearXNG");
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WhenUrlConfigured_ReturnsTrue()
    {
        var (provider, _) = CreateProvider(searxngUrl: "http://localhost:8080");
        provider.IsAvailable.ShouldBeTrue();
        await Task.CompletedTask;
    }

    [Test]
    public async Task IsAvailable_WhenUrlNotConfigured_ReturnsFalse()
    {
        var (provider, _) = CreateProvider(searxngUrl: null);
        provider.IsAvailable.ShouldBeFalse();
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchAsync_WhenNotConfigured_ReturnsError()
    {
        var (provider, _) = CreateProvider(searxngUrl: null);
        var result = await provider.SearchAsync(new SearchQuery { Query = "test" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("not configured");
        result.Results.ShouldBeEmpty();
    }

    [Test]
    public async Task BuildRequestUrl_BasicQuery_FormatsCorrectly()
    {
        var url = SearXNGSearchProvider.BuildRequestUrl(
            "http://localhost:8080",
            new SearchQuery { Query = "hello world" });

        url.ShouldBe("http://localhost:8080/search?q=hello%20world&format=json");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithCategory_IncludesCategory()
    {
        var url = SearXNGSearchProvider.BuildRequestUrl(
            "http://localhost:8080",
            new SearchQuery { Query = "test", Category = "news" });

        url.ShouldContain("categories=news");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithLanguage_IncludesLanguage()
    {
        var url = SearXNGSearchProvider.BuildRequestUrl(
            "http://localhost:8080",
            new SearchQuery { Query = "test", Language = "cs" });

        url.ShouldContain("language=cs");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_WithTimeRange_IncludesTimeRange()
    {
        var url = SearXNGSearchProvider.BuildRequestUrl(
            "http://localhost:8080",
            new SearchQuery { Query = "test", TimeRange = "week" });

        url.ShouldContain("time_range=week");
        await Task.CompletedTask;
    }

    [Test]
    public async Task BuildRequestUrl_TrimsTrailingSlash()
    {
        var url = SearXNGSearchProvider.BuildRequestUrl(
            "http://localhost:8080/",
            new SearchQuery { Query = "test" });

        url.ShouldStartWith("http://localhost:8080/search?");
        url.ShouldNotContain("//search");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_EmptyResponse_ReturnsEmptyList()
    {
        var response = new SearXNGSearchProvider.SearXNGResponse { Results = [] };
        var results = SearXNGSearchProvider.MapResults(response, 20);
        results.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_NullResults_ReturnsEmptyList()
    {
        var response = new SearXNGSearchProvider.SearXNGResponse { Results = null };
        var results = SearXNGSearchProvider.MapResults(response, 20);
        results.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_MapsFieldsCorrectly()
    {
        var response = new SearXNGSearchProvider.SearXNGResponse
        {
            Results =
            [
                new SearXNGSearchProvider.SearXNGResult
                {
                    Url = "https://example.com",
                    Title = "Example",
                    Content = "A snippet",
                    Engine = "google",
                    Category = "general"
                }
            ]
        };

        var results = SearXNGSearchProvider.MapResults(response, 20);

        results.Count.ShouldBe(1);
        results[0].Url.ShouldBe("https://example.com");
        results[0].Title.ShouldBe("Example");
        results[0].Snippet.ShouldBe("A snippet");
        results[0].Engine.ShouldBe("google");
        results[0].Category.ShouldBe("general");
        results[0].Position.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_RespectsMaxResults()
    {
        var response = new SearXNGSearchProvider.SearXNGResponse
        {
            Results = Enumerable.Range(1, 50)
                .Select(i => new SearXNGSearchProvider.SearXNGResult
                {
                    Url = $"https://example.com/{i}",
                    Title = $"Result {i}"
                })
                .ToList()
        };

        var results = SearXNGSearchProvider.MapResults(response, 10);
        results.Count.ShouldBe(10);
        await Task.CompletedTask;
    }

    [Test]
    public async Task MapResults_FiltersEmptyUrls()
    {
        var response = new SearXNGSearchProvider.SearXNGResponse
        {
            Results =
            [
                new SearXNGSearchProvider.SearXNGResult { Url = "https://valid.com", Title = "Valid" },
                new SearXNGSearchProvider.SearXNGResult { Url = "", Title = "Empty URL" },
                new SearXNGSearchProvider.SearXNGResult { Url = null, Title = "Null URL" },
                new SearXNGSearchProvider.SearXNGResult { Url = "   ", Title = "Whitespace URL" }
            ]
        };

        var results = SearXNGSearchProvider.MapResults(response, 20);
        results.Count.ShouldBe(1);
        results[0].Url.ShouldBe("https://valid.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchAsync_WhenUrlBlockedBySsrf_ReturnsError()
    {
        var httpClient = new HttpClient();
        var settings = Microsoft.Extensions.Options.Options.Create(
            new Core.Entities.SearchSettings { SearxngUrl = "http://169.254.169.254" });
        var urlValidator = Substitute.For<ChangeDetection.Core.Pipeline.IUrlValidator>();
        urlValidator.Validate("http://169.254.169.254").Returns("URL targets a blocked IP range");
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<SearXNGSearchProvider>();

        var provider = new SearXNGSearchProvider(httpClient, settings, urlValidator, logger);
        var result = await provider.SearchAsync(new SearchQuery { Query = "test" });

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("security policy");
    }

    private static (SearXNGSearchProvider provider, HttpClient httpClient) CreateProvider(
        string? searxngUrl = "http://localhost:8080")
    {
        var httpClient = new HttpClient();
        var settings = Microsoft.Extensions.Options.Options.Create(
            new Core.Entities.SearchSettings { SearxngUrl = searxngUrl });
        var urlValidator = Substitute.For<ChangeDetection.Core.Pipeline.IUrlValidator>();
        urlValidator.Validate(Arg.Any<string>()).Returns((string?)null); // allow all URLs
        var logger = new Microsoft.Extensions.Logging.Abstractions.NullLogger<SearXNGSearchProvider>();

        return (new SearXNGSearchProvider(httpClient, settings, urlValidator, logger), httpClient);
    }
}
