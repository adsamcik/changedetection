using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class MultiProviderSearchServiceTests : TestBase
{
    [Test]
    public async Task MergeAndDeduplicate_DeduplicatesByUrl()
    {
        var provider1Results = new SearchResultSet
        {
            ProviderId = "provider1",
            Query = "test",
            Results =
            [
                new SearchResult { Url = "https://a.com", Title = "A from P1", Position = 1 },
                new SearchResult { Url = "https://b.com", Title = "B from P1", Position = 2 }
            ],
            IsSuccess = true
        };

        var provider2Results = new SearchResultSet
        {
            ProviderId = "provider2",
            Query = "test",
            Results =
            [
                new SearchResult { Url = "https://a.com", Title = "A from P2", Position = 3 },
                new SearchResult { Url = "https://c.com", Title = "C from P2", Position = 1 }
            ],
            IsSuccess = true
        };

        var merged = MultiProviderSearchService.MergeAndDeduplicate([provider1Results, provider2Results]);

        merged.Count.ShouldBe(3); // a.com, b.com, c.com
        merged.ShouldContain(r => r.Url == "https://a.com");
        merged.ShouldContain(r => r.Url == "https://b.com");
        merged.ShouldContain(r => r.Url == "https://c.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MergeAndDeduplicate_KeepsBestPosition()
    {
        var provider1 = new SearchResultSet
        {
            ProviderId = "p1", Query = "test", IsSuccess = true,
            Results = [new SearchResult { Url = "https://x.com", Title = "X P1", Position = 5 }]
        };

        var provider2 = new SearchResultSet
        {
            ProviderId = "p2", Query = "test", IsSuccess = true,
            Results = [new SearchResult { Url = "https://x.com", Title = "X P2 Better", Position = 1 }]
        };

        var merged = MultiProviderSearchService.MergeAndDeduplicate([provider1, provider2]);

        merged.Count.ShouldBe(1);
        merged[0].BestPosition.ShouldBe(1);
        merged[0].Title.ShouldBe("X P2 Better"); // title from better-ranked provider
        await Task.CompletedTask;
    }

    [Test]
    public async Task MergeAndDeduplicate_TracksProviderIds()
    {
        var p1 = new SearchResultSet
        {
            ProviderId = "searxng", Query = "test", IsSuccess = true,
            Results = [new SearchResult { Url = "https://x.com", Title = "X", Position = 1 }]
        };

        var p2 = new SearchResultSet
        {
            ProviderId = "brave", Query = "test", IsSuccess = true,
            Results = [new SearchResult { Url = "https://x.com", Title = "X", Position = 2 }]
        };

        var p3 = new SearchResultSet
        {
            ProviderId = "google-cse", Query = "test", IsSuccess = true,
            Results = [new SearchResult { Url = "https://x.com", Title = "X", Position = 3 }]
        };

        var merged = MultiProviderSearchService.MergeAndDeduplicate([p1, p2, p3]);

        merged.Count.ShouldBe(1);
        merged[0].ProviderCount.ShouldBe(3);
        merged[0].ProviderIds.ShouldContain("searxng");
        merged[0].ProviderIds.ShouldContain("brave");
        merged[0].ProviderIds.ShouldContain("google-cse");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MergeAndDeduplicate_OrdersByProviderCountThenPosition()
    {
        var p1 = new SearchResultSet
        {
            ProviderId = "p1", Query = "test", IsSuccess = true,
            Results =
            [
                new SearchResult { Url = "https://popular.com", Title = "Popular", Position = 5 },
                new SearchResult { Url = "https://niche.com", Title = "Niche", Position = 1 }
            ]
        };

        var p2 = new SearchResultSet
        {
            ProviderId = "p2", Query = "test", IsSuccess = true,
            Results =
            [
                new SearchResult { Url = "https://popular.com", Title = "Popular", Position = 3 }
            ]
        };

        var merged = MultiProviderSearchService.MergeAndDeduplicate([p1, p2]);

        // popular.com appears in 2 providers, niche.com in 1
        merged[0].Url.ShouldBe("https://popular.com");
        merged[1].Url.ShouldBe("https://niche.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MergeAndDeduplicate_SkipsFailedProviders()
    {
        var success = new SearchResultSet
        {
            ProviderId = "p1", Query = "test", IsSuccess = true,
            Results = [new SearchResult { Url = "https://x.com", Title = "X", Position = 1 }]
        };

        var failed = new SearchResultSet
        {
            ProviderId = "p2", Query = "test", IsSuccess = false,
            Results = [new SearchResult { Url = "https://should-ignore.com", Title = "Ignore", Position = 1 }]
        };

        var merged = MultiProviderSearchService.MergeAndDeduplicate([success, failed]);

        merged.Count.ShouldBe(1);
        merged[0].Url.ShouldBe("https://x.com");
        await Task.CompletedTask;
    }

    [Test]
    public async Task MergeAndDeduplicate_EmptyInput_ReturnsEmpty()
    {
        var merged = MultiProviderSearchService.MergeAndDeduplicate([]);
        merged.ShouldBeEmpty();
        await Task.CompletedTask;
    }

    [Test]
    public async Task MergeAndDeduplicate_CaseInsensitiveUrlDedup()
    {
        var p1 = new SearchResultSet
        {
            ProviderId = "p1", Query = "test", IsSuccess = true,
            Results = [new SearchResult { Url = "https://Example.COM/Page", Title = "Upper", Position = 1 }]
        };

        var p2 = new SearchResultSet
        {
            ProviderId = "p2", Query = "test", IsSuccess = true,
            Results = [new SearchResult { Url = "https://example.com/page", Title = "Lower", Position = 2 }]
        };

        var merged = MultiProviderSearchService.MergeAndDeduplicate([p1, p2]);

        merged.Count.ShouldBe(1);
        merged[0].ProviderCount.ShouldBe(2);
        await Task.CompletedTask;
    }

    [Test]
    public async Task SearchAllAsync_QueriesAllAvailableProviders()
    {
        var provider1 = CreateMockProvider("p1", isAvailable: true,
            results: [new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }]);
        var provider2 = CreateMockProvider("p2", isAvailable: true,
            results: [new SearchResult { Url = "https://b.com", Title = "B", Position = 1 }]);

        var sut = new MultiProviderSearchService([provider1, provider2], CreateLogger<MultiProviderSearchService>());
        var result = await sut.SearchAllAsync(new SearchQuery { Query = "test" });

        result.ProviderResults.Count.ShouldBe(2);
        result.MergedResults.Count.ShouldBe(2);
        result.Query.ShouldBe("test");
    }

    [Test]
    public async Task SearchAllAsync_SkipsUnavailableProviders()
    {
        var available = CreateMockProvider("p1", isAvailable: true,
            results: [new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }]);
        var unavailable = CreateMockProvider("p2", isAvailable: false, results: []);

        var sut = new MultiProviderSearchService([available, unavailable], CreateLogger<MultiProviderSearchService>());
        var result = await sut.SearchAllAsync(new SearchQuery { Query = "test" });

        result.ProviderResults.Count.ShouldBe(1);
        result.MergedResults.Count.ShouldBe(1);
    }

    [Test]
    public async Task SearchAllAsync_FiltersToSpecificProviderIds()
    {
        var p1 = CreateMockProvider("searxng", isAvailable: true,
            results: [new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }]);
        var p2 = CreateMockProvider("brave", isAvailable: true,
            results: [new SearchResult { Url = "https://b.com", Title = "B", Position = 1 }]);
        var p3 = CreateMockProvider("google-cse", isAvailable: true,
            results: [new SearchResult { Url = "https://c.com", Title = "C", Position = 1 }]);

        var sut = new MultiProviderSearchService([p1, p2, p3], CreateLogger<MultiProviderSearchService>());
        var result = await sut.SearchAllAsync(new SearchQuery { Query = "test" }, ["searxng", "brave"]);

        result.ProviderResults.Count.ShouldBe(2);
        result.MergedResults.ShouldNotContain(r => r.Url == "https://c.com");
    }

    [Test]
    public async Task SearchAllAsync_NoAvailableProviders_ReturnsEmpty()
    {
        var unavailable = CreateMockProvider("p1", isAvailable: false, results: []);

        var sut = new MultiProviderSearchService([unavailable], CreateLogger<MultiProviderSearchService>());
        var result = await sut.SearchAllAsync(new SearchQuery { Query = "test" });

        result.ProviderResults.ShouldBeEmpty();
        result.MergedResults.ShouldBeEmpty();
    }

    [Test]
    public async Task SearchAllAsync_ProviderThrows_GracefullyHandled()
    {
        var good = CreateMockProvider("p1", isAvailable: true,
            results: [new SearchResult { Url = "https://a.com", Title = "A", Position = 1 }]);
        var throwing = Substitute.For<ISearchProvider>();
        throwing.ProviderId.Returns("p2");
        throwing.IsAvailable.Returns(true);
        throwing.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var sut = new MultiProviderSearchService([good, throwing], CreateLogger<MultiProviderSearchService>());
        var result = await sut.SearchAllAsync(new SearchQuery { Query = "test" });

        // Should still return results from the working provider
        result.MergedResults.Count.ShouldBe(1);
        result.MergedResults[0].Url.ShouldBe("https://a.com");
    }

    [Test]
    public async Task SearchAllAsync_DeduplicatesAcrossProviders()
    {
        var p1 = CreateMockProvider("p1", isAvailable: true,
            results: [
                new SearchResult { Url = "https://shared.com", Title = "Shared", Position = 1 },
                new SearchResult { Url = "https://unique-p1.com", Title = "Unique P1", Position = 2 }
            ]);
        var p2 = CreateMockProvider("p2", isAvailable: true,
            results: [
                new SearchResult { Url = "https://shared.com", Title = "Shared", Position = 2 },
                new SearchResult { Url = "https://unique-p2.com", Title = "Unique P2", Position = 1 }
            ]);

        var sut = new MultiProviderSearchService([p1, p2], CreateLogger<MultiProviderSearchService>());
        var result = await sut.SearchAllAsync(new SearchQuery { Query = "test" });

        result.MergedResults.Count.ShouldBe(3); // shared + unique-p1 + unique-p2
        var shared = result.MergedResults.First(r => r.Url == "https://shared.com");
        shared.ProviderCount.ShouldBe(2);
    }

    private static ISearchProvider CreateMockProvider(
        string providerId, bool isAvailable, IReadOnlyList<SearchResult> results)
    {
        var provider = Substitute.For<ISearchProvider>();
        provider.ProviderId.Returns(providerId);
        provider.IsAvailable.Returns(isAvailable);
        provider.SearchAsync(Arg.Any<SearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new SearchResultSet
            {
                ProviderId = providerId,
                Query = "test",
                Results = results,
                IsSuccess = true
            });
        return provider;
    }
}
