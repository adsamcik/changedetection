using ChangeDetection.Core.Interfaces;
using ChangeDetection.Services.Search;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using TUnit.Core;

namespace ChangeDetection.Tests.Search;

[Category("Unit")]
public class RssDiscoveryServiceTests : TestBase
{
    [Test]
    public async Task DiscoverFeedsAsync_PageWithRssFeed_FindsFeed()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = """
                <html><head>
                <link rel="alternate" type="application/rss+xml" title="Blog RSS" href="/feed.xml">
                </head><body>Content</body></html>
                """,
                HttpStatusCode = 200
            });

        var sut = new RssDiscoveryService(fetcher, CreateLogger<RssDiscoveryService>());
        var result = await sut.DiscoverFeedsAsync("https://example.com");

        result.HasFeeds.ShouldBeTrue();
        result.Feeds.Count.ShouldBe(1);
        result.Feeds[0].FeedUrl.ShouldBe("https://example.com/feed.xml");
        result.Feeds[0].Title.ShouldBe("Blog RSS");
        result.Feeds[0].MimeType.ShouldContain("rss");
        result.Feeds[0].IsAtom.ShouldBeFalse();
    }

    [Test]
    public async Task DiscoverFeedsAsync_PageWithAtomFeed_FindsAtom()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = """
                <html><head>
                <link rel="alternate" type="application/atom+xml" title="Atom Feed" href="https://example.com/atom.xml">
                </head></html>
                """,
                HttpStatusCode = 200
            });

        var sut = new RssDiscoveryService(fetcher, CreateLogger<RssDiscoveryService>());
        var result = await sut.DiscoverFeedsAsync("https://example.com");

        result.HasFeeds.ShouldBeTrue();
        result.Feeds[0].IsAtom.ShouldBeTrue();
        result.Feeds[0].FeedUrl.ShouldBe("https://example.com/atom.xml");
    }

    [Test]
    public async Task DiscoverFeedsAsync_MultipleFeeds_FindsAll()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = """
                <html><head>
                <link rel="alternate" type="application/rss+xml" title="RSS" href="/rss.xml">
                <link rel="alternate" type="application/atom+xml" title="Atom" href="/atom.xml">
                </head></html>
                """,
                HttpStatusCode = 200
            });

        var sut = new RssDiscoveryService(fetcher, CreateLogger<RssDiscoveryService>());
        var result = await sut.DiscoverFeedsAsync("https://example.com");

        result.Feeds.Count.ShouldBe(2);
    }

    [Test]
    public async Task DiscoverFeedsAsync_NoFeeds_ReturnsEmpty()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult
            {
                IsSuccess = true,
                Html = "<html><head><link rel='stylesheet' href='style.css'></head><body>No feeds</body></html>",
                HttpStatusCode = 200
            });

        var sut = new RssDiscoveryService(fetcher, CreateLogger<RssDiscoveryService>());
        var result = await sut.DiscoverFeedsAsync("https://example.com");

        result.HasFeeds.ShouldBeFalse();
        result.Feeds.ShouldBeEmpty();
    }

    [Test]
    public async Task DiscoverFeedsAsync_FetchFails_ReturnsEmpty()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .Returns(new FetchResult { IsSuccess = false, HttpStatusCode = 500 });

        var sut = new RssDiscoveryService(fetcher, CreateLogger<RssDiscoveryService>());
        var result = await sut.DiscoverFeedsAsync("https://example.com");

        result.HasFeeds.ShouldBeFalse();
    }

    [Test]
    public async Task DiscoverFeedsAsync_FetcherThrows_ReturnsEmpty()
    {
        var fetcher = Substitute.For<IContentFetcher>();
        fetcher.FetchAsync(Arg.Any<string>(), Arg.Any<FetchOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        var sut = new RssDiscoveryService(fetcher, CreateLogger<RssDiscoveryService>());
        var result = await sut.DiscoverFeedsAsync("https://example.com");

        result.HasFeeds.ShouldBeFalse();
    }

    // --- Static method tests ---

    [Test]
    public async Task ParseFeedLinks_IgnoresNonAlternateLinks()
    {
        var html = """
        <link rel="stylesheet" type="text/css" href="style.css">
        <link rel="icon" type="image/png" href="favicon.png">
        <link rel="alternate" type="application/rss+xml" href="/feed.xml">
        """;

        var feeds = RssDiscoveryService.ParseFeedLinks(html, "https://example.com");
        feeds.Count.ShouldBe(1);
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseFeedLinks_IgnoresNonFeedTypes()
    {
        var html = """
        <link rel="alternate" type="text/html" href="/en/">
        <link rel="alternate" type="application/rss+xml" href="/feed.xml">
        """;

        var feeds = RssDiscoveryService.ParseFeedLinks(html, "https://example.com");
        feeds.Count.ShouldBe(1);
        feeds[0].FeedUrl.ShouldContain("feed.xml");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseFeedLinks_ResolvesRelativeUrls()
    {
        var html = """<link rel="alternate" type="application/rss+xml" href="/blog/feed">""";

        var feeds = RssDiscoveryService.ParseFeedLinks(html, "https://example.com/page");
        feeds.Count.ShouldBe(1);
        feeds[0].FeedUrl.ShouldBe("https://example.com/blog/feed");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ParseFeedLinks_HandlesAbsoluteUrls()
    {
        var html = """<link rel="alternate" type="application/atom+xml" href="https://other.com/feed.xml">""";

        var feeds = RssDiscoveryService.ParseFeedLinks(html, "https://example.com");
        feeds.Count.ShouldBe(1);
        feeds[0].FeedUrl.ShouldBe("https://other.com/feed.xml");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolveUrl_AbsoluteUrl_ReturnsAsIs()
    {
        var result = RssDiscoveryService.ResolveUrl("https://example.com/feed.xml", "https://base.com");
        result.ShouldBe("https://example.com/feed.xml");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolveUrl_RelativeUrl_ResolvesAgainstBase()
    {
        var result = RssDiscoveryService.ResolveUrl("/feed.xml", "https://example.com/page");
        result.ShouldBe("https://example.com/feed.xml");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ResolveUrl_InvalidUrl_ReturnsNull()
    {
        var result = RssDiscoveryService.ResolveUrl(":::invalid:::", "also-invalid");
        result.ShouldBeNull();
        await Task.CompletedTask;
    }
}
