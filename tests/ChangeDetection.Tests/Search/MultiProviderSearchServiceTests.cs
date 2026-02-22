using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
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
}
